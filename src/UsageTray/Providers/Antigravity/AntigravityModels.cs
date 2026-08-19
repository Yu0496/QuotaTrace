using System;
using UsageTray.Core;

namespace UsageTray.Providers.Antigravity;

public enum QuotaEstimateConfidence
{
    Low,
    Medium,
    High
}

public sealed record AntigravityGenerationUsage(
    string ConversationId,
    string? GenerationId,
    string? ResponseId,
    DateTimeOffset Timestamp,
    string Model,
    string? DisplayName,
    long InputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long ThinkingOutputTokens,
    long ResponseOutputTokens,
    long OutputTokens,
    string? ProjectKey,
    string SourceDbPath,
    int SourceRowIdx,
    DataQuality Quality = DataQuality.Exact)
{
    public string EffectiveKey => !string.IsNullOrWhiteSpace(ResponseId)
        ? ResponseId
        : (!string.IsNullOrWhiteSpace(GenerationId) ? GenerationId : $"{ConversationId}#{SourceRowIdx}#{Model}#{ResponseId}");

    public long TotalInputTokens => InputTokens + CacheReadTokens + CacheWriteTokens;
    public long TotalTokens => TotalInputTokens + OutputTokens;

    public bool InvariantHolds => OutputTokens == ThinkingOutputTokens + ResponseOutputTokens;
}

public sealed record AntigravityQuotaEstimate(
    string PoolId,
    string WindowKind,
    string DisplayName,
    double? RemainingFraction,
    DateTimeOffset? ResetAt,
    decimal? ObservedCostUsd,
    double? ConsumedFraction,
    decimal? EstimatedFullQuotaUsd,
    QuotaEstimateConfidence Confidence,
    int SampleCount,
    decimal? RollingMedianUsd,
    decimal? ObservedMinUsd,
    decimal? ObservedMaxUsd,
    string? CalculationDetails = null
);

public sealed record AntigravityProcessInfo(string Name, int ProcessId, string? ExecutablePath, string? CommandLine, string? CsrfToken);

public sealed record AntigravityQuotaResult(
    IReadOnlyList<QuotaSnapshot> Snapshots,
    string Source,
    string? PlanTier,
    string? Endpoint,
    IReadOnlyList<string> Warnings);

public sealed record AntigravityHistoryScanResult(
    IReadOnlyList<UsageBucket> Buckets,
    bool HasUsageEvents,
    bool HasCacheSplit,
    bool HasModel,
    bool HasTimestamp,
    bool HasConversation,
    bool HasProject,
    int ErrorCount,
    IReadOnlyList<string> Warnings);

