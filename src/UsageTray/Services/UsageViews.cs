using UsageTray.Core;

namespace UsageTray.Services;

public sealed record DailyUsageView(DateOnly Date, long InputTokens, long CachedTokens, long CacheCreationTokens, long OutputTokens,
    decimal? ApiEquivalentUsd, long UnpricedTokens, CostQuality CostQuality)
{
    public long NonCachedInputTokens => Math.Max(0, InputTokens - CachedTokens - CacheCreationTokens);
}

public sealed record ModelUsageView(string ModelId, ProviderKind Provider, long InputTokens, long CachedTokens, long CacheCreationTokens,
    long OutputTokens, decimal? ApiEquivalentUsd, long UnpricedTokens, CostQuality CostQuality)
{
    public long NonCachedInputTokens => Math.Max(0, InputTokens - CachedTokens - CacheCreationTokens);
}

public sealed record ProjectUsageView(string ProjectKey, string DisplayName, ProviderKind Provider, long Tokens,
    long InputTokens, long CachedTokens, long CacheCreationTokens, long OutputTokens, decimal? ApiEquivalentUsd,
    long UnpricedTokens, CostQuality CostQuality)
{
    public long NonCachedInputTokens => Math.Max(0, InputTokens - CachedTokens - CacheCreationTokens);
}

public sealed record QuotaView(QuotaSnapshot Snapshot, bool IsOffline);

public sealed class DashboardSnapshot
{
    public DateRange Range { get; init; } = DateRange.Today();
    public ProviderKind? ProviderFilter { get; init; }
    public decimal? ApiEquivalentUsd { get; init; }
    // InputTokens 保留数据源原始总 input，便于追溯；界面和 Sub2API 兼容口径使用 NonCachedInputTokens。
    public long InputTokens { get; init; }
    public long CachedTokens { get; init; }
    public long CacheCreationTokens { get; init; }
    public long NonCachedInputTokens => Math.Max(0, InputTokens - CachedTokens - CacheCreationTokens);
    public long TotalInputTokens => NonCachedInputTokens + CachedTokens + CacheCreationTokens;
    public long OutputTokens { get; init; }
    public long UnpricedTokens { get; init; }
    public CostQuality CostQuality { get; init; } = CostQuality.Unavailable;
    public DateTimeOffset? CoverageStart { get; init; }
    public IReadOnlyList<DailyUsageView> Daily { get; init; } = [];
    public IReadOnlyList<ModelUsageView> Models { get; init; } = [];
    public IReadOnlyList<ProjectUsageView> Projects { get; init; } = [];
    public IReadOnlyList<QuotaView> Quotas { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public DateTimeOffset RefreshedAt { get; init; }
}
