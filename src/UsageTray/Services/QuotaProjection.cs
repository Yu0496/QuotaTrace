using UsageTray.Core;

namespace UsageTray.Services;

public sealed record QuotaProjection(decimal? FullValue, string Note);

public static class QuotaProjector
{
    // Estimate from matching observations, never from an assumed 100% cycle baseline.
    public static QuotaProjection Estimate(QuotaSnapshot latest, IEnumerable<QuotaSnapshot> history,
        Func<DateTimeOffset, DateTimeOffset, decimal?> intervalCost, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        if (latest.IsResetPassed(at) || latest.IsStale(at)) return new(null, "快照待更新");
        if (latest.ResetAt is null || latest.RemainingFraction is null || !latest.HasValidFraction)
            return new(null, "额度或周期未知");
        var duration = latest.WindowKind == "weekly" ? TimeSpan.FromDays(7) :
            latest.WindowKind == "5h" ? TimeSpan.FromHours(5) : (TimeSpan?)null;
        if (duration is null || latest.CapturedAt < latest.ResetAt.Value - duration.Value || latest.CapturedAt > at.AddMinutes(1))
            return new(null, "额度或周期未知");
        var samples = history.Where(q => q.Provider == latest.Provider && q.ModelOrPoolId == latest.ModelOrPoolId &&
            q.WindowKind == latest.WindowKind && q.CapturedAt >= latest.ResetAt.Value - duration.Value && q.CapturedAt <= latest.CapturedAt && q.ResetAt.HasValue &&
            Math.Abs((q.ResetAt.Value - latest.ResetAt.Value).TotalSeconds) <= 60 &&
            q.HasValidFraction && q.RemainingFraction.HasValue).OrderBy(q => q.CapturedAt).ToList();
        if (!samples.Any(q => q.CapturedAt == latest.CapturedAt)) samples.Add(latest);
        QuotaSnapshot? baseline = null;
        QuotaSnapshot? previous = null;
        foreach (var sample in samples)
        {
            if (previous is null || sample.PlanTier != previous.PlanTier ||
                sample.RemainingFraction > previous.RemainingFraction + 0.000001 ||
                sample.CapturedAt - previous.CapturedAt > TimeSpan.FromHours(36)) baseline = sample;
            baseline ??= sample;
            previous = sample;
        }
        if (baseline is null || baseline.CapturedAt >= latest.CapturedAt) return new(null, "需同周期两个快照");
        var fraction = Math.Round(baseline.RemainingFraction!.Value - latest.RemainingFraction.Value, 6);
        if (fraction < 0.05) return new(null, "样本消耗不足5%");
        var cost = intervalCost(baseline.CapturedAt.AddTicks(1), latest.CapturedAt.AddTicks(1));
        if (cost is null) return new(null, "样本含未定价用量");
        if (cost <= 0) return new(null, "缺少对应本机用量");
        return new(Math.Round(cost.Value / (decimal)fraction, 2, MidpointRounding.AwayFromZero), "仅本机样本外推");
    }
}
