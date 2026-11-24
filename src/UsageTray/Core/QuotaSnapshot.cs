namespace UsageTray.Core;

public static class QuotaSources
{
    public const string AntigravityLocal = "antigravity-local";
    public const string AntigravityStatusLine = "antigravity-statusline";
    public const string AntigravityManual = "antigravity-manual";
}

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

    public bool IsManualCalibration => string.Equals(Source, QuotaSources.AntigravityManual, StringComparison.OrdinalIgnoreCase);

    public bool IsStatusLineSync => string.Equals(Source, QuotaSources.AntigravityStatusLine, StringComparison.OrdinalIgnoreCase);

    public bool IsResetPassed(DateTimeOffset? now = null) =>
        ResetAt.HasValue && ResetAt.Value <= (now ?? DateTimeOffset.Now);

    public bool IsStale(DateTimeOffset? now = null)
    {
        if (IsManualCalibration) return false;
        var at = now ?? DateTimeOffset.UtcNow;
        if (IsStatusLineSync) return at - CapturedAt >= TimeSpan.FromHours(24);
        return at - CapturedAt >= TimeSpan.FromMinutes(15);
    }

    public bool IsExpiredStaleAntigravityFiveHour(DateTimeOffset? now = null)
    {
        if (Provider != ProviderKind.Antigravity) return false;
        var at = now ?? DateTimeOffset.UtcNow;
        var val = $"{WindowKind} {ModelOrPoolId} {DisplayLabel}".ToLowerInvariant();
        var is5h = val.Contains("5h", StringComparison.Ordinal) || val.Contains("5 h", StringComparison.Ordinal) ||
                   val.Contains("5-hour", StringComparison.Ordinal) || val.Contains("5 hour", StringComparison.Ordinal) ||
                   val.Contains("five_hour", StringComparison.Ordinal) || val.Contains("5小时", StringComparison.Ordinal);
        return is5h && ResetAt.HasValue && ResetAt.Value <= at && (at - ResetAt.Value) > TimeSpan.FromHours(12);
    }

    // A passed reset time is not evidence of the server's new remaining quota.
    public double? EffectiveRemainingFraction(DateTimeOffset? now = null) =>
        IsResetPassed(now) || !HasValidFraction ? null : RemainingFraction;
}
