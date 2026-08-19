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
    bool CacheSplitValidated = true);

public sealed record PricingDocument(
    int SchemaVersion,
    DateOnly LastVerifiedAt,
    IReadOnlyList<PricingRule> Rules);
