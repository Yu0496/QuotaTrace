using System;
using System.Collections.Generic;
using System.Linq;
using UsageTray.Core;
using UsageTray.Pricing;

namespace UsageTray.Providers.Antigravity;

public sealed record QuotaEpoch(
    string PoolId,
    string WindowKind,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    double StartRemaining,
    double EndRemaining,
    DateTimeOffset? ResetAt,
    double ConsumedFraction,
    decimal ObservedCostUsd,
    int GenerationCount,
    decimal? FullQuotaEstimateUsd
);

public sealed class AntigravityQuotaEstimator
{
    private readonly PricingService _pricingService;

    public AntigravityQuotaEstimator(PricingService pricingService)
    {
        _pricingService = pricingService;
    }

    public static string GetModelQuotaPool(string modelId)
    {
        var model = modelId.Trim().ToLowerInvariant();
        if (model.StartsWith("gemini", StringComparison.OrdinalIgnoreCase))
        {
            return "gemini";
        }
        return "3p";
    }

    public static bool SnapshotMatchesPool(QuotaSnapshot snapshot, string poolCategory)
    {
        var id = $"{snapshot.ModelOrPoolId} {snapshot.DisplayLabel}".ToLowerInvariant();
        if (poolCategory == "gemini")
        {
            return id.Contains("gemini", StringComparison.OrdinalIgnoreCase);
        }
        return id.Contains("3p", StringComparison.OrdinalIgnoreCase) ||
               id.Contains("claude", StringComparison.OrdinalIgnoreCase) ||
               id.Contains("gpt", StringComparison.OrdinalIgnoreCase) ||
               id.Contains("other", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<AntigravityQuotaEstimate> EstimateAll(
        IReadOnlyList<QuotaSnapshot> allSnapshots,
        IReadOnlyList<AntigravityGenerationUsage> allGenerations)
    {
        var results = new List<AntigravityQuotaEstimate>();

        // Pools to estimate: gemini, 3p
        var pools = new[]
        {
            (Category: "gemini", Display: "Gemini Models"),
            (Category: "3p", Display: "Claude and GPT models")
        };

        var windows = new[] { "5h", "weekly" };

        foreach (var pool in pools)
        {
            foreach (var win in windows)
            {
                var estimate = EstimatePoolWindow(pool.Category, pool.Display, win, allSnapshots, allGenerations);
                if (estimate is not null)
                {
                    results.Add(estimate);
                }
            }
        }

        return results;
    }

    public AntigravityQuotaEstimate? EstimatePoolWindow(
        string poolCategory,
        string poolDisplayName,
        string windowKind,
        IReadOnlyList<QuotaSnapshot> allSnapshots,
        IReadOnlyList<AntigravityGenerationUsage> allGenerations)
    {
        // Filter snapshots
        var matchingSnapshots = allSnapshots
            .Where(s => SnapshotMatchesPool(s, poolCategory) && IsMatchingWindow(s, windowKind) && s.RemainingFraction.HasValue)
            .OrderBy(s => s.CapturedAt)
            .ToList();

        if (matchingSnapshots.Count == 0) return null;

        var latestSnapshot = matchingSnapshots.Last();
        var poolGenerations = allGenerations
            .Where(g => GetModelQuotaPool(g.Model) == poolCategory)
            .OrderBy(g => g.Timestamp)
            .ToList();

        // Calculate Window-based cycle start & reset
        DateTimeOffset cycleStart;
        DateTimeOffset? resetAt = latestSnapshot.ResetAt;

        if (windowKind == "weekly")
        {
            cycleStart = resetAt.HasValue ? resetAt.Value.AddDays(-7) : latestSnapshot.CapturedAt.AddDays(-7);
            if (cycleStart > DateTimeOffset.UtcNow) cycleStart = latestSnapshot.CapturedAt.AddDays(-7);
        }
        else
        {
            cycleStart = resetAt.HasValue ? resetAt.Value.AddHours(-5) : latestSnapshot.CapturedAt.AddHours(-5);
            if (cycleStart > DateTimeOffset.UtcNow) cycleStart = latestSnapshot.CapturedAt.AddHours(-5);
        }

        var cycleGens = poolGenerations
            .Where(g => g.Timestamp >= cycleStart && (!resetAt.HasValue || g.Timestamp <= resetAt.Value))
            .ToList();

        decimal observedCost = 0;
        foreach (var gen in cycleGens)
        {
            var rule = _pricingService.Rules is not null
                ? PricingMatcher.Find(_pricingService.Rules, ProviderKind.Antigravity, gen.Model)
                : null;
            var isLong = rule?.LongContextPrice is not null && gen.TotalInputTokens > rule.LongContextThresholdTokens;

            var bucket = new UsageBucket(
                ProviderKind.Antigravity,
                DateOnly.FromDateTime(gen.Timestamp.ToLocalTime().DateTime),
                gen.ProjectKey,
                gen.Model,
                gen.TotalInputTokens,
                gen.CacheReadTokens,
                gen.OutputTokens,
                1,
                gen.Quality,
                gen.SourceDbPath,
                gen.ConversationId,
                gen.CacheWriteTokens,
                CostQuality.ExactTokenSplit,
                null,
                isLong ? 1 : 0,
                0,
                isLong ? gen.TotalInputTokens : 0,
                isLong ? gen.CacheReadTokens : 0,
                isLong ? gen.CacheWriteTokens : 0,
                isLong ? gen.OutputTokens : 0,
                true
            );
            var calc = _pricingService.Calculate(bucket);
            if (calc.CostUsd.HasValue)
            {
                observedCost += calc.CostUsd.Value;
            }
        }

        var remaining = latestSnapshot.RemainingFraction;
        var consumedFraction = remaining.HasValue ? Math.Clamp(Math.Round(1.0 - remaining.Value, 6), 0.0, 1.0) : (double?)null;

        decimal? currentEstimate = null;
        if (consumedFraction.HasValue && consumedFraction.Value >= 0.005 && observedCost > 0)
        {
            currentEstimate = Math.Round(observedCost / (decimal)consumedFraction.Value, 2, MidpointRounding.AwayFromZero);
        }

        var confidence = QuotaEstimateConfidence.Low;
        if (consumedFraction.HasValue)
        {
            if (consumedFraction.Value >= 0.30 && cycleGens.Count >= 5)
            {
                confidence = QuotaEstimateConfidence.High;
            }
            else if (consumedFraction.Value >= 0.10)
            {
                confidence = QuotaEstimateConfidence.Medium;
            }
        }

        var details = $"{remaining:P0} 剩余 (消耗 {consumedFraction:P1})，本轮 API 等值 ${observedCost:F2} ({cycleGens.Count} 次请求)";

        return new AntigravityQuotaEstimate(
            latestSnapshot.ModelOrPoolId,
            windowKind,
            poolDisplayName,
            latestSnapshot.RemainingFraction,
            latestSnapshot.ResetAt,
            observedCost,
            consumedFraction,
            currentEstimate,
            confidence,
            cycleGens.Count,
            currentEstimate,
            null,
            null,
            details
        );
    }


    private List<QuotaEpoch> BuildEpochs(
        IReadOnlyList<QuotaSnapshot> snapshots,
        IReadOnlyList<AntigravityGenerationUsage> generations)
    {
        var epochs = new List<QuotaEpoch>();
        if (snapshots.Count == 0) return epochs;

        var currentGroup = new List<QuotaSnapshot> { snapshots[0] };

        for (int i = 1; i < snapshots.Count; i++)
        {
            var prev = snapshots[i - 1];
            var curr = snapshots[i];

            bool resetChanged = curr.ResetAt.HasValue && prev.ResetAt.HasValue &&
                                Math.Abs((curr.ResetAt.Value - prev.ResetAt.Value).TotalSeconds) > 60;

            bool rechargeDetected = curr.RemainingFraction.HasValue && prev.RemainingFraction.HasValue &&
                                    (curr.RemainingFraction.Value - prev.RemainingFraction.Value) > 0.02;

            bool gapTooLarge = (curr.CapturedAt - prev.CapturedAt).TotalHours > 36;

            if (resetChanged || rechargeDetected || gapTooLarge)
            {
                var epoch = EvaluateEpoch(currentGroup, generations);
                if (epoch is not null) epochs.Add(epoch);
                currentGroup = new List<QuotaSnapshot> { curr };
            }
            else
            {
                currentGroup.Add(curr);
            }
        }

        if (currentGroup.Count > 0)
        {
            var epoch = EvaluateEpoch(currentGroup, generations);
            if (epoch is not null) epochs.Add(epoch);
        }

        return epochs;
    }

    private QuotaEpoch? EvaluateEpoch(
        IReadOnlyList<QuotaSnapshot> epochSnapshots,
        IReadOnlyList<AntigravityGenerationUsage> generations)
    {
        if (epochSnapshots.Count == 0) return null;
        var startSnap = epochSnapshots.First();
        var endSnap = epochSnapshots.Last();

        double startRem = startSnap.RemainingFraction ?? 1.0;
        double endRem = endSnap.RemainingFraction ?? startRem;
        double consumed = Math.Max(0, startRem - endRem);

        var startTime = startSnap.CapturedAt;
        var endTime = endSnap.CapturedAt;

        var epochGens = generations
            .Where(g => g.Timestamp >= startTime && g.Timestamp <= endTime)
            .ToList();

        decimal cost = 0;
        foreach (var gen in epochGens)
        {
            var rule = _pricingService.Rules is not null
                ? PricingMatcher.Find(_pricingService.Rules, ProviderKind.Antigravity, gen.Model)
                : null;
            var isLong = rule?.LongContextPrice is not null && gen.TotalInputTokens > rule.LongContextThresholdTokens;

            var bucket = new UsageBucket(
                ProviderKind.Antigravity,
                DateOnly.FromDateTime(gen.Timestamp.ToLocalTime().DateTime),
                gen.ProjectKey,
                gen.Model,
                gen.TotalInputTokens,
                gen.CacheReadTokens,
                gen.OutputTokens,
                1,
                gen.Quality,
                gen.SourceDbPath,
                gen.ConversationId,
                gen.CacheWriteTokens,
                CostQuality.ExactTokenSplit,
                null,
                isLong ? 1 : 0,
                0,
                isLong ? gen.TotalInputTokens : 0,
                isLong ? gen.CacheReadTokens : 0,
                isLong ? gen.CacheWriteTokens : 0,
                isLong ? gen.OutputTokens : 0,
                true
            );
            var calc = _pricingService.Calculate(bucket);
            if (calc.CostUsd.HasValue)
            {
                cost += calc.CostUsd.Value;
            }
        }

        decimal? fullEstimate = null;
        if (consumed >= 0.02 && cost > 0)
        {
            fullEstimate = cost / (decimal)consumed;
        }

        return new QuotaEpoch(
            startSnap.ModelOrPoolId,
            startSnap.WindowKind,
            startTime,
            endTime,
            startRem,
            endRem,
            endSnap.ResetAt,
            consumed,
            cost,
            epochGens.Count,
            fullEstimate
        );
    }

    private static bool IsMatchingWindow(QuotaSnapshot snapshot, string windowKind)
    {
        var val = $"{snapshot.WindowKind} {snapshot.ModelOrPoolId} {snapshot.DisplayLabel}".ToLowerInvariant();
        if (windowKind == "5h")
        {
            return val.Contains("5h", StringComparison.Ordinal) || val.Contains("5 h", StringComparison.Ordinal) ||
                   val.Contains("5-hour", StringComparison.Ordinal) || val.Contains("5 hour", StringComparison.Ordinal) ||
                   val.Contains("five_hour", StringComparison.Ordinal) || val.Contains("five hour", StringComparison.Ordinal) ||
                   val.Contains("5小时", StringComparison.Ordinal);
        }
        if (windowKind == "weekly")
        {
            return val.Contains("week", StringComparison.Ordinal) || val.Contains("weekly", StringComparison.Ordinal) ||
                   val.Contains("7-day", StringComparison.Ordinal) || val.Contains("7 day", StringComparison.Ordinal) ||
                   val.Contains("7d", StringComparison.Ordinal) || val.Contains("周", StringComparison.Ordinal);
        }
        return false;
    }
}
