using System.Text.Json;
using UsageTray.Core;
using UsageTray.Providers;

namespace UsageTray.Providers.Antigravity;

public sealed class AntigravityHistoryParser
{
    private static readonly string[] InputNames = ["input_tokens", "inputTokens", "prompt_tokens", "promptTokens", "input", "total_input_tokens", "totalInputTokens"];
    private static readonly string[] CachedNames = ["cache_read_input_tokens", "cacheReadInputTokens", "cached_input_tokens", "cachedInputTokens", "cached", "cache_read"];
    private static readonly string[] CacheWriteNames = ["cache_creation_input_tokens", "cacheCreationInputTokens", "cache_write_input_tokens", "cacheWriteInputTokens", "cache_write"];
    private static readonly string[] OutputNames = ["output_tokens", "outputTokens", "completion_tokens", "completionTokens", "output", "total_output_tokens", "totalOutputTokens"];

    public AntigravityHistoryScanResult ParseFile(string path)
    {
        var buckets = new Dictionary<BucketKey, MutableBucket>();
        var counters = new Dictionary<string, Counter>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var hasCacheSplit = false;
        var hasModel = false;
        var hasTimestamp = false;
        var hasConversation = false;
        var hasProject = false;
        var warningCount = 0;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var document = JsonDocument.Parse(stream);
                    ParseJsonDocument(document.RootElement, path, buckets, counters, ref hasCacheSplit, ref hasModel,
                        ref hasTimestamp, ref hasConversation, ref hasProject);
                }
                catch (JsonException)
                {
                    warningCount++;
                    warnings.Add($"Antigravity {Path.GetFileName(path)} JSON 损坏，已跳过。");
                }
            }
            else
            {
                using var reader = new StreamReader(stream);
                var lineNumber = 0;
                while (reader.ReadLine() is { } line)
                {
                    lineNumber++;
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        ParseElement(document.RootElement, path, buckets, counters, ref hasCacheSplit, ref hasModel,
                            ref hasTimestamp, ref hasConversation, ref hasProject);
                    }
                    catch (JsonException)
                    {
                        warningCount++;
                        if (warnings.Count < 20) warnings.Add($"Antigravity {Path.GetFileName(path)} 第 {lineNumber} 行 JSON 损坏，已跳过。");
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warningCount++;
            warnings.Add($"无法读取 Antigravity 历史文件：{path}（{exception.Message}）");
        }

        var result = buckets.Select(pair => new UsageBucket(ProviderKind.Antigravity, pair.Key.LocalDate, pair.Key.ProjectKey, pair.Key.Model,
            pair.Value.Input, pair.Value.Cached, pair.Value.Output, pair.Value.Requests, pair.Value.Quality, path, pair.Key.ConversationId,
            pair.Value.CacheWrite, pair.Value.CostQuality)).ToList();
        return new AntigravityHistoryScanResult(result, result.Count > 0, hasCacheSplit, hasModel, hasTimestamp, hasConversation, hasProject, warningCount, warnings);
    }

    private static void ParseJsonDocument(JsonElement root, string path, Dictionary<BucketKey, MutableBucket> buckets, Dictionary<string, Counter> counters,
        ref bool hasCacheSplit, ref bool hasModel, ref bool hasTimestamp, ref bool hasConversation, ref bool hasProject)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
                ParseElement(item, path, buckets, counters, ref hasCacheSplit, ref hasModel, ref hasTimestamp, ref hasConversation, ref hasProject);
            return;
        }

        if (root.ValueKind == JsonValueKind.Object && JsonValueReader.HasAny(root, InputNames.Concat(OutputNames).ToArray()))
        {
            ParseElement(root, path, buckets, counters, ref hasCacheSplit, ref hasModel, ref hasTimestamp, ref hasConversation, ref hasProject);
            return;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            var arrayProperties = root.EnumerateObject().Where(property => property.Value.ValueKind == JsonValueKind.Array).ToList();
            if (arrayProperties.Count > 0)
            {
                foreach (var property in arrayProperties)
                foreach (var item in property.Value.EnumerateArray())
                    ParseElement(item, path, buckets, counters, ref hasCacheSplit, ref hasModel, ref hasTimestamp, ref hasConversation, ref hasProject);
                return;
            }
        }

        ParseElement(root, path, buckets, counters, ref hasCacheSplit, ref hasModel, ref hasTimestamp, ref hasConversation, ref hasProject);
    }

    private static void ParseElement(JsonElement root, string path, Dictionary<BucketKey, MutableBucket> buckets, Dictionary<string, Counter> counters,
        ref bool hasCacheSplit, ref bool hasModel, ref bool hasTimestamp, ref bool hasConversation, ref bool hasProject)
    {
        var fields = FindUsageFields(root);
        if (fields is null || fields.Input < 0 || fields.Output < 0) return;
        var timestamp = JsonValueReader.FindTimestamp(root) ?? new DateTimeOffset(File.GetLastWriteTimeUtc(path));
        var conversation = JsonValueReader.FindConversationId(root);
        var model = JsonValueReader.FindModel(root) ?? "Unknown";
        var project = ProjectResolver.Normalize(JsonValueReader.FindProjectPath(root));
        hasCacheSplit |= fields.Cached > 0 || fields.CacheWrite > 0;
        hasModel |= model != "Unknown";
        hasTimestamp = true;
        hasConversation |= !string.IsNullOrWhiteSpace(conversation);
        hasProject |= project is not null;
        var counterKey = $"{conversation}\u0000{model}";
        var effective = fields;
        if (fields.IsCumulative)
        {
            var previous = counters.TryGetValue(counterKey, out var old) ? old : default;
            if (fields.Input < previous.Input || fields.Output < previous.Output)
            {
                counters[counterKey] = new Counter(fields.Input, fields.Cached, fields.CacheWrite, fields.Output);
                return;
            }
            effective = fields with { Input = fields.Input - previous.Input, Cached = Math.Max(0, fields.Cached - previous.Cached), CacheWrite = Math.Max(0, fields.CacheWrite - previous.CacheWrite), Output = fields.Output - previous.Output, IsCumulative = false };
            counters[counterKey] = new Counter(fields.Input, fields.Cached, fields.CacheWrite, fields.Output);
        }
        if (effective.Input == 0 && effective.Output == 0 && effective.CacheWrite == 0) return;
        var key = new BucketKey(DateOnly.FromDateTime(timestamp.ToLocalTime().DateTime), project, model, conversation);
        if (!buckets.TryGetValue(key, out var bucket)) buckets[key] = bucket = new MutableBucket();
        bucket.Input += effective.Input;
        bucket.Cached += Math.Min(effective.Cached, effective.Input);
        bucket.CacheWrite += Math.Min(effective.CacheWrite, Math.Max(0, effective.Input - effective.Cached));
        bucket.Output += effective.Output;
        bucket.Requests++;
        bucket.Quality = fields.IsCumulative ? DataQuality.Derived : DataQuality.Exact;
        bucket.CostQuality = fields.IsCumulative ? CostQuality.PartialPrice : (effective.Cached > 0 || effective.CacheWrite > 0 ? CostQuality.ExactTokenSplit : CostQuality.ExactTokensNoCache);
    }

    private static TokenFields? FindUsageFields(JsonElement root)
    {
        TokenFields? best = null;
        foreach (var item in JsonValueReader.EnumerateObjects(root))
        {
            var input = JsonValueReader.GetLong(item, InputNames);
            var output = JsonValueReader.GetLong(item, OutputNames);
            if (!input.HasValue && !output.HasValue) continue;
            var candidate = new TokenFields(Math.Max(0, input ?? 0), Math.Max(0, JsonValueReader.GetLong(item, CachedNames) ?? 0), Math.Max(0, JsonValueReader.GetLong(item, CacheWriteNames) ?? 0), Math.Max(0, output ?? 0), JsonValueReader.HasAny(item, "total_input_tokens", "totalInputTokens", "total_output_tokens", "totalOutputTokens"), ScoreObject(item));
            if (best is null || candidate.Score > best.Score) best = candidate;
        }
        return best;
    }

    private static int ScoreObject(JsonElement item)
    {
        var score = 0;
        if (JsonValueReader.HasAny(item, "input_tokens", "output_tokens", "cache_read_input_tokens", "cache_creation_input_tokens")) score += 10;
        if (JsonValueReader.HasAny(item, "total_input_tokens", "total_output_tokens")) score += 8;
        if (JsonValueReader.HasAny(item, "usage", "usageMetadata", "token_usage")) score += 2;
        return score;
    }

    private readonly record struct BucketKey(DateOnly LocalDate, string? ProjectKey, string Model, string? ConversationId);
    private sealed class MutableBucket
    {
        public long Input;
        public long Cached;
        public long CacheWrite;
        public long Output;
        public int Requests;
        public DataQuality Quality = DataQuality.Exact;
        public CostQuality CostQuality = CostQuality.ExactTokensNoCache;
    }
    private readonly record struct Counter(long Input, long Cached, long CacheWrite, long Output);
    private sealed record TokenFields(long Input, long Cached, long CacheWrite, long Output, bool IsCumulative, int Score);
}
