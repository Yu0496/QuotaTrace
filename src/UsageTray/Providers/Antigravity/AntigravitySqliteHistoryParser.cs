using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using UsageTray.Core;
using UsageTray.Pricing;

namespace UsageTray.Providers.Antigravity;

public sealed record AntigravitySqliteParseResult(
    IReadOnlyList<AntigravityGenerationUsage> Generations,
    IReadOnlyList<UsageBucket> Buckets,
    IReadOnlyList<string> Warnings
);

public sealed class AntigravitySqliteHistoryParser
{
    public AntigravitySqliteParseResult ParseFile(string path, AntigravityProjectResolver? projectResolver = null, IReadOnlyList<PricingRule>? pricingRules = null)
    {
        var generations = new List<AntigravityGenerationUsage>();
        var warnings = new List<string>();
        var conversationId = Path.GetFileNameWithoutExtension(path);

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared
            }.ToString();

            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using (var pragmaCmd = connection.CreateCommand())
            {
                pragmaCmd.CommandText = "PRAGMA cache_size=-2000;";
                pragmaCmd.ExecuteNonQuery();
            }

            // 1. Read step timestamps
            var stepTimes = new Dictionary<int, DateTimeOffset>();
            try
            {
                using var stepCmd = connection.CreateCommand();
                stepCmd.CommandText = "SELECT idx, metadata FROM steps WHERE metadata IS NOT NULL;";
                using var stepReader = stepCmd.ExecuteReader();
                while (stepReader.Read())
                {
                    var idx = stepReader.GetInt32(0);
                    var metaBlob = (byte[])stepReader.GetValue(1);
                    var ts = AntigravityProtobufReader.ParseStepTimestamp(metaBlob);
                    if (ts.HasValue) stepTimes[idx] = ts.Value;
                }
            }
            catch { }

            // 2. Resolve project key
            var projectKey = projectResolver?.ResolveProjectKey(conversationId, connection);

            // 3. Fallback timestamp sources
            var summary = projectResolver?.GetSummary(conversationId);
            var fallbackTime = summary is not null && summary.LastModifiedTime > DateTimeOffset.MinValue
                ? summary.LastModifiedTime
                : new DateTimeOffset(File.GetLastWriteTimeUtc(path));

            // 4. Read gen_metadata
            using var genCmd = connection.CreateCommand();
            genCmd.CommandText = "SELECT idx, data FROM gen_metadata ORDER BY idx;";
            using var reader = genCmd.ExecuteReader();
            while (reader.Read())
            {
                var idx = reader.GetInt32(0);
                var blob = (byte[])reader.GetValue(1);
                var parsed = AntigravityProtobufReader.ParseGenerationMetadata(blob, idx);
                if (parsed is null) continue;

                var isExactTime = stepTimes.TryGetValue(idx, out var timestamp);
                if (!isExactTime) timestamp = fallbackTime;

                var model = !string.IsNullOrWhiteSpace(parsed.ResponseModel) && !string.Equals(parsed.ResponseModel.Trim(), "Unknown", StringComparison.OrdinalIgnoreCase)
                    ? parsed.ResponseModel.Trim()
                    : InferModelFromDisplayName(parsed.DisplayName);
                var gen = new AntigravityGenerationUsage(
                    conversationId,
                    parsed.GenerationId,
                    parsed.ResponseId,
                    timestamp,
                    model,
                    parsed.DisplayName,
                    parsed.InputTokens,
                    parsed.CacheReadTokens,
                    parsed.CacheWriteTokens,
                    parsed.ThinkingOutputTokens,
                    parsed.ResponseOutputTokens,
                    parsed.AggregateOutputTokens,
                    projectKey,
                    path,
                    idx,
                    isExactTime ? DataQuality.Exact : DataQuality.Derived
                );
                generations.Add(gen);
            }
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            warnings.Add($"无法以只读方式读取 Antigravity SQLite：{path}（{exception.Message}）");
        }

        var buckets = ConvertToBuckets(generations, path, pricingRules);
        return new AntigravitySqliteParseResult(generations, buckets, warnings);
    }

    public static string InferModelFromDisplayName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return "Unknown";
        var name = displayName.Trim().ToLowerInvariant();

        if (name.Contains("sonnet")) return "claude-sonnet-4-6";
        if (name.Contains("opus")) return "claude-opus-4-6-thinking";
        if (name.Contains("haiku")) return "claude-3-5-haiku";

        if (name.Contains("3.7") && name.Contains("flash")) return "gemini-3.7-flash";
        if (name.Contains("3.6") && name.Contains("flash")) return "gemini-3.6-flash";
        if (name.Contains("3.5") && name.Contains("lite")) return "gemini-3.5-flash-lite";
        if (name.Contains("3.5") && name.Contains("flash")) return "gemini-3.5-flash";
        if (name.Contains("3.1") && name.Contains("lite")) return "gemini-3.1-flash-lite";
        if (name.Contains("3.1") && name.Contains("pro")) return "gemini-3.1-pro";
        if (name.Contains("2.5") && name.Contains("pro")) return "gemini-2.5-pro";
        if (name.Contains("2.5") && name.Contains("flash")) return "gemini-2.5-flash";
        if (name.Contains("gemini") && name.Contains("pro")) return "gemini-3.1-pro";
        if (name.Contains("gemini") && name.Contains("flash")) return "gemini-3.5-flash";

        if (name.Contains("gpt-oss") || name.Contains("120b")) return "gpt-oss-120b-medium";

        return displayName.Trim();
    }

    public static IReadOnlyList<UsageBucket> ConvertToBuckets(IEnumerable<AntigravityGenerationUsage> generations, string sourcePath, IReadOnlyList<PricingRule>? pricingRules = null)
    {
        var groupMap = new Dictionary<(DateOnly Date, string? Project, string Model, string? Conv), MutableBucket>();

        foreach (var gen in generations)
        {
            var date = DateOnly.FromDateTime(gen.Timestamp.ToLocalTime().DateTime);
            var key = (date, gen.ProjectKey, gen.Model, gen.ConversationId);
            if (!groupMap.TryGetValue(key, out var acc))
            {
                groupMap[key] = acc = new MutableBucket();
            }

            var rule = pricingRules is not null
                ? PricingMatcher.Find(pricingRules, ProviderKind.Antigravity, gen.Model)
                : null;
            var isLong = rule?.LongContextPrice is not null && gen.TotalInputTokens > rule.LongContextThresholdTokens;

            acc.UncachedInput += gen.InputTokens;
            acc.CacheRead += gen.CacheReadTokens;
            acc.CacheWrite += gen.CacheWriteTokens;
            acc.Output += gen.OutputTokens;
            acc.ThinkingOutput += gen.ThinkingOutputTokens;
            acc.ResponseOutput += gen.ResponseOutputTokens;
            acc.Requests++;
            if (gen.Quality == DataQuality.Derived) acc.Quality = DataQuality.Derived;

            if (isLong)
            {
                acc.LongContextRequests++;
                acc.LongContextInput += gen.TotalInputTokens;
                acc.LongContextCached += gen.CacheReadTokens;
                acc.LongContextCacheWrite += gen.CacheWriteTokens;
                acc.LongContextOutput += gen.OutputTokens;
            }
        }

        return groupMap.Select(pair =>
        {
            var val = pair.Value;
            // Antigravity Token 语义标准化:
            // InputTokens 存储 TotalInput (Uncached + CacheRead + CacheWrite)
            // CachedInputTokens 存储 CacheRead
            // CacheWriteInputTokens 存储 CacheWrite
            // 使得 UsageBucket.NonCachedInputTokens = InputTokens - CacheRead - CacheWrite = UncachedInput (原始 input_tokens)
            // Cost 计算 = Uncached * P_input + CacheRead * P_cache_read + CacheWrite * P_cache_write + Output * P_output
            var totalInput = val.UncachedInput + val.CacheRead + val.CacheWrite;
            var costQuality = val.CacheRead > 0 || val.CacheWrite > 0 ? CostQuality.ExactTokenSplit : CostQuality.ExactTokensNoCache;

            return new UsageBucket(
                ProviderKind.Antigravity,
                pair.Key.Date,
                pair.Key.Project,
                pair.Key.Model,
                totalInput,
                val.CacheRead,
                val.Output,
                val.Requests,
                val.Quality,
                sourcePath,
                pair.Key.Conv,
                val.CacheWrite,
                costQuality,
                null,
                val.LongContextRequests,
                0,
                val.LongContextInput,
                val.LongContextCached,
                val.LongContextCacheWrite,
                val.LongContextOutput,
                true
            );
        }).ToList();
    }

    private sealed class MutableBucket
    {
        public long UncachedInput;
        public long CacheRead;
        public long CacheWrite;
        public long Output;
        public long ThinkingOutput;
        public long ResponseOutput;
        public int Requests;
        public DataQuality Quality = DataQuality.Exact;
        public int LongContextRequests;
        public long LongContextInput;
        public long LongContextCached;
        public long LongContextCacheWrite;
        public long LongContextOutput;
    }
}
