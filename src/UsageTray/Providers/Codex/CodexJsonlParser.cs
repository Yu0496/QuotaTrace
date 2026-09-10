using System.Text.Json;
using UsageTray.Core;
using UsageTray.Providers;

namespace UsageTray.Providers.Codex;

public sealed class CodexJsonlParser
{
    public const int ParserVersion = 12;

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
        var latestQuotas = new Dictionary<string, (DateTimeOffset Time, QuotaSnapshot Snapshot)>(StringComparer.OrdinalIgnoreCase);
        var allQuotas = new List<QuotaSnapshot>();
        var lastSeenQuotas = new Dictionary<string, (double? Remaining, DateTimeOffset? ResetAt, DateTimeOffset CapturedAt)>(StringComparer.OrdinalIgnoreCase);
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
                    var currentPlan = NormalizePlanTier(JsonValueReader.FindString(root, "plan_type", "planType", "plan_tier", "planTier"));
                    if (!string.IsNullOrWhiteSpace(currentPlan)) planTier = currentPlan;
                    var extractedQuotas = ExtractRateLimits(root, timestamp, planTier, model ?? lastModel);
                    if (extractedQuotas is { Count: > 0 })
                    {
                        foreach (var kvp in extractedQuotas)
                        {
                            var snap = kvp.Value;
                            var quotaKey = $"{snap.ModelOrPoolId}_{snap.WindowKind}";
                            if (!latestQuotas.TryGetValue(quotaKey, out var existing) || timestamp >= existing.Time)
                            {
                                latestQuotas[quotaKey] = (timestamp, snap);
                            }

                            // 保留关键时序快照：首见快照、额度变化 >= 0.5%、重置时刻改变或时间跨度 >= 2小时
                            if (!lastSeenQuotas.TryGetValue(quotaKey, out var prev))
                            {
                                allQuotas.Add(snap);
                                lastSeenQuotas[quotaKey] = (snap.RemainingFraction, snap.ResetAt, snap.CapturedAt);
                            }
                            else
                            {
                                var remChanged = Math.Abs((snap.RemainingFraction ?? 0.0) - (prev.Remaining ?? 0.0)) >= 0.005;
                                var resetChanged = snap.ResetAt != prev.ResetAt;
                                var timePassed = (snap.CapturedAt - prev.CapturedAt).TotalHours >= 2.0;
                                if (remChanged || resetChanged || timePassed)
                                {
                                    allQuotas.Add(snap);
                                    lastSeenQuotas[quotaKey] = (snap.RemainingFraction, snap.ResetAt, snap.CapturedAt);
                                }
                            }
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

        // 确保会话文件最后一行的最新配额快照必然被收录
        foreach (var (key, (time, snap)) in latestQuotas)
        {
            if (lastSeenQuotas.TryGetValue(key, out var seen) && seen.CapturedAt != time)
            {
                allQuotas.Add(snap);
            }
        }

        var normalized = _normalizer.Normalize(snapshots);
        warnings.AddRange(normalized.Warnings);
        warningCount += normalized.Warnings.Count;
        var quotaList = allQuotas.Count > 0
            ? allQuotas.OrderBy(item => item.ModelOrPoolId).ThenBy(item => item.WindowKind).ThenBy(item => item.CapturedAt).ToList()
            : latestQuotas.Values.Select(v => v.Snapshot).OrderBy(item => item.ModelOrPoolId).ThenBy(item => item.WindowKind).ToList();
        return new CodexParseResult(normalized.Buckets, sessionId, ProjectResolver.Normalize(projectPath), lastModel,
            warningCount, snapshots.Count > 0, coverageStart, warnings,
            quotaList, snapshots, normalized);
    }

    private static string? FindSessionId(JsonElement root)
    {
        foreach (var item in JsonValueReader.EnumerateObjects(root))
        {
            if (!JsonValueReader.TryGetString(item, out var type, "type") ||
                !string.Equals(type, "session_meta", StringComparison.OrdinalIgnoreCase)) continue;
            if (JsonValueReader.TryGetProperty(item, out var payload, "payload") &&
                JsonValueReader.TryGetString(payload, out var id, "id", "session_id", "sessionId", "conversation_id", "conversationId")) return id;
            if (JsonValueReader.TryGetString(item, out id, "id", "session_id", "sessionId", "conversation_id", "conversationId")) return id;
        }

        return JsonValueReader.FindConversationId(root);
    }

    private static bool TryReadTokenEvent(JsonElement root, out ParsedTokenEvent result)
    {
        // 显式忽略非用量事件与内部执行跟踪记录：
        // 1. token_usage_record：Codex CLI v0.153.3+ 为每个 Turn 输出的单次内部追踪，随后紧跟权威 token_count。
        // 2. compacted / context_compacted：上下文压缩事件，其 replacement_history 包含历史对话文本或工具结果，
        //    若落入旧日志 fallback 会错误解析为缺失 last_token_usage 的伪用量，导致请求形状不确定性（HasRequestShapeUncertainty=true）并触发破折号熔断。
        if (JsonValueReader.TryGetString(root, out var rootType, "type"))
        {
            if (string.Equals(rootType, "token_usage_record", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rootType, "compacted", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rootType, "context_compacted", StringComparison.OrdinalIgnoreCase))
            {
                result = default!;
                return false;
            }
        }

        foreach (var item in JsonValueReader.EnumerateObjects(root))
        {
            if (JsonValueReader.TryGetString(item, out var recordType, "type") &&
                (string.Equals(recordType, "token_usage_record", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(recordType, "compacted", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(recordType, "context_compacted", StringComparison.OrdinalIgnoreCase)))
            {
                result = default!;
                return false;
            }
        }

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

    private static Dictionary<string, QuotaSnapshot>? ExtractRateLimits(JsonElement root, DateTimeOffset capturedAt, string? planTier, string? currentModel)
    {
        Dictionary<string, QuotaSnapshot>? result = null;
        var isReserveModel = currentModel?.Contains("reserve", StringComparison.OrdinalIgnoreCase) == true;

        foreach (var item in JsonValueReader.EnumerateObjects(root))
        {
            if (!JsonValueReader.TryGetProperty(item, out var rateLimits, "rate_limits", "rateLimits") || rateLimits.ValueKind != JsonValueKind.Object) continue;
            result ??= new Dictionary<string, QuotaSnapshot>(StringComparer.OrdinalIgnoreCase);

            var rawPlan = JsonValueReader.FindString(rateLimits, "plan_type", "planType", "plan_tier", "planTier") ?? planTier;
            var effectivePlan = NormalizePlanTier(rawPlan);

            var isProOrTeam = effectivePlan != null && (
                effectivePlan.Contains("pro", StringComparison.OrdinalIgnoreCase) ||
                effectivePlan.Contains("team", StringComparison.OrdinalIgnoreCase) ||
                effectivePlan.Contains("ent", StringComparison.OrdinalIgnoreCase));

            var limitId = JsonValueReader.FindString(rateLimits, "limit_id", "limitId");
            var limitName = JsonValueReader.FindString(rateLimits, "limit_name", "limitName");

            var explicitPool = string.Equals(limitId, "base_model_inference", StringComparison.OrdinalIgnoreCase)
                ? CodexQuotaPools.FromLimitId(limitName) : CodexQuotaPools.FromLimitId(limitId);
            if (explicitPool == "unknown") continue;
            var isSparkModel = explicitPool is not null ? explicitPool == "spark" :
                CodexQuotaPools.IsSparkModel(limitName) || CodexQuotaPools.IsSparkModel(currentModel);
            var isReserve = explicitPool is not null ? explicitPool == "reserve" :
                CodexQuotaPools.IsReserveModel(limitName) || isReserveModel;

            // Observed Pro/ProLite Spark logs can report the generic codex id with
            // Spark's 5h + weekly pair; the main pool reports a single weekly window.
            if (explicitPool == "standard" && isProOrTeam && CodexQuotaPools.IsSparkModel(currentModel) &&
                JsonValueReader.TryGetProperty(rateLimits, out var sparkPrimary, "primary") && sparkPrimary.ValueKind == JsonValueKind.Object &&
                JsonValueReader.GetLong(sparkPrimary, "window_minutes", "windowMinutes", "window_duration_mins", "windowDurationMins") == 300 &&
                JsonValueReader.TryGetProperty(rateLimits, out var sparkSecondary, "secondary") && sparkSecondary.ValueKind == JsonValueKind.Object &&
                JsonValueReader.GetLong(sparkSecondary, "window_minutes", "windowMinutes", "window_duration_mins", "windowDurationMins") == 10080)
                isSparkModel = true;

            // Observed legacy Reserve: limit_id=codex, current model=gpt-reserve,
            // one weekly primary and no secondary. Standard ids otherwise win over current model.
            if (explicitPool == "standard" && isReserveModel &&
                (!JsonValueReader.TryGetProperty(rateLimits, out var reserveSecondary, "secondary") || reserveSecondary.ValueKind != JsonValueKind.Object) &&
                JsonValueReader.TryGetProperty(rateLimits, out var reservePrimary, "primary") && reservePrimary.ValueKind == JsonValueKind.Object &&
                JsonValueReader.GetLong(reservePrimary, "window_minutes", "windowMinutes", "window_duration_mins", "windowDurationMins") == 10080)
                isReserve = true;

            // Compatibility with legacy Plus logs that omit a pool id.
            if (explicitPool is null && !isReserve && !isSparkModel &&
                string.Equals(effectivePlan, "plus", StringComparison.OrdinalIgnoreCase) &&
                (!JsonValueReader.TryGetProperty(rateLimits, out var sec, "secondary") || sec.ValueKind != JsonValueKind.Object) &&
                JsonValueReader.TryGetProperty(rateLimits, out var prim, "primary") && prim.ValueKind == JsonValueKind.Object &&
                JsonValueReader.GetLong(prim, "window_minutes", "windowMinutes", "window_duration_mins", "windowDurationMins") == 10080)
                isReserve = true;

            if (isReserve)
            {
                if (JsonValueReader.TryGetProperty(rateLimits, out var window, "primary") && window.ValueKind == JsonValueKind.Object)
                {
                    var usedPercent = JsonValueReader.GetDouble(window, "used_percent", "usedPercent");
                    if (usedPercent.HasValue && usedPercent.Value is >= 0 and <= 100)
                    {
                        var snapshot = new QuotaSnapshot(ProviderKind.Codex, capturedAt, "codex-reserve", "Codex Reserve",
                            1d - usedPercent.Value / 100d, ReadReset(window, capturedAt), "weekly", "codex-session-rate-limits", effectivePlan ?? planTier);
                        result["codex-reserve"] = snapshot;
                    }
                }
            }
            else
            {
                foreach (var windowName in new[] { "primary", "secondary" })
                {
                    if (!JsonValueReader.TryGetProperty(rateLimits, out var window, windowName) || window.ValueKind != JsonValueKind.Object) continue;
                    var usedPercent = JsonValueReader.GetDouble(window, "used_percent", "usedPercent");
                    if (!usedPercent.HasValue || usedPercent.Value is < 0 or > 100) continue;
                    var minutes = JsonValueReader.GetLong(window, "window_minutes", "windowMinutes", "window_duration_mins", "windowDurationMins");
                    var kind = ClassifyWindow(windowName, minutes, isProOrTeam, isSparkModel);

                    string modelOrPoolId;
                    string displayLabel;
                    if (isSparkModel)
                    {
                        modelOrPoolId = $"codex-spark-{kind}";
                        displayLabel = kind switch
                        {
                            "5h" => "GPT-5.3 Spark (5小时额度)",
                            "weekly" => "GPT-5.3 Spark (周额度)",
                            _ => $"GPT-5.3 Spark ({kind})"
                        };
                    }
                    else
                    {
                        modelOrPoolId = $"codex-{kind}";
                        displayLabel = kind switch
                        {
                            "5h" => "Codex 主力模型 (5小时额度)",
                            "weekly" => "Codex 主力模型 (周额度)",
                            _ => $"Codex 主力模型 ({kind})"
                        };
                    }

                    var snapshot = new QuotaSnapshot(ProviderKind.Codex, capturedAt, modelOrPoolId, displayLabel,
                        1d - usedPercent.Value / 100d, ReadReset(window, capturedAt), kind, "codex-session-rate-limits", effectivePlan ?? planTier);
                    result[$"{modelOrPoolId}_{kind}"] = snapshot;
                }
            }
        }
        return result;
    }

    private static string ClassifyWindow(string windowName, long? minutes, bool isProOrTeam = false, bool isSpark = false) => minutes switch
    {
        300 => "5h",
        10080 => "weekly",
        > 0 => $"{minutes}m",
        _ when isSpark && string.Equals(windowName, "primary", StringComparison.OrdinalIgnoreCase) => "5h",
        _ when isSpark && string.Equals(windowName, "secondary", StringComparison.OrdinalIgnoreCase) => "weekly",
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
        if (JsonValueReader.GetLong(window, "resets_in_seconds", "resetsInSeconds") is { } seconds && seconds >= 0)
        {
            try { return capturedAt.AddSeconds(seconds); }
            catch (ArgumentOutOfRangeException) { }
        }
        if (JsonValueReader.TryGetString(window, out var text, "resets_at", "resetsAt") && DateTimeOffset.TryParse(text, out var parsed)) return parsed;
        return null;
    }

    private static string? NormalizeModel(string? model) => string.IsNullOrWhiteSpace(model) ? null : model.Trim();

    private static string? NormalizePlanTier(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        return trimmed.ToLowerInvariant() switch
        {
            "prolite" => "ProLite",
            "plus" => "Plus",
            "pro" => "Pro",
            "team" => "Team",
            "enterprise" => "Enterprise",
            _ => trimmed
        };
    }

    private sealed record ParsedTokenEvent(CodexCumulativeUsage Total, CodexRequestUsage? Last, long? ContextWindow, string? ServiceTier, string EventType);
}
