namespace UsageTray.Core;

public sealed record QuotaSnapshot(
    ProviderKind Provider,
    DateTimeOffset CapturedAt,
    string ModelOrPoolId,
    string DisplayLabel,
    double? RemainingFraction,
    DateTimeOffset? ResetAt,
    string WindowKind,
    string Source,
    string? PlanTier = null)
{
    public bool HasValidFraction => !RemainingFraction.HasValue ||
        (RemainingFraction.Value >= 0 && RemainingFraction.Value <= 1);

    public bool IsResetPassed(DateTimeOffset? now = null) =>
        ResetAt.HasValue && ResetAt.Value <= (now ?? DateTimeOffset.Now);

    public bool IsStale(DateTimeOffset? now = null) =>
        (now ?? DateTimeOffset.UtcNow) - CapturedAt >= TimeSpan.FromMinutes(15);

    // A passed reset time is not evidence of the server's new remaining quota.
    public double? EffectiveRemainingFraction(DateTimeOffset? now = null) =>
        IsResetPassed(now) || !HasValidFraction ? null : RemainingFraction;
}
