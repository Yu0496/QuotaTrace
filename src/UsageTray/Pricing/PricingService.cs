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

    public PricingService(string filePath, PricingDocument document, PricingDocument? defaultTemplate = null)
    {
        FilePath = filePath;
        Document = MigrateDocument(document, defaultTemplate);
    }

    public static PricingService LoadOrCreate(string filePath, string? bundledDefaultPath = null)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(filePath)!);
        PricingDocument? bundled = null;
        if (!string.IsNullOrWhiteSpace(bundledDefaultPath) && File.Exists(bundledDefaultPath))
        {
            try { bundled = JsonSerializer.Deserialize<PricingDocument>(File.ReadAllText(bundledDefaultPath), JsonOptions); }
            catch { }
        }
        var template = bundled ?? BuiltInDefaults();

        if (File.Exists(filePath))
        {
            try
            {
                var document = JsonSerializer.Deserialize<PricingDocument>(File.ReadAllText(filePath), JsonOptions);
                if (document is not null && document.Rules is not null)
                {
                    var service = new PricingService(filePath, document, template);
                    service.Save();
                    return service;
                }
            }
            catch { }
        }

        var result = new PricingService(filePath, template);
        result.Save();
        return result;
    }

    public void Replace(PricingDocument document) => Document = MigrateDocument(document);

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
        if (bucket.HasRequestShapeUncertainty)
            return new CostCalculation(null, CostQuality.RequestShapeUnavailable, $"模型 {bucket.ModelId} 缺少可核对的请求级 token 形状", rule);

        var standard = new TokenPriceSet(rule.InputPerMillionUsd, rule.CacheReadPerMillionUsd ?? rule.InputPerMillionUsd,
            rule.CacheWritePerMillionUsd, rule.OutputPerMillionUsd);
        var price = standard;
        if (!string.IsNullOrWhiteSpace(bucket.ServiceTier))
        {
            if (rule.ServiceTierPrices is null || !rule.ServiceTierPrices.TryGetValue(bucket.ServiceTier, out price!))
                return new CostCalculation(null, CostQuality.Unavailable, $"未配置服务档位价格：{bucket.ServiceTier}", rule);
        }

        var input = Math.Max(0, bucket.InputTokens);
        var cacheRead = Math.Min(input, Math.Max(0, bucket.CachedInputTokens));
        var cacheWrite = Math.Min(Math.Max(0, input - cacheRead), Math.Max(0, bucket.CacheWriteInputTokens));
        if (!bucket.CacheWriteAvailable && input > cacheRead)
            return new CostCalculation(null, CostQuality.CacheWriteUnavailable, "来源没有 cache-write 字段，未将缺失值假定为零", rule);

        var semanticsValidated = rule.CacheSplitValidated && bucket.CostQuality != CostQuality.PartialPrice;
        if ((cacheRead > 0 || cacheWrite > 0) && !semanticsValidated)
            return new CostCalculation(null, CostQuality.PartialPrice, $"模型 {bucket.ModelId} 的缓存 token 语义尚未完成样本验证", rule);

        var longInput = Math.Min(input, Math.Max(0, bucket.LongContextInputTokens));
        var longRead = Math.Min(longInput, Math.Max(0, bucket.LongContextCachedInputTokens));
        var longWrite = Math.Min(Math.Max(0, longInput - longRead), Math.Max(0, bucket.LongContextCacheWriteInputTokens));
        var longOutput = Math.Min(Math.Max(0, bucket.OutputTokens), Math.Max(0, bucket.LongContextOutputTokens));
        var shortInput = input - longInput;
        var shortRead = Math.Max(0, cacheRead - longRead);
        var shortWrite = Math.Max(0, cacheWrite - longWrite);
        var shortOutput = Math.Max(0, Math.Max(0, bucket.OutputTokens) - longOutput);
        var longPrice = price;
        if (longInput > 0 || longOutput > 0)
        {
            if (rule.LongContextPrice is null)
                return new CostCalculation(null, CostQuality.LongContextUncertain, $"模型 {bucket.ModelId} 缺少 >272K 长上下文价格", rule);
            longPrice = rule.LongContextPrice;
        }

        var cost = SegmentCost(shortInput, shortRead, shortWrite, shortOutput, price) +
                   SegmentCost(longInput, longRead, longWrite, longOutput, longPrice);
        var quality = !bucket.CacheWriteAvailable ? CostQuality.CacheWriteUnavailable :
            cacheRead > 0 || cacheWrite > 0 ? CostQuality.ExactTokenSplit : CostQuality.ExactTokensNoCache;
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
            if (result.CostUsd.HasValue) { known += result.CostUsd.Value; hasKnown = true; }
            else { unpricedBuckets++; unpricedTokens += bucket.DisplayedTotalTokens; }
            if (!string.IsNullOrWhiteSpace(result.Warning)) warnings.Add(result.Warning!);
        }
        return new AggregateCost(hasKnown ? known : null, unpricedTokens, unpricedBuckets, quality, warnings.ToList());
    }

    private static decimal SegmentCost(long input, long cacheRead, long cacheWrite, long output, TokenPriceSet price) =>
        Math.Max(0, input - cacheRead - cacheWrite) * price.InputPerMillionUsd / 1_000_000m +
        cacheRead * price.CacheReadPerMillionUsd / 1_000_000m +
        cacheWrite * (price.CacheWritePerMillionUsd ?? price.InputPerMillionUsd) / 1_000_000m +
        Math.Max(0, output) * price.OutputPerMillionUsd / 1_000_000m;

    private static CostQuality CombineQuality(CostQuality left, CostQuality right) => (CostQuality)Math.Max((int)left, (int)right);

    private static PricingDocument MigrateDocument(PricingDocument document, PricingDocument? defaultTemplate = null)
    {
        var rules = document.Rules
            .Where(rule => !(rule.Provider.Equals("Codex", StringComparison.OrdinalIgnoreCase) &&
                             rule.MatchMode == MatchMode.Wildcard && rule.ModelPattern.Equals("gpt-5*", StringComparison.OrdinalIgnoreCase)))
            .Select(AddLongContextPrice)
            .ToList();

        // 自动合并内置/模板中新增的官方规则
        var template = defaultTemplate ?? BuiltInDefaults();
        var existingKeys = new HashSet<string>(rules.Select(r => $"{r.Provider}::{r.ModelPattern}".ToLowerInvariant()));

        foreach (var rule in template.Rules)
        {
            var key = $"{rule.Provider}::{rule.ModelPattern}".ToLowerInvariant();
            if (!existingKeys.Contains(key))
            {
                rules.Add(rule);
                existingKeys.Add(key);
            }
        }

        return new PricingDocument(Math.Max(2, document.SchemaVersion), document.LastVerifiedAt, rules);
    }

    private static PricingRule AddLongContextPrice(PricingRule rule)
    {
        if (rule.LongContextPrice is not null) return rule;
        var pattern = rule.ModelPattern.ToLowerInvariant();
        TokenPriceSet? longPrice = pattern switch
        {
            "gpt-5.6" or "gpt-5.6-sol*" => new TokenPriceSet(10m, 1m, 12.5m, 45m),
            "gpt-5.6-terra*" => new TokenPriceSet(4m, 0.4m, 5m, 18m),
            "gpt-5.6-luna*" => new TokenPriceSet(0.4m, 0.04m, 0.5m, 1.8m),
            "gemini-3.1-pro*" or "gemini-pro-default*" or "gemini-pro*" => new TokenPriceSet(4m, 1m, null, 18m),
            "gemini-2.5-pro*" => new TokenPriceSet(2.5m, 0.625m, null, 15m),
            _ => null
        };
        var threshold = pattern.StartsWith("gemini", StringComparison.OrdinalIgnoreCase) ? 200_000 : 272_000;
        return longPrice is null ? rule : rule with { LongContextPrice = longPrice, LongContextThresholdTokens = threshold };
    }

    public static PricingDocument BuiltInDefaults() => new(
        2, new DateOnly(2026, 8, 20),
        [
            new PricingRule("Codex", "gpt-5.6", MatchMode.Exact, 5m, 0.5m, 6.25m, 30m,
                "https://developers.openai.com/api/docs/models/gpt-5.6-sol", new DateOnly(2026, 8, 20), true,
                new TokenPriceSet(10m, 1m, 12.5m, 45m)),
            new PricingRule("Codex", "gpt-5.6-sol*", MatchMode.Wildcard, 5m, 0.5m, 6.25m, 30m,
                "https://developers.openai.com/api/docs/models/gpt-5.6-sol", new DateOnly(2026, 8, 20), true,
                new TokenPriceSet(10m, 1m, 12.5m, 45m)),
            new PricingRule("Codex", "gpt-5.6-terra*", MatchMode.Wildcard, 2m, 0.2m, 2.5m, 12m,
                "https://developers.openai.com/api/docs/models/gpt-5.6-terra", new DateOnly(2026, 8, 20), true,
                new TokenPriceSet(4m, 0.4m, 5m, 18m)),
            new PricingRule("Codex", "gpt-5.6-luna*", MatchMode.Wildcard, 0.2m, 0.02m, 0.25m, 1.2m,
                "https://developers.openai.com/api/docs/models/gpt-5.6-luna", new DateOnly(2026, 8, 20), true,
                new TokenPriceSet(0.4m, 0.04m, 0.5m, 1.8m)),
            new PricingRule("Codex", "gpt-5.5*", MatchMode.Wildcard, 5m, 0.5m, null, 30m,
                "https://developers.openai.com/api/docs/models/gpt-5.5", new DateOnly(2026, 8, 20)),
            new PricingRule("Codex", "gpt-5.4-mini*", MatchMode.Wildcard, 0.75m, 0.075m, null, 4.5m,
                "https://developers.openai.com/api/docs/models/gpt-5.4-mini", new DateOnly(2026, 8, 20)),
            new PricingRule("Codex", "gpt-5.4-nano*", MatchMode.Wildcard, 0.2m, 0.02m, null, 1.25m,
                "https://developers.openai.com/api/docs/models/gpt-5.4-nano", new DateOnly(2026, 8, 20)),
            new PricingRule("Codex", "gpt-5.4*", MatchMode.Wildcard, 2.5m, 0.25m, null, 15m,
                "https://developers.openai.com/api/docs/models/gpt-5.4", new DateOnly(2026, 8, 20)),
            new PricingRule("Codex", "gpt-4.1*", MatchMode.Wildcard, 2m, 0.5m, null, 8m,
                "https://platform.openai.com/pricing", new DateOnly(2026, 8, 20)),
            new PricingRule("Antigravity", "claude-sonnet-4-6*", MatchMode.Wildcard, 3m, 0.3m, 3.75m, 15m,
                "https://docs.anthropic.com/en/docs/about-claude/pricing", new DateOnly(2026, 8, 20), true),
            new PricingRule("Antigravity", "claude-sonnet*", MatchMode.Wildcard, 3m, 0.3m, 3.75m, 15m,
                "https://docs.anthropic.com/en/docs/about-claude/pricing", new DateOnly(2026, 8, 20), true),
            new PricingRule("Antigravity", "claude-3-7-sonnet*", MatchMode.Wildcard, 3m, 0.3m, 3.75m, 15m,
                "https://docs.anthropic.com/en/docs/about-claude/pricing", new DateOnly(2026, 8, 20), true),
            new PricingRule("Antigravity", "claude-3.7-sonnet*", MatchMode.Wildcard, 3m, 0.3m, 3.75m, 15m,
                "https://docs.anthropic.com/en/docs/about-claude/pricing", new DateOnly(2026, 8, 20), true),
            new PricingRule("Antigravity", "claude-3-5-sonnet*", MatchMode.Wildcard, 3m, 0.3m, 3.75m, 15m,
                "https://docs.anthropic.com/en/docs/about-claude/pricing", new DateOnly(2026, 8, 20), true),
            new PricingRule("Antigravity", "claude-3.5-sonnet*", MatchMode.Wildcard, 3m, 0.3m, 3.75m, 15m,
                "https://docs.anthropic.com/en/docs/about-claude/pricing", new DateOnly(2026, 8, 20), true),
            new PricingRule("Antigravity", "claude-opus-4-6*", MatchMode.Wildcard, 15m, 1.5m, 18.75m, 75m,
                "https://docs.anthropic.com/en/docs/about-claude/pricing", new DateOnly(2026, 8, 20), true),
            new PricingRule("Antigravity", "claude-opus*", MatchMode.Wildcard, 15m, 1.5m, 18.75m, 75m,
                "https://docs.anthropic.com/en/docs/about-claude/pricing", new DateOnly(2026, 8, 20), true),
            new PricingRule("Antigravity", "claude-3-opus*", MatchMode.Wildcard, 15m, 1.5m, 18.75m, 75m,
                "https://docs.anthropic.com/en/docs/about-claude/pricing", new DateOnly(2026, 8, 20), true),
            new PricingRule("Antigravity", "claude-3.5-opus*", MatchMode.Wildcard, 15m, 1.5m, 18.75m, 75m,
                "https://docs.anthropic.com/en/docs/about-claude/pricing", new DateOnly(2026, 8, 20), true),
            new PricingRule("Antigravity", "claude-3-5-haiku*", MatchMode.Wildcard, 0.8m, 0.08m, 1.0m, 4m,
                "https://docs.anthropic.com/en/docs/about-claude/pricing", new DateOnly(2026, 8, 20), true),
            new PricingRule("Antigravity", "claude-3.5-haiku*", MatchMode.Wildcard, 0.8m, 0.08m, 1.0m, 4m,
                "https://docs.anthropic.com/en/docs/about-claude/pricing", new DateOnly(2026, 8, 20), true),
            new PricingRule("Antigravity", "claude-haiku*", MatchMode.Wildcard, 0.8m, 0.08m, 1.0m, 4m,
                "https://docs.anthropic.com/en/docs/about-claude/pricing", new DateOnly(2026, 8, 20), true),
            new PricingRule("Antigravity", "gemini-3.7-flash*", MatchMode.Wildcard, 0.75m, 0.075m, null, 3.75m,
                "https://ai.google.dev/gemini-api/docs/pricing", new DateOnly(2026, 8, 20), true),
            new PricingRule("Antigravity", "gemini-3.6-flash*", MatchMode.Wildcard, 0.75m, 0.075m, null, 3.75m,
                "https://ai.google.dev/gemini-api/docs/pricing", new DateOnly(2026, 8, 20), true),
            new PricingRule("Antigravity", "gemini-3.6-flash-tiered*", MatchMode.Wildcard, 0.75m, 0.075m, null, 3.75m,
                "https://ai.google.dev/gemini-api/docs/pricing", new DateOnly(2026, 8, 20), true),
            new PricingRule("Antigravity", "gemini-3.5-flash*", MatchMode.Wildcard, 1.5m, 0.15m, null, 9m,
                "https://ai.google.dev/gemini-api/docs/pricing", new DateOnly(2026, 8, 20), true),
            new PricingRule("Antigravity", "gemini-3-flash-a*", MatchMode.Wildcard, 1.5m, 0.15m, null, 9m,
                "https://ai.google.dev/gemini-api/docs/pricing", new DateOnly(2026, 8, 20), true),
            new PricingRule("Antigravity", "gemini-default*", MatchMode.Wildcard, 1.5m, 0.15m, null, 9m,
                "https://ai.google.dev/gemini-api/docs/pricing", new DateOnly(2026, 8, 20), true),
            new PricingRule("Antigravity", "gemini-3.5-flash-lite*", MatchMode.Wildcard, 0.3m, 0.03m, null, 2.5m,
                "https://ai.google.dev/gemini-api/docs/pricing", new DateOnly(2026, 8, 20), true),
            new PricingRule("Antigravity", "gemini-3.1-flash-lite*", MatchMode.Wildcard, 0.25m, 0.025m, null, 1.5m,
                "https://ai.google.dev/gemini-api/docs/pricing", new DateOnly(2026, 8, 20), true),
            new PricingRule("Antigravity", "gemini-3.1-pro*", MatchMode.Wildcard, 2.0m, 0.5m, null, 12m,
                "https://ai.google.dev/gemini-api/docs/pricing", new DateOnly(2026, 8, 20), true,
                new TokenPriceSet(4m, 1m, null, 18m), 200000),
            new PricingRule("Antigravity", "gemini-pro-default*", MatchMode.Wildcard, 2.0m, 0.5m, null, 12m,
                "https://ai.google.dev/gemini-api/docs/pricing", new DateOnly(2026, 8, 20), true,
                new TokenPriceSet(4m, 1m, null, 18m), 200000),
            new PricingRule("Antigravity", "gemini-pro*", MatchMode.Wildcard, 2.0m, 0.5m, null, 12m,
                "https://ai.google.dev/gemini-api/docs/pricing", new DateOnly(2026, 8, 20), true,
                new TokenPriceSet(4m, 1m, null, 18m), 200000),
            new PricingRule("Antigravity", "gemini-2.5-pro*", MatchMode.Wildcard, 1.25m, 0.3125m, null, 10m,
                "https://ai.google.dev/gemini-api/docs/pricing", new DateOnly(2026, 8, 20), true,
                new TokenPriceSet(2.5m, 0.625m, null, 15m), 200000),
            new PricingRule("Antigravity", "gemini-2.5-flash*", MatchMode.Wildcard, 0.3m, 0.03m, null, 2.5m,
                "https://ai.google.dev/gemini-api/docs/pricing", new DateOnly(2026, 8, 20), true),
            new PricingRule("Antigravity", "gpt-oss-120b*", MatchMode.Wildcard, 0.6m, 0.15m, null, 2.4m,
                "https://ai.google.dev/gemini-api/docs/pricing", new DateOnly(2026, 8, 20), true),
            new PricingRule("Antigravity", "gpt-oss*", MatchMode.Wildcard, 0.6m, 0.15m, null, 2.4m,
                "https://ai.google.dev/gemini-api/docs/pricing", new DateOnly(2026, 8, 20), true)
        ]);
}

