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

    public const int DefaultDocumentVersion = 5;

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
        if (rule.UnverifiedReason is not null)
            return new CostCalculation(null, CostQuality.Unavailable, $"{bucket.ModelId} 未定价：{rule.UnverifiedReason}", rule);
        if (bucket.HasRequestShapeUncertainty)
            return new CostCalculation(null, CostQuality.RequestShapeUnavailable, $"模型 {bucket.ModelId} 缺少可核对的请求级 token 形状", rule);

        var standard = new TokenPriceSet(rule.InputPerMillionUsd, rule.CacheReadPerMillionUsd ?? rule.InputPerMillionUsd,
            rule.CacheWritePerMillionUsd, rule.OutputPerMillionUsd);
        var price = standard;
        var tier = NormalizeTier(bucket.ServiceTier);
        if (tier is not null)
        {
            if (rule.ServiceTierPrices is null || !rule.ServiceTierPrices.TryGetValue(tier, out price!))
                return new CostCalculation(null, CostQuality.Unavailable, $"未配置服务档位价格：{bucket.ServiceTier}", rule);
        }

        if (bucket.CacheWriteInputTokens > 0 && rule.CacheWritePerMillionUsd is null)
            return new CostCalculation(null, CostQuality.PartialPrice, $"模型 {bucket.ModelId} 缺少缓存创建价格", rule);
        if (bucket.CachedInputTokens > 0 && rule.CacheReadPerMillionUsd is null)
            return new CostCalculation(null, CostQuality.PartialPrice, $"模型 {bucket.ModelId} 缺少缓存读取价格", rule);
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
                return new CostCalculation(null, CostQuality.LongContextUncertain, $"模型 {bucket.ModelId} 缺少长上下文价格", rule);
            if (tier is not null)
            {
                if (rule.ServiceTierLongContextPrices is null || !rule.ServiceTierLongContextPrices.TryGetValue(tier, out longPrice!))
                    return new CostCalculation(null, CostQuality.LongContextUncertain, $"模型 {bucket.ModelId} 缺少 {tier} 长上下文价格", rule);
            }
            else longPrice = rule.LongContextPrice;
        }

        if ((shortWrite > 0 && price.CacheWritePerMillionUsd is null) ||
            (longWrite > 0 && longPrice.CacheWritePerMillionUsd is null))
            return new CostCalculation(null, CostQuality.PartialPrice, $"模型 {bucket.ModelId} 当前档位缺少缓存创建价格", rule);

        var cost = SegmentCost(shortInput, shortRead, shortWrite, shortOutput, price) +
                   SegmentCost(longInput, longRead, longWrite, longOutput, longPrice);
        var quality = !bucket.CacheWriteAvailable ? CostQuality.CacheWriteUnavailable :
            cacheRead > 0 || cacheWrite > 0 ? CostQuality.ExactTokenSplit : CostQuality.ExactTokensNoCache;
        return new CostCalculation(cost, quality, null, rule);
    }

    public AggregateCost CalculateAggregate(IEnumerable<UsageBucket> buckets)
    {
        decimal known = 0;
        long unpricedTokens = 0;
        var unpricedBuckets = 0;
        var quality = CostQuality.ExactTokenSplit;
        var warnings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bucket in buckets)
        {
            if (bucket.DisplayedTotalTokens == 0) continue;
            var result = Calculate(bucket);
            quality = CombineQuality(quality, result.Quality);
            if (result.CostUsd.HasValue) { known += result.CostUsd.Value; }
            else { unpricedBuckets++; unpricedTokens += bucket.DisplayedTotalTokens; }
            if (!string.IsNullOrWhiteSpace(result.Warning)) warnings.Add(result.Warning!);
        }
        return new AggregateCost(unpricedBuckets == 0 ? known : null, unpricedTokens, unpricedBuckets, quality, warnings.ToList());
    }

    private static decimal SegmentCost(long input, long cacheRead, long cacheWrite, long output, TokenPriceSet price) =>
        Math.Max(0, input - cacheRead - cacheWrite) * price.InputPerMillionUsd / 1_000_000m +
        cacheRead * price.CacheReadPerMillionUsd / 1_000_000m +
        cacheWrite * (price.CacheWritePerMillionUsd ?? price.InputPerMillionUsd) / 1_000_000m +
        Math.Max(0, output) * price.OutputPerMillionUsd / 1_000_000m;

    private static CostQuality CombineQuality(CostQuality left, CostQuality right) => (CostQuality)Math.Max((int)left, (int)right);

    public static string? NormalizeTier(string? tier) => string.IsNullOrWhiteSpace(tier) ||
        tier.Equals("standard", StringComparison.OrdinalIgnoreCase) || tier.Equals("default", StringComparison.OrdinalIgnoreCase) ||
        tier.Equals("auto", StringComparison.OrdinalIgnoreCase) ? null :
        tier.Equals("priority", StringComparison.OrdinalIgnoreCase) ? "fast" : tier.Trim().ToLowerInvariant();

    private static PricingDocument MigrateDocument(PricingDocument document, PricingDocument? defaultTemplate = null)
    {
        var template = defaultTemplate ?? BuiltInDefaults();
        var legacy = ReadEmbedded("legacy-pricing.json");
        var rules = new List<PricingRule>();
        var custom = new List<PricingRule>();
        foreach (var rule in document.Rules)
        {
            // Upgrade only shipped rates; retain separately configured user rates.
            var old = legacy.Rules.FirstOrDefault(r => r.Provider.Equals(rule.Provider, StringComparison.OrdinalIgnoreCase) &&
                r.ModelPattern.Equals(rule.ModelPattern, StringComparison.OrdinalIgnoreCase) && r.MatchMode == rule.MatchMode &&
                r.InputPerMillionUsd == rule.InputPerMillionUsd && r.CacheReadPerMillionUsd == rule.CacheReadPerMillionUsd &&
                r.CacheWritePerMillionUsd == rule.CacheWritePerMillionUsd && r.OutputPerMillionUsd == rule.OutputPerMillionUsd &&
                r.SourceUrl == rule.SourceUrl && SamePrices(r, rule));
            if (document.SchemaVersion < DefaultDocumentVersion && old is not null)
            {
                var replacement = template.Rules.FirstOrDefault(r => r.Provider == rule.Provider &&
                    (r.ModelPattern == rule.ModelPattern || r.ModelPattern == rule.ModelPattern.TrimEnd('*')));
                if (replacement is not null) { rules.Add(replacement); continue; }
            }
            rules.Add(rule);
            if (old is null && !template.Rules.Any(r => SamePrices(r, rule) && r.ModelPattern == rule.ModelPattern)) custom.Add(rule);
        }
        foreach (var rule in template.Rules)
            if (!rules.Any(r => r.Provider == rule.Provider && r.ModelPattern == rule.ModelPattern && r.MatchMode == rule.MatchMode) &&
                !custom.Any(r => r.Provider == rule.Provider && PricingMatcher.Matches(r, rule.ModelPattern.TrimEnd('*'))))
                rules.Add(rule);
        return new PricingDocument(Math.Max(DefaultDocumentVersion, document.SchemaVersion), template.LastVerifiedAt, rules);
    }

    private static bool SamePrices(PricingRule a, PricingRule b) =>
        JsonSerializer.Serialize(a with { LastVerifiedAt = default }, JsonOptions) ==
        JsonSerializer.Serialize(b with { LastVerifiedAt = default }, JsonOptions);

    private static PricingDocument ReadEmbedded(string name)
    {
        using var stream = typeof(PricingService).Assembly.GetManifestResourceStream("UsageTray.Pricing." + name)
            ?? throw new InvalidOperationException("Missing bundled pricing: " + name);
        return JsonSerializer.Deserialize<PricingDocument>(stream, JsonOptions)!;
    }

    public static PricingDocument BuiltInDefaults() => ReadEmbedded("default-pricing.json");
}
