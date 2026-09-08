namespace UsageTray.Core;

public enum MatchMode
{
    Exact = 0,
    Prefix = 1,
    Contains = 2,
    Wildcard = 3
}

public sealed record PricingRule(
    string Provider,
    string ModelPattern,
    MatchMode MatchMode,
    decimal InputPerMillionUsd,
    decimal? CacheReadPerMillionUsd,
    decimal? CacheWritePerMillionUsd,
    decimal OutputPerMillionUsd,
    string SourceUrl,
    DateOnly LastVerifiedAt,
    bool CacheSplitValidated = true,
    TokenPriceSet? LongContextPrice = null,
    long LongContextThresholdTokens = 272_000,
    IReadOnlyDictionary<string, TokenPriceSet>? ServiceTierPrices = null,
    IReadOnlyDictionary<string, TokenPriceSet>? ServiceTierLongContextPrices = null,
    string? UnverifiedReason = null,
    string? ReferenceBasis = null);

public sealed record TokenPriceSet(
    decimal InputPerMillionUsd,
    decimal CacheReadPerMillionUsd,
    decimal? CacheWritePerMillionUsd,
    decimal OutputPerMillionUsd);

public sealed record PricingDocument(
    int SchemaVersion,
    DateOnly LastVerifiedAt,
    IReadOnlyList<PricingRule> Rules);
