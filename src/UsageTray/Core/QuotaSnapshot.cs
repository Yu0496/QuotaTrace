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
}
