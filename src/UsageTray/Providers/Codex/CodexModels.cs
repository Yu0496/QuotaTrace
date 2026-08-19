using UsageTray.Core;

namespace UsageTray.Providers.Codex;

public sealed record CodexParseResult(
    IReadOnlyList<UsageBucket> Buckets,
    string? SessionId,
    string? ProjectKey,
    string? LastModel,
    int WarningCount,
    bool HasTokenData,
    DateTimeOffset? CoverageStart,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<QuotaSnapshot> Quotas = null!);

internal sealed record TokenFields(long Input, long Cached, long CacheWrite, long Output, bool IsCumulative, int Score);
