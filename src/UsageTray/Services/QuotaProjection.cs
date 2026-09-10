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
        if (latest.IsResetPassed(at)) return new(null, "周期已重置");
        if (latest.ResetAt is null || latest.RemainingFraction is null || !latest.HasValidFraction)
            return new(null, "额度或周期未知");
        var duration = latest.WindowKind == "weekly" ? TimeSpan.FromDays(7) :
            latest.WindowKind == "5h" ? TimeSpan.FromHours(5) : (TimeSpan?)null;
        if (duration is null || latest.CapturedAt < latest.ResetAt.Value - duration.Value || latest.CapturedAt > at.AddMinutes(5))
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
        var fullValue = Math.Round(cost.Value / (decimal)fraction, 2, MidpointRounding.AwayFromZero);
        var isStale = latest.IsStale(at);
        var note = isStale ? "快照待更新" : "仅本机样本外推";
        return new(fullValue, note);
    }

    public static IReadOnlyList<ModelQuotaProjectionView> EstimateModelProjections(
        QuotaSnapshot latest,
        IEnumerable<QuotaSnapshot> history,
        Func<DateTimeOffset, DateTimeOffset, IReadOnlyDictionary<string, decimal>> getIntervalModelCosts,
        IEnumerable<QuotaSnapshot>? allHistoricalSnapshots = null,
        DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        if (latest.IsResetPassed(at)) return [];
        if (latest.ResetAt is null || latest.RemainingFraction is null || !latest.HasValidFraction) return [];
        var duration = latest.WindowKind == "weekly" ? TimeSpan.FromDays(7) :
            latest.WindowKind == "5h" ? TimeSpan.FromHours(5) : (TimeSpan?)null;
        if (duration is null || latest.CapturedAt < latest.ResetAt.Value - duration.Value || latest.CapturedAt > at.AddMinutes(5))
            return [];

        var samples = history.Where(q => q.Provider == latest.Provider && q.ModelOrPoolId == latest.ModelOrPoolId &&
            q.WindowKind == latest.WindowKind && q.CapturedAt >= latest.ResetAt.Value - duration.Value && q.CapturedAt <= latest.CapturedAt && q.ResetAt.HasValue &&
            Math.Abs((q.ResetAt.Value - latest.ResetAt.Value).TotalSeconds) <= 60 &&
            q.HasValidFraction && q.RemainingFraction.HasValue).OrderBy(q => q.CapturedAt).ToList();
        if (!samples.Any(q => q.CapturedAt == latest.CapturedAt)) samples.Add(latest);

        var slices = new List<(DateTimeOffset Start, DateTimeOffset End, double FractionDrop, IReadOnlyDictionary<string, decimal> ModelCosts)>();
        QuotaSnapshot? anchor = null;
        foreach (var sample in samples)
        {
            if (anchor is not null)
            {
                if (sample.PlanTier != anchor.PlanTier ||
                    sample.RemainingFraction > anchor.RemainingFraction + 0.000001 ||
                    sample.CapturedAt - anchor.CapturedAt > TimeSpan.FromHours(36))
                {
                    slices.Clear();
                    anchor = sample;
                }
                else if (sample.RemainingFraction < anchor.RemainingFraction)
                {
                    var drop = anchor.RemainingFraction!.Value - sample.RemainingFraction!.Value;
                    if (drop > 0.00001)
                    {
                        var costs = getIntervalModelCosts(anchor.CapturedAt.AddTicks(1), sample.CapturedAt.AddTicks(1));
                        if (costs.Count > 0)
                        {
                            var totalSliceCost = costs.Values.Sum();
                            // 过滤因他机/网页端/云端消耗导致本机用量严重缺失的污染切片（外推周满额 < $15.00）
                            if (totalSliceCost / (decimal)drop >= 15.0m)
                            {
                                slices.Add((anchor.CapturedAt, sample.CapturedAt, drop, costs));
                            }
                        }
                        anchor = sample;
                    }
                }
            }
            anchor ??= sample;
        }

        var results = new List<ModelQuotaProjectionView>();
        var evaluatedModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var allModelsInSlices = slices.SelectMany(s => s.ModelCosts.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var model in allModelsInSlices)
        {
            double modelDrop = 0.0;
            decimal modelCost = 0m;

            foreach (var slice in slices)
            {
                if (!slice.ModelCosts.TryGetValue(model, out var cost) || cost <= 0) continue;
                var totalCost = slice.ModelCosts.Values.Sum();
                var ratio = totalCost > 0 ? (double)(cost / totalCost) : 0.0;
                modelDrop += slice.FractionDrop * ratio;
                modelCost += cost;
            }

            modelDrop = Math.Round(modelDrop, 6);
            if (modelDrop >= 0.025 && modelCost > 0)
            {
                var fullUsd = Math.Round(modelCost / (decimal)modelDrop, 2, MidpointRounding.AwayFromZero);
                if (fullUsd > 0)
                {
                    evaluatedModels.Add(model);
                    results.Add(new ModelQuotaProjectionView(
                        model,
                        fullUsd,
                        modelDrop,
                        modelCost,
                        "仅本机样本外推",
                        $"本周测算：该模型累计消耗额度 {modelDrop:P1}，对应参考金额 ${modelCost:F2}，外推周满额 ${fullUsd:N2}。",
                        true));
                }
            }
        }

        if (allHistoricalSnapshots != null)
        {
            var planTier = latest.PlanTier;
            var historicalWeekly = allHistoricalSnapshots
                .Where(s => s.Provider == latest.Provider && s.ModelOrPoolId == latest.ModelOrPoolId &&
                            s.WindowKind == latest.WindowKind && s.ResetAt.HasValue &&
                            s.HasValidFraction && s.RemainingFraction.HasValue &&
                            (string.IsNullOrEmpty(planTier) || string.Equals(s.PlanTier, planTier, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(s => s.CapturedAt)
                .ToList();

            var histCycles = historicalWeekly.GroupBy(s => s.ResetAt!.Value).ToList();
            foreach (var cycleGroup in histCycles.OrderByDescending(g => g.Key).Take(15))
            {
                var cycleReset = cycleGroup.Key;
                if (latest.ResetAt.HasValue && Math.Abs((cycleReset - latest.ResetAt.Value).TotalSeconds) <= 60) continue;

                var cSamples = cycleGroup.OrderBy(s => s.CapturedAt).ToList();
                if (cSamples.Count < 2) continue;

                QuotaSnapshot? cAnchor = null;
                var cSlices = new List<(DateTimeOffset Start, DateTimeOffset End, double FractionDrop, IReadOnlyDictionary<string, decimal> ModelCosts)>();
                foreach (var cs in cSamples)
                {
                    if (cAnchor != null)
                    {
                        if (cs.PlanTier != cAnchor.PlanTier ||
                            cs.RemainingFraction > cAnchor.RemainingFraction + 0.000001)
                        {
                            cSlices.Clear();
                            cAnchor = cs;
                        }
                        else if (cs.RemainingFraction < cAnchor.RemainingFraction)
                        {
                            var cDrop = cAnchor.RemainingFraction!.Value - cs.RemainingFraction!.Value;
                            if (cDrop > 0.00001)
                            {
                                var costs = getIntervalModelCosts(cAnchor.CapturedAt.AddTicks(1), cs.CapturedAt.AddTicks(1));
                            if (costs.Count > 0)
                            {
                                var cTotalCost = costs.Values.Sum();
                                if (cTotalCost / (decimal)cDrop >= 15.0m)
                                {
                                    cSlices.Add((cAnchor.CapturedAt, cs.CapturedAt, cDrop, costs));
                                }
                            }
                            cAnchor = cs;
                            }
                        }
                    }
                    cAnchor ??= cs;
                }

                var cModels = cSlices.SelectMany(s => s.ModelCosts.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var model in cModels)
                {
                    if (evaluatedModels.Contains(model)) continue;

                    double hDrop = 0.0;
                    decimal hCost = 0m;
                    foreach (var sl in cSlices)
                    {
                        if (sl.ModelCosts.TryGetValue(model, out var cost) && cost > 0)
                        {
                            var total = sl.ModelCosts.Values.Sum();
                            var ratio = total > 0 ? (double)(cost / total) : 0.0;
                            hDrop += sl.FractionDrop * ratio;
                            hCost += cost;
                        }
                    }
                    hDrop = Math.Round(hDrop, 6);
                    if (hDrop >= 0.03 && hCost > 0)
                    {
                        var fullUsd = Math.Round(hCost / (decimal)hDrop, 2, MidpointRounding.AwayFromZero);
                        if (fullUsd > 0)
                        {
                            evaluatedModels.Add(model);
                            results.Add(new ModelQuotaProjectionView(
                                model,
                                fullUsd,
                                hDrop,
                                hCost,
                                "历史同套餐样本",
                                $"历史测算：基于同套餐历史周期样本（累计消耗 {hDrop:P1}，参考金额 ${hCost:F2}）外推周满额 ${fullUsd:N2}。",
                                false));
                        }
                    }
                }
            }
        }

        return results.OrderByDescending(r => r.EstimatedWeeklyCostUsd).ToList();
    }
}
