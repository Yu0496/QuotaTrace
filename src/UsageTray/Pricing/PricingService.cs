using System.Text.Json;
using System.Text.Json.Serialization;
using UsageTray.Core;

namespace UsageTray.Pricing;

public sealed record CostCalculation(decimal? CostUsd, CostQuality Quality, string? Warning, PricingRule? Rule)
{
    public bool IsPriced => CostUsd.HasValue;
}

public sealed record AggregateCost(decimal? PricedCostUsd, long UnpricedTokens, int UnpricedBucketCount,
    CostQuality Quality, IReadOnlyList<string> Warnings);

public sealed class PricingService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string FilePath { get; }
    public PricingDocument Document { get; private set; }
    public IReadOnlyList<PricingRule> Rules => Document.Rules;

    public PricingService(string filePath, PricingDocument document)
    {
        FilePath = filePath;
        Document = document;
    }

    public static PricingService LoadOrCreate(string filePath, string? bundledDefaultPath = null)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(filePath)!);
        if (File.Exists(filePath))
        {
            try
            {
                var document = JsonSerializer.Deserialize<PricingDocument>(File.ReadAllText(filePath), JsonOptions);
                if (document is not null && document.Rules is not null) return new PricingService(filePath, document);
            }
            catch { }
        }

        PricingDocument? bundled = null;
        if (!string.IsNullOrWhiteSpace(bundledDefaultPath) && File.Exists(bundledDefaultPath))
        {
            try { bundled = JsonSerializer.Deserialize<PricingDocument>(File.ReadAllText(bundledDefaultPath), JsonOptions); }
            catch { }
        }

        var result = new PricingService(filePath, bundled ?? BuiltInDefaults());
        if (!File.Exists(filePath)) result.Save();
        return result;
    }

    public void Replace(PricingDocument document) => Document = document;

    public void Save()
    {
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(Document, JsonOptions));
        File.Move(temporary, FilePath, true);
    }

    public CostCalculation Calculate(UsageBucket bucket)
    {
        var rule = PricingMatcher.Find(Rules, bucket.Provider, bucket.ModelId);
        if (rule is null)
            return new CostCalculation(null, CostQuality.Unavailable, $"未配置模型价格：{bucket.ModelId ?? "Unknown"}", null);

        var input = Math.Max(0, bucket.InputTokens);
        var cacheRead = Math.Min(input, Math.Max(0, bucket.CachedInputTokens));
        var cacheWrite = Math.Min(Math.Max(0, input - cacheRead), Math.Max(0, bucket.CacheWriteInputTokens));
        var normalInput = Math.Max(0, input - cacheRead - cacheWrite);

        var cacheNeedsValidation = (cacheRead > 0 && rule.CacheReadPerMillionUsd.HasValue) ||
                                   (cacheWrite > 0 && rule.CacheWritePerMillionUsd.HasValue);
        var semanticsValidated = rule.CacheSplitValidated && bucket.CostQuality != CostQuality.PartialPrice;
        if (cacheNeedsValidation && !semanticsValidated)
            return new CostCalculation(null, CostQuality.PartialPrice, $"模型 {bucket.ModelId} 的缓存 token 语义尚未完成样本验证", rule);

        var cacheReadPrice = rule.CacheReadPerMillionUsd ?? rule.InputPerMillionUsd;
        var cacheWritePrice = rule.CacheWritePerMillionUsd ?? rule.InputPerMillionUsd;
        var cost = normalInput * rule.InputPerMillionUsd / 1_000_000m
                   + cacheRead * cacheReadPrice / 1_000_000m
                   + cacheWrite * cacheWritePrice / 1_000_000m
                   + Math.Max(0, bucket.OutputTokens) * rule.OutputPerMillionUsd / 1_000_000m;
        var quality = cacheRead > 0 || cacheWrite > 0 ? CostQuality.ExactTokenSplit : CostQuality.ExactTokensNoCache;
        return new CostCalculation(cost, quality, null, rule);
    }

    public AggregateCost CalculateAggregate(IEnumerable<UsageBucket> buckets)
    {
        decimal known = 0;
        var hasKnown = false;
        long unpricedTokens = 0;
        var unpricedBuckets = 0;
        var quality = CostQuality.ExactTokenSplit;
        var warnings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bucket in buckets)
        {
            var result = Calculate(bucket);
            quality = CombineQuality(quality, result.Quality);
            if (result.CostUsd.HasValue)
            {
                known += result.CostUsd.Value;
                hasKnown = true;
            }
            else
            {
                unpricedBuckets++;
                unpricedTokens += bucket.DisplayedTotalTokens;
            }
            if (!string.IsNullOrWhiteSpace(result.Warning)) warnings.Add(result.Warning!);
        }
        return new AggregateCost(hasKnown ? known : null, unpricedTokens, unpricedBuckets, quality, warnings.ToList());
    }

    private static CostQuality CombineQuality(CostQuality left, CostQuality right) =>
        (CostQuality)Math.Max((int)left, (int)right);

    private static PricingDocument BuiltInDefaults() => new(
        1,
        new DateOnly(2026, 8, 19),
        [
            new PricingRule("Codex", "gpt-5.6", MatchMode.Exact, 5m, 0.5m, 6.25m, 30m,
                "https://developers.openai.com/api/docs/models/gpt-5.6-sol", new DateOnly(2026, 8, 19)),
            new PricingRule("Codex", "gpt-5.6-sol*", MatchMode.Wildcard, 5m, 0.5m, 6.25m, 30m,
                "https://developers.openai.com/api/docs/models/gpt-5.6-sol", new DateOnly(2026, 8, 19)),
            new PricingRule("Codex", "gpt-5.6-terra*", MatchMode.Wildcard, 2m, 0.2m, 2.5m, 12m,
                "https://developers.openai.com/api/docs/models/gpt-5.6-terra", new DateOnly(2026, 8, 19)),
            new PricingRule("Codex", "gpt-5.6-luna*", MatchMode.Wildcard, 0.2m, 0.02m, 0.25m, 1.2m,
                "https://developers.openai.com/api/docs/models/gpt-5.6-luna", new DateOnly(2026, 8, 19)),
            new PricingRule("Codex", "gpt-5.5*", MatchMode.Wildcard, 5m, 0.5m, null, 30m,
                "https://developers.openai.com/api/docs/models/gpt-5.5", new DateOnly(2026, 8, 19)),
            new PricingRule("Codex", "gpt-5.4-mini*", MatchMode.Wildcard, 0.75m, 0.075m, null, 4.5m,
                "https://developers.openai.com/api/docs/models/gpt-5.4-mini", new DateOnly(2026, 8, 19)),
            new PricingRule("Codex", "gpt-5.4-nano*", MatchMode.Wildcard, 0.2m, 0.02m, null, 1.25m,
                "https://developers.openai.com/api/docs/models/gpt-5.4-nano", new DateOnly(2026, 8, 19)),
            new PricingRule("Codex", "gpt-5.4*", MatchMode.Wildcard, 2.5m, 0.25m, null, 15m,
                "https://developers.openai.com/api/docs/models/gpt-5.4", new DateOnly(2026, 8, 19)),
            new PricingRule("Codex", "gpt-5*", MatchMode.Wildcard, 1.25m, 0.125m, null, 10m,
                "https://platform.openai.com/pricing", new DateOnly(2026, 8, 19)),
            new PricingRule("Codex", "gpt-4.1*", MatchMode.Wildcard, 2m, 0.5m, null, 8m,
                "https://platform.openai.com/pricing", new DateOnly(2026, 8, 19)),
            new PricingRule("Antigravity", "gemini-3.7-flash*", MatchMode.Wildcard, 0.75m, 0.075m, null, 3.75m,
                "https://ai.google.dev/gemini-api/docs/pricing", new DateOnly(2026, 8, 19), false),
            new PricingRule("Antigravity", "gemini-3.6-flash*", MatchMode.Wildcard, 0.75m, 0.075m, null, 3.75m,
                "https://ai.google.dev/gemini-api/docs/pricing", new DateOnly(2026, 8, 19), false),
            new PricingRule("Antigravity", "gemini-3.5-flash-lite*", MatchMode.Wildcard, 0.3m, 0.03m, null, 2.5m,
                "https://ai.google.dev/gemini-api/docs/pricing", new DateOnly(2026, 8, 19), false),
            new PricingRule("Antigravity", "gemini-3.5-flash*", MatchMode.Wildcard, 1.5m, 0.15m, null, 9m,
                "https://ai.google.dev/gemini-api/docs/pricing", new DateOnly(2026, 8, 19), false),
            new PricingRule("Antigravity", "gemini-3.1-flash-lite*", MatchMode.Wildcard, 0.25m, 0.025m, null, 1.5m,
                "https://ai.google.dev/gemini-api/docs/pricing", new DateOnly(2026, 8, 19), false),
            new PricingRule("Antigravity", "gemini-2.5-pro*", MatchMode.Wildcard, 1.25m, 0.3125m, null, 10m,
                "https://ai.google.dev/gemini-api/docs/pricing", new DateOnly(2026, 8, 19), false),
            new PricingRule("Antigravity", "gemini-2.5-flash*", MatchMode.Wildcard, 0.3m, 0.03m, null, 2.5m,
                "https://ai.google.dev/gemini-api/docs/pricing", new DateOnly(2026, 8, 19), false),
            new PricingRule("Antigravity", "claude-3-7-sonnet*", MatchMode.Wildcard, 3m, 0.3m, null, 15m,
                "https://docs.anthropic.com/en/docs/about-claude/pricing", new DateOnly(2026, 8, 19), false)
        ]);
}
