using UsageTray.Core;
using UsageTray.Providers.Antigravity;

namespace UsageTray.Services;

public sealed record DailyUsageView(DateOnly Date, long InputTokens, long CachedTokens, long CacheCreationTokens, long OutputTokens,
    decimal? ApiEquivalentUsd, long UnpricedTokens, CostQuality CostQuality)
{
    public long NonCachedInputTokens => Math.Max(0, InputTokens - CachedTokens - CacheCreationTokens);
    public double CacheHitRate => InputTokens > 0 ? (double)CachedTokens / InputTokens * 100.0 : 0.0;
}

public sealed record ModelUsageView(string ModelId, ProviderKind Provider, long InputTokens, long CachedTokens, long CacheCreationTokens,
    long OutputTokens, decimal? ApiEquivalentUsd, long UnpricedTokens, CostQuality CostQuality, TokenSpeedEstimate? SpeedEstimate = null)
{
    public long NonCachedInputTokens => Math.Max(0, InputTokens - CachedTokens - CacheCreationTokens);
    public double CacheHitRate => InputTokens > 0 ? (double)CachedTokens / InputTokens * 100.0 : 0.0;
}

public sealed record ProjectUsageView(string ProjectKey, string DisplayName, ProviderKind Provider, long Tokens,
    long InputTokens, long CachedTokens, long CacheCreationTokens, long OutputTokens, decimal? ApiEquivalentUsd,
    long UnpricedTokens, CostQuality CostQuality, TokenSpeedEstimate? SpeedEstimate = null)
{
    public long NonCachedInputTokens => Math.Max(0, InputTokens - CachedTokens - CacheCreationTokens);
    public double CacheHitRate => InputTokens > 0 ? (double)CachedTokens / InputTokens * 100.0 : 0.0;
}

public sealed record QuotaView(QuotaSnapshot Snapshot, bool IsOffline);

public sealed record CodexCycleUsageView(
    DateTimeOffset CycleStart,
    DateTimeOffset? ResetAt,
    double? RemainingFraction,
    double? UsedFraction,
    decimal? CycleCostUsd,
    decimal? EstimatedWeeklyCostUsd,
    long CycleInputTokens,
    long CycleCachedTokens,
    long CycleCacheCreationTokens,
    long CycleOutputTokens,
    CostQuality CostQuality,
    string PoolName = "标准额度",
    string PoolCategory = "standard",
    string? EstimateNote = null)
{
    public long NonCachedInputTokens => Math.Max(0, CycleInputTokens - CycleCachedTokens - CycleCacheCreationTokens);
    public long TotalTokens => NonCachedInputTokens + CycleCachedTokens + CycleCacheCreationTokens + CycleOutputTokens;
    public double CacheHitRate => CycleInputTokens > 0 ? (double)CycleCachedTokens / CycleInputTokens * 100.0 : 0.0;
}

public sealed record DashboardSnapshot
{
    public DateRange Range { get; init; } = DateRange.Today();
    public ProviderKind? ProviderFilter { get; init; }
    public bool IsWeeklyCycleWindow { get; init; }
    public string? RangeDisplayOverride { get; init; }
    public DateTimeOffset? WindowStartUtc { get; init; }
    public DateTimeOffset? WindowEndUtc { get; init; }
    public decimal? ApiEquivalentUsd { get; init; }
    public decimal? CodexApiEquivalentUsd { get; init; }
    public decimal? CodexStandardApiEquivalentUsd { get; init; }
    public decimal? CodexSparkApiEquivalentUsd { get; init; }
    public decimal? CodexReserveApiEquivalentUsd { get; init; }
    public decimal? AntigravityApiEquivalentUsd { get; init; }
    public decimal? AntigravityGeminiApiEquivalentUsd { get; init; }
    public decimal? AntigravityClaudeApiEquivalentUsd { get; init; }
    // InputTokens 保留数据源原始总 input，便于追溯；界面和 Sub2API 兼容口径使用 NonCachedInputTokens。

    public long InputTokens { get; init; }
    public long CachedTokens { get; init; }
    public long CacheCreationTokens { get; init; }
    public long NonCachedInputTokens => Math.Max(0, InputTokens - CachedTokens - CacheCreationTokens);
    public long TotalInputTokens => NonCachedInputTokens + CachedTokens + CacheCreationTokens;
    public double CacheHitRate => InputTokens > 0 ? (double)CachedTokens / InputTokens * 100.0 : 0.0;
    public long OutputTokens { get; init; }

    public long UnpricedTokens { get; init; }
    public CostQuality CostQuality { get; init; } = CostQuality.Unavailable;
    public DateTimeOffset? CoverageStart { get; init; }
    public CodexCycleUsageView? CodexWeeklyCycle { get; init; }
    public CodexCycleUsageView? CodexSparkWeeklyCycle { get; init; }
    public CodexCycleUsageView? CodexReserveWeeklyCycle { get; init; }
    public IReadOnlyList<CodexCycleUsageView> CodexWeeklyCycles { get; init; } = [];
    public IReadOnlyList<AntigravityQuotaEstimate> AntigravityEstimates { get; init; } = [];
    public TokenSpeedEstimate? SpeedEstimate { get; init; }
    public IReadOnlyList<DailyUsageView> Daily { get; init; } = [];
    public IReadOnlyList<ModelUsageView> Models { get; init; } = [];
    public IReadOnlyList<ProjectUsageView> Projects { get; init; } = [];
    public IReadOnlyList<QuotaView> Quotas { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public DateTimeOffset RefreshedAt { get; init; }
}
