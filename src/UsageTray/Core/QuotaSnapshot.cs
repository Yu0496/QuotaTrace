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

    /// <summary>
    /// 当快照重置时间已过且记录为 0% 时，推断额度已经自然重置回满（1.0），等待下一次日志校准。
    /// </summary>
    public double? EffectiveRemainingFraction(DateTimeOffset? now = null)
    {
        if (IsResetPassed(now) && RemainingFraction.HasValue && RemainingFraction.Value <= 0.0001)
        {
            return 1.0;
        }
        return RemainingFraction;
    }
}


