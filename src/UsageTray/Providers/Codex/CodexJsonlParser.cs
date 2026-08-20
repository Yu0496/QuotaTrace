using System.Text.Json;
using UsageTray.Core;
using UsageTray.Providers;

namespace UsageTray.Providers.Codex;

public sealed class CodexJsonlParser
{
    public const int ParserVersion = 3;

    private static readonly string[] InputNames = ["input_tokens", "inputTokens", "prompt_tokens", "promptTokens", "input", "total_input_tokens", "totalInputTokens"];
    private static readonly string[] CachedNames = ["cached_input_tokens", "cachedInputTokens", "cache_read_input_tokens", "cacheReadInputTokens", "cached", "cache_read"];
    private static readonly string[] CacheWriteNames = ["cache_creation_input_tokens", "cacheCreationInputTokens", "cache_write_input_tokens", "cacheWriteInputTokens", "cache_write"];
    private static readonly string[] OutputNames = ["output_tokens", "outputTokens", "completion_tokens", "completionTokens", "output", "total_output_tokens", "totalOutputTokens"];
    private static readonly string[] ReasoningNames = ["reasoning_output_tokens", "reasoningOutputTokens", "reasoning_tokens", "reasoningTokens"];

    private readonly CodexUsageNormalizer _normalizer;

    public CodexJsonlParser(CodexUsageNormalizer? normalizer = null) => _normalizer = normalizer ?? new CodexUsageNormalizer();

    public CodexParseResult ParseFile(string path)
    {
        var snapshots = new List<CodexTokenSnapshot>();
        Dictionary<string, QuotaSnapshot>? latestQuotas = null;
        DateTimeOffset? latestQuotaTime = null;
        var warnings = new List<string>();
        string? sessionId = null;
        string? projectPath = null;
        string? serviceTier = null;
        string? lastModel = null;
        string? planTier = null;
        var warningCount = 0;
        DateTimeOffset? coverageStart = null;
        var lineNumber = 0;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    sessionId ??= FindSessionId(root) ?? Path.GetFileNameWithoutExtension(path);
                    var currentProject = JsonValueReader.FindProjectPath(root);
                    if (!string.IsNullOrWhiteSpace(currentProject)) projectPath = currentProject;
                    var currentTier = JsonValueReader.FindString(root, "service_tier", "serviceTier", "tier");
                    if (!string.IsNullOrWhiteSpace(currentTier)) serviceTier = currentTier;
                    var model = NormalizeModel(JsonValueReader.FindModel(root));
                    if (!string.IsNullOrWhiteSpace(model)) lastModel = model;
                    var timestamp = JsonValueReader.FindTimestamp(root) ?? File.GetLastWriteTimeUtc(path);
                    coverageStart = coverageStart is null || timestamp < coverageStart ? timestamp : coverageStart;
                    planTier ??= JsonValueReader.FindString(root, "plan_type", "planType", "plan_tier", "planTier");
                    var extractedQuotas = ExtractRateLimits(root, timestamp, planTier);
                    if (extractedQuotas is { Count: > 0 })
                    {
                        if (latestQuotaTime is null || timestamp >= latestQuotaTime)
                        {
                            latestQuotas = extractedQuotas;
                            latestQuotaTime = timestamp;
                        }
                    }

                    if (!TryReadTokenEvent(root, out var tokenEvent)) continue;
                    var effectiveSession = sessionId ?? Path.GetFileNameWithoutExtension(path);
                    var snapshot = new CodexTokenSnapshot(effectiveSession, timestamp, model ?? lastModel,
                        ProjectResolver.Normalize(projectPath), currentTier ?? serviceTier ?? tokenEvent.ServiceTier, tokenEvent.Total, tokenEvent.Last,
                        tokenEvent.ContextWindow, path, lineNumber, tokenEvent.EventType);
                    snapshots.Add(snapshot);
                }
                catch (JsonException)
                {
                    warningCount++;
                    if (warnings.Count < 20) warnings.Add($"Codex {Path.GetFileName(path)} 第 {lineNumber} 行 JSON 损坏，已跳过。");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warningCount++;
            warnings.Add($"无法读取 Codex 文件：{path}（{exception.Message}）");
        }

        var normalized = _normalizer.Normalize(snapshots);
        warnings.AddRange(normalized.Warnings);
        warningCount += normalized.Warnings.Count;
        var quotaList = latestQuotas?.Values.OrderBy(item => item.WindowKind).ToList() ?? [];
        return new CodexParseResult(normalized.Buckets, sessionId, ProjectResolver.Normalize(projectPath), lastModel,
            warningCount, snapshots.Count > 0, coverageStart, warnings,
            quotaList, snapshots, normalized);
    }

    private static string? FindSessionId(JsonElement root)
    {
        var direct = JsonValueReader.FindConversationId(root);
        if (!string.IsNullOrWhiteSpace(direct)) return direct;
        foreach (var item in JsonValueReader.EnumerateObjects(root))
        {
            if (!JsonValueReader.TryGetString(item, out var type, "type") ||
                !string.Equals(type, "session_meta", StringComparison.OrdinalIgnoreCase)) continue;
            if (JsonValueReader.TryGetString(item, out var id, "id", "session_id", "sessionId", "conversation_id", "conversationId")) return id;
            if (JsonValueReader.TryGetProperty(item, out var payload, "payload") &&
                JsonValueReader.TryGetString(payload, out id, "id", "session_id", "sessionId", "conversation_id", "conversationId")) return id;
        }
        return null;
    }

    private static bool TryReadTokenEvent(JsonElement root, out ParsedTokenEvent result)
    {
        foreach (var item in JsonValueReader.EnumerateObjects(root))
        {
            if (!JsonValueReader.TryGetString(item, out var type, "type") ||
                !string.Equals(type, "token_count", StringComparison.OrdinalIgnoreCase)) continue;
            var info = item;
            if (JsonValueReader.TryGetProperty(item, out var infoObject, "info") && infoObject.ValueKind == JsonValueKind.Object)
                info = infoObject;
            if (!TryGetNamedUsage(info, out var total, "total_token_usage", "totalTokenUsage") && !TryParseUsage(info, out total)) continue;
            CodexRequestUsage? last = null;
            if (TryGetNamedUsage(info, out var lastUsage, "last_token_usage", "lastTokenUsage")) last = ToRequestUsage(lastUsage);
            var context = JsonValueReader.GetLong(info, "model_context_window", "modelContextWindow", "context_window", "contextWindow");
            var tier = JsonValueReader.FindString(info, "service_tier", "serviceTier", "tier");
            result = new ParsedTokenEvent(total, last, context, tier, "token_count");
            return true;
        }

        // Older fixtures and exported logs may have a cumulative usage object without a token_count type.
        foreach (var item in JsonValueReader.EnumerateObjects(root))
        {
            if (!TryGetNamedUsage(item, out var total, "total_token_usage", "totalTokenUsage")) continue;
            CodexRequestUsage? last = null;
            if (TryGetNamedUsage(item, out var lastUsage, "last_token_usage", "lastTokenUsage")) last = ToRequestUsage(lastUsage);
            result = new ParsedTokenEvent(total, last, JsonValueReader.GetLong(item, "model_context_window", "modelContextWindow"), null, "token_count");
            return true;
        }

        foreach (var item in JsonValueReader.EnumerateObjects(root))
        {
            if (!TryParseUsage(item, out var total)) continue;
            var isExplicitUsage = JsonValueReader.HasAny(item, "input_tokens", "inputTokens", "output_tokens", "outputTokens", "prompt_tokens", "completion_tokens");
            if (!isExplicitUsage) continue;
            result = new ParsedTokenEvent(total, null, null, null, "usage");
            return true;
        }
        result = default!;
        return false;
    }

    private static bool TryGetNamedUsage(JsonElement parent, out CodexCumulativeUsage usage, params string[] names)
    {
        if (JsonValueReader.TryGetProperty(parent, out var value, names) && value.ValueKind == JsonValueKind.Object)
            return TryParseUsage(value, out usage);
        usage = default!;
        return false;
    }

    private static bool TryParseUsage(JsonElement item, out CodexCumulativeUsage usage)
    {
        var input = JsonValueReader.GetLong(item, InputNames);
        var output = JsonValueReader.GetLong(item, OutputNames);
        if (!input.HasValue && !output.HasValue)
        {
            usage = default!;
            return false;
        }
        usage = new CodexCumulativeUsage(Math.Max(0, input ?? 0), Math.Max(0, JsonValueReader.GetLong(item, CachedNames) ?? 0),
            Math.Max(0, output ?? 0), JsonValueReader.GetLong(item, CacheWriteNames), JsonValueReader.GetLong(item, ReasoningNames));
        return true;
    }

    private static CodexRequestUsage ToRequestUsage(CodexCumulativeUsage usage) =>
        new(usage.InputTokens, usage.CachedInputTokens, usage.OutputTokens, usage.CacheWriteInputTokens, usage.ReasoningOutputTokens);

    private static Dictionary<string, QuotaSnapshot>? ExtractRateLimits(JsonElement root, DateTimeOffset capturedAt, string? planTier)
    {
        Dictionary<string, QuotaSnapshot>? result = null;
        foreach (var item in JsonValueReader.EnumerateObjects(root))
        {
            if (!JsonValueReader.TryGetProperty(item, out var rateLimits, "rate_limits", "rateLimits") || rateLimits.ValueKind != JsonValueKind.Object) continue;
            result ??= new Dictionary<string, QuotaSnapshot>(StringComparer.OrdinalIgnoreCase);
            foreach (var windowName in new[] { "primary", "secondary" })
            {
                if (!JsonValueReader.TryGetProperty(rateLimits, out var window, windowName) || window.ValueKind != JsonValueKind.Object) continue;
                var usedPercent = JsonValueReader.GetDouble(window, "used_percent", "usedPercent");
                if (!usedPercent.HasValue || usedPercent.Value is < 0 or > 100) continue;
                var minutes = JsonValueReader.GetLong(window, "window_minutes", "windowMinutes", "window_duration_mins", "windowDurationMins");
                var kind = ClassifyWindow(windowName, minutes);
                var snapshot = new QuotaSnapshot(ProviderKind.Codex, capturedAt, $"codex-{kind}", $"Codex {kind}",
                    1d - usedPercent.Value / 100d, ReadReset(window, capturedAt), kind, "codex-session-rate-limits", planTier);
                result[kind] = snapshot;
            }
        }
        return result;
    }

    private static string ClassifyWindow(string windowName, long? minutes) => minutes switch
    {
        <= 360 and > 0 => "5h",
        >= 10_000 => "weekly",
        > 0 => $"{minutes}m",
        _ when string.Equals(windowName, "primary", StringComparison.OrdinalIgnoreCase) => "5h",
        _ when string.Equals(windowName, "secondary", StringComparison.OrdinalIgnoreCase) => "weekly",
        _ => "unknown"
    };

    private static DateTimeOffset? ReadReset(JsonElement window, DateTimeOffset capturedAt)
    {
        var reset = JsonValueReader.GetLong(window, "resets_at", "resetsAt");
        if (reset.HasValue)
        {
            try { return reset.Value > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(reset.Value) : DateTimeOffset.FromUnixTimeSeconds(reset.Value); }
            catch (ArgumentOutOfRangeException) { }
        }
        if (JsonValueReader.GetLong(window, "resets_in_seconds", "resetsInSeconds") is { } seconds && seconds >= 0) return capturedAt.AddSeconds(seconds);
        if (JsonValueReader.TryGetString(window, out var text, "resets_at", "resetsAt") && DateTimeOffset.TryParse(text, out var parsed)) return parsed;
        return null;
    }

    private static string? NormalizeModel(string? model) => string.IsNullOrWhiteSpace(model) ? null : model.Trim();

    private sealed record ParsedTokenEvent(CodexCumulativeUsage Total, CodexRequestUsage? Last, long? ContextWindow, string? ServiceTier, string EventType);
}
