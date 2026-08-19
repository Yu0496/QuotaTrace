using System.Text.Json;
using UsageTray.Core;
using UsageTray.Providers;

namespace UsageTray.Providers.Codex;

public sealed class CodexJsonlParser
{
    private static readonly string[] InputNames = ["input_tokens", "inputTokens", "prompt_tokens", "promptTokens", "input", "total_input_tokens", "totalInputTokens"];
    private static readonly string[] CachedNames = ["cached_input_tokens", "cachedInputTokens", "cache_read_input_tokens", "cacheReadInputTokens", "cached", "cache_read"];
    private static readonly string[] CacheWriteNames = ["cache_creation_input_tokens", "cacheCreationInputTokens", "cache_write_input_tokens", "cacheWriteInputTokens", "cache_write"];
    private static readonly string[] OutputNames = ["output_tokens", "outputTokens", "completion_tokens", "completionTokens", "output", "total_output_tokens", "totalOutputTokens"];

    public CodexParseResult ParseFile(string path)
    {
        var aggregate = new Dictionary<BucketKey, MutableBucket>();
        var counters = new Dictionary<string, Counter>(StringComparer.OrdinalIgnoreCase);
        var quotaByWindow = new Dictionary<string, QuotaSnapshot>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        string? sessionId = null;
        string? projectPath = null;
        string? lastModel = null;
        string? planTier = null;
        var warningCount = 0;
        var hasTokenData = false;
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
                    sessionId ??= JsonValueReader.FindConversationId(root) ?? Path.GetFileNameWithoutExtension(path);
                    projectPath ??= JsonValueReader.FindProjectPath(root);
                    var model = NormalizeModel(JsonValueReader.FindModel(root));
                    if (!string.IsNullOrWhiteSpace(model)) lastModel = model;
                    var timestamp = JsonValueReader.FindTimestamp(root) ?? File.GetLastWriteTimeUtc(path);
                    coverageStart = coverageStart is null || timestamp < coverageStart ? timestamp : coverageStart;
                    planTier ??= JsonValueReader.FindString(root, "plan_type", "planType", "plan_tier", "planTier");
                    ExtractRateLimits(root, timestamp, planTier, quotaByWindow);

                    var fields = FindTokenFields(root);
                    if (fields is null || fields.Input < 0 || fields.Output < 0) continue;
                    hasTokenData = true;
                    var effectiveModel = model ?? lastModel ?? "Unknown";
                    var usage = fields;
                    if (fields.IsCumulative)
                    {
                        var previous = counters.TryGetValue(effectiveModel, out var old) ? old : default;
                        if (fields.Input < previous.Input || fields.Output < previous.Output)
                        {
                            counters[effectiveModel] = new Counter(fields.Input, fields.Cached, fields.CacheWrite, fields.Output);
                            continue;
                        }
                        usage = fields with
                        {
                            Input = fields.Input - previous.Input,
                            Cached = Math.Max(0, fields.Cached - previous.Cached),
                            CacheWrite = Math.Max(0, fields.CacheWrite - previous.CacheWrite),
                            Output = fields.Output - previous.Output,
                            IsCumulative = false
                        };
                        counters[effectiveModel] = new Counter(fields.Input, fields.Cached, fields.CacheWrite, fields.Output);
                    }
                    if (usage.Input == 0 && usage.Output == 0 && usage.CacheWrite == 0) continue;
                    var key = new BucketKey(DateOnly.FromDateTime(timestamp.ToLocalTime().DateTime), ProjectResolver.Normalize(projectPath), effectiveModel);
                    if (!aggregate.TryGetValue(key, out var bucket)) aggregate[key] = bucket = new MutableBucket();
                    bucket.Input += usage.Input;
                    bucket.Cached += Math.Min(usage.Cached, usage.Input);
                    bucket.CacheWrite += Math.Min(usage.CacheWrite, Math.Max(0, usage.Input - usage.Cached));
                    bucket.Output += usage.Output;
                    bucket.Requests++;
                    bucket.Quality = fields.IsCumulative ? DataQuality.Derived : DataQuality.Exact;
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

        var buckets = aggregate.Select(pair => new UsageBucket(ProviderKind.Codex, pair.Key.LocalDate, pair.Key.ProjectKey, pair.Key.Model,
            pair.Value.Input, pair.Value.Cached, pair.Value.Output, pair.Value.Requests, pair.Value.Quality, path, sessionId, pair.Value.CacheWrite,
            pair.Value.Cached > 0 || pair.Value.CacheWrite > 0 ? CostQuality.ExactTokenSplit : CostQuality.ExactTokensNoCache)).ToList();
        return new CodexParseResult(buckets, sessionId, ProjectResolver.Normalize(projectPath), lastModel, warningCount, hasTokenData, coverageStart,
            warnings, quotaByWindow.Values.OrderBy(item => item.WindowKind).ToList());
    }

    private static void ExtractRateLimits(JsonElement root, DateTimeOffset capturedAt, string? planTier,
        Dictionary<string, QuotaSnapshot> quotaByWindow)
    {
        foreach (var item in JsonValueReader.EnumerateObjects(root))
        {
            if (!JsonValueReader.TryGetProperty(item, out var rateLimits, "rate_limits", "rateLimits") ||
                rateLimits.ValueKind != JsonValueKind.Object) continue;

            foreach (var windowName in new[] { "primary", "secondary" })
            {
                if (!JsonValueReader.TryGetProperty(rateLimits, out var window, windowName) ||
                    window.ValueKind != JsonValueKind.Object) continue;
                var usedPercent = JsonValueReader.GetDouble(window, "used_percent", "usedPercent");
                if (!usedPercent.HasValue || usedPercent.Value is < 0 or > 100) continue;
                var minutes = JsonValueReader.GetLong(window, "window_minutes", "windowMinutes", "window_duration_mins", "windowDurationMins");
                var kind = ClassifyWindow(windowName, minutes);
                var reset = ReadReset(window, capturedAt);
                var snapshot = new QuotaSnapshot(
                    ProviderKind.Codex,
                    capturedAt,
                    $"codex-{windowName}",
                    $"Codex {kind}",
                    1d - usedPercent.Value / 100d,
                    reset,
                    kind,
                    "codex-session-rate-limits",
                    planTier);
                if (!quotaByWindow.TryGetValue(kind, out var previous) || snapshot.CapturedAt >= previous.CapturedAt)
                    quotaByWindow[kind] = snapshot;
            }
        }
    }

    private static string ClassifyWindow(string windowName, long? minutes) =>
        minutes switch
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

        if (JsonValueReader.GetLong(window, "resets_in_seconds", "resetsInSeconds") is { } seconds && seconds >= 0)
            return capturedAt.AddSeconds(seconds);
        if (JsonValueReader.TryGetString(window, out var text, "resets_at", "resetsAt") &&
            DateTimeOffset.TryParse(text, out var parsed)) return parsed;
        return null;
    }

    private static string? NormalizeModel(string? model) => string.IsNullOrWhiteSpace(model) ? null : model.Trim();

    private static TokenFields? FindTokenFields(JsonElement root)
    {
        TokenFields? best = null;
        foreach (var item in JsonValueReader.EnumerateObjects(root))
        {
            var input = JsonValueReader.GetLong(item, InputNames);
            var output = JsonValueReader.GetLong(item, OutputNames);
            if (!input.HasValue && !output.HasValue) continue;
            var candidate = new TokenFields(Math.Max(0, input ?? 0), Math.Max(0, JsonValueReader.GetLong(item, CachedNames) ?? 0), Math.Max(0, JsonValueReader.GetLong(item, CacheWriteNames) ?? 0), Math.Max(0, output ?? 0), IsCumulativeObject(item, root), ScoreObject(item));
            if (best is null || candidate.Score > best.Score) best = candidate;
        }
        return best;
    }

    private static bool IsCumulativeObject(JsonElement item, JsonElement root)
    {
        if (JsonValueReader.HasAny(item, "total_input_tokens", "totalInputTokens", "total_output_tokens", "totalOutputTokens", "total_token_usage", "totalTokenUsage")) return true;
        foreach (var candidate in JsonValueReader.EnumerateObjects(root))
        {
            if (JsonValueReader.TryGetString(candidate, out var type, "type", "event_type", "eventType") && type?.Contains("token_count", StringComparison.OrdinalIgnoreCase) == true) return true;
        }
        return false;
    }

    private static int ScoreObject(JsonElement item)
    {
        var score = 0;
        if (JsonValueReader.HasAny(item, "total_input_tokens", "totalInputTokens", "total_output_tokens", "totalOutputTokens")) score += 10;
        if (JsonValueReader.HasAny(item, "input_tokens", "output_tokens", "cached_input_tokens")) score += 5;
        if (JsonValueReader.HasAny(item, "usage", "total_token_usage", "token_count")) score += 2;
        return score;
    }

    private readonly record struct BucketKey(DateOnly LocalDate, string? ProjectKey, string Model);
    private sealed class MutableBucket
    {
        public long Input;
        public long Cached;
        public long CacheWrite;
        public long Output;
        public int Requests;
        public DataQuality Quality = DataQuality.Exact;
    }
    private readonly record struct Counter(long Input, long Cached, long CacheWrite, long Output);
    private sealed record TokenFields(long Input, long Cached, long CacheWrite, long Output, bool IsCumulative, int Score);
}
