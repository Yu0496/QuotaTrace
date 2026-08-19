using UsageTray.Core;

namespace UsageTray.Providers.Antigravity;

public sealed record AntigravityHistoryScanResult(
    IReadOnlyList<UsageBucket> Buckets,
    bool HasUsageEvents,
    bool HasCacheSplit,
    bool HasModel,
    bool HasTimestamp,
    bool HasConversationId,
    bool HasProject,
    int WarningCount,
    IReadOnlyList<string> Warnings);

public sealed record AntigravityProcessInfo(
    string Name,
    int ProcessId,
    string? ExecutablePath,
    string? CommandLine,
    string? CsrfToken = null);

public sealed record AntigravityQuotaResult(
    IReadOnlyList<QuotaSnapshot> Snapshots,
    string Source,
    string? PlanTier,
    string? Endpoint,
    IReadOnlyList<string> Warnings);
