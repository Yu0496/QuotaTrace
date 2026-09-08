using System.Security.Cryptography;
using System.Text;
using UsageTray.Core;

namespace UsageTray.Providers.Codex;

/// <summary>One cumulative token snapshot read from a Codex session event.</summary>
public sealed record CodexCumulativeUsage(
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long? CacheWriteInputTokens = null,
    long? ReasoningOutputTokens = null)
{
    public long SafeInputTokens => Math.Max(0, InputTokens);
    public long SafeCachedInputTokens => Math.Max(0, CachedInputTokens);
    public long SafeOutputTokens => Math.Max(0, OutputTokens);
    public bool HasCacheWrite => CacheWriteInputTokens.HasValue;
}

/// <summary>Per-request usage reported by last_token_usage; it is evidence, not a ledger.</summary>
public sealed record CodexRequestUsage(
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long? CacheWriteInputTokens = null,
    long? ReasoningOutputTokens = null)
{
    public long SafeInputTokens => Math.Max(0, InputTokens);
    public long SafeCachedInputTokens => Math.Max(0, CachedInputTokens);
    public long SafeOutputTokens => Math.Max(0, OutputTokens);
}

public enum CodexRequestUsageQuality
{
    Unknown = 0,
    AggregateOnly = 1,
    Exact = 2
}

/// <summary>
/// Raw token event. EventKey intentionally does not include the source path so the same
/// logical event copied between active and archived trees is deduplicated.
/// </summary>
public sealed record CodexTokenSnapshot(
    string SessionId,
    DateTimeOffset CapturedAt,
    string? ModelId,
    string? ProjectKey,
    string? ServiceTier,
    CodexCumulativeUsage TotalUsage,
    CodexRequestUsage? LastUsage,
    long? ContextWindowTokens,
    string SourcePath,
    int SourceLine,
    string EventType = "token_count",
    string? EventKey = null)
{
    public string StableEventKey => EventKey ?? ComputeEventKey();

    public string ComputeEventKey()
    {
        var raw = string.Join("|", SessionId, CapturedAt.ToUniversalTime().ToString("O"), EventType,
            TotalUsage.InputTokens, TotalUsage.CachedInputTokens, TotalUsage.CacheWriteInputTokens?.ToString() ?? "?",
            TotalUsage.OutputTokens);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }
}

public sealed record CodexDeltaUsage(
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long CacheWriteInputTokens,
    bool CacheWriteAvailable);

public sealed record CodexEventAudit(
    string SessionId,
    string? ProjectKey,
    string EventKey,
    DateTimeOffset CapturedAt,
    string ModelId,
    string SourcePath,
    int SourceLine,
    int CounterEpoch,
    CodexDeltaUsage Delta,
    CodexRequestUsageQuality RequestUsageQuality,
    bool IsLongContext,
    bool IsDuplicate,
    IReadOnlyList<string> DuplicateSources,
    string? ServiceTier = null);


public sealed record CodexSourceAudit(
    string SessionId,
    string SourcePath,
    int EventCount,
    int UniqueEventCount,
    DateTimeOffset? FirstTimestamp,
    DateTimeOffset? LastTimestamp,
    long? FirstInputTokens,
    long? LastInputTokens,
    string Classification,
    IReadOnlyList<string> OverlappingSources);

public sealed record CodexNormalizationResult(
    IReadOnlyList<UsageBucket> Buckets,
    IReadOnlyList<CodexEventAudit> Events,
    IReadOnlyList<CodexSourceAudit> Sources,
    int DuplicateEventCount,
    int CounterRewindCount,
    int RequestShapeUnknownCount,
    int ConflictingDuplicateCount,
    IReadOnlyList<string> Warnings,
    int SuppressedSourceCount = 0,
    int SuppressedEventCount = 0);

public static class CodexSourceClassifier
{
    public static string Classify(IReadOnlyList<string> orderedLeft, IReadOnlyList<string> orderedRight,
        DateTimeOffset? leftFirst = null, DateTimeOffset? leftLast = null,
        DateTimeOffset? rightFirst = null, DateTimeOffset? rightLast = null,
        long? leftFirstCounter = null, long? leftLastCounter = null,
        long? rightFirstCounter = null, long? rightLastCounter = null)
    {
        if (orderedLeft.SequenceEqual(orderedRight, StringComparer.OrdinalIgnoreCase)) return "exact";
        if (IsPrefix(orderedLeft, orderedRight) || IsPrefix(orderedRight, orderedLeft)) return "prefix";
        var overlap = orderedLeft.Intersect(orderedRight, StringComparer.OrdinalIgnoreCase).Any();
        if (overlap) return "partial";
        var timeOverlap = leftFirst.HasValue && leftLast.HasValue && rightFirst.HasValue && rightLast.HasValue &&
                          leftFirst <= rightLast && rightFirst <= leftLast;
        var counterOverlap = leftFirstCounter.HasValue && leftLastCounter.HasValue && rightFirstCounter.HasValue && rightLastCounter.HasValue &&
                             leftFirstCounter <= rightLastCounter && rightFirstCounter <= leftLastCounter;
        if (timeOverlap || counterOverlap) return "partial";
        if (leftLast.HasValue && rightFirst.HasValue && rightFirst >= leftLast &&
            (!leftLastCounter.HasValue || !rightFirstCounter.HasValue || rightFirstCounter >= leftLastCounter)) return "continuation";
        if (rightLast.HasValue && leftFirst.HasValue && leftFirst >= rightLast &&
            (!rightLastCounter.HasValue || !leftFirstCounter.HasValue || leftFirstCounter >= rightLastCounter)) return "continuation";
        return "independent";
    }

    private static bool IsPrefix(IReadOnlyList<string> prefix, IReadOnlyList<string> full)
    {
        if (prefix.Count > full.Count) return false;
        for (var index = 0; index < prefix.Count; index++)
            if (!string.Equals(prefix[index], full[index], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}
