using System;
using System.Collections.Generic;
using System.Linq;
using UsageTray.Core;
using UsageTray.Pricing;

namespace UsageTray.Providers.Antigravity;

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

        var duration = windowKind == "weekly" ? TimeSpan.FromDays(7) : TimeSpan.FromHours(5);
        var cycleStart = latestSnapshot.ResetAt?.Subtract(duration);
        var resetAt = latestSnapshot.ResetAt;
        var validCycle = cycleStart.HasValue && resetAt.HasValue && !latestSnapshot.IsResetPassed() &&
            cycleStart <= latestSnapshot.CapturedAt;
        var cycleGens = validCycle ? poolGenerations.Where(g => g.Timestamp >= cycleStart && g.Timestamp < resetAt).ToList() : [];
        var cost = validCycle ? Calculate(cycleGens) : null;
        var remaining = latestSnapshot.EffectiveRemainingFraction();
        var consumedFraction = remaining.HasValue ? Math.Round(1 - remaining.Value, 6) : (double?)null;
        var projection = UsageTray.Services.QuotaProjector.Estimate(latestSnapshot, matchingSnapshots, (start, end) =>
            Calculate(poolGenerations.Where(g => g.Timestamp >= start && g.Timestamp < end)));
        return new AntigravityQuotaEstimate(latestSnapshot.ModelOrPoolId, windowKind, poolDisplayName,
            remaining, resetAt, cost, consumedFraction, projection.FullValue, QuotaEstimateConfidence.Low,
            cycleGens.Count, null, null, null, projection.Note);
    }

    private decimal? Calculate(IEnumerable<AntigravityGenerationUsage> generations)
    {
        var buckets = generations.Select(gen =>
        {
            var rule = PricingMatcher.Find(_pricingService.Rules, ProviderKind.Antigravity, gen.Model);
            var isLong = rule?.LongContextPrice is not null && gen.TotalInputTokens > rule.LongContextThresholdTokens;
            return new UsageBucket(ProviderKind.Antigravity, DateOnly.FromDateTime(gen.Timestamp.LocalDateTime),
                gen.ProjectKey, gen.Model, gen.TotalInputTokens, gen.CacheReadTokens, gen.OutputTokens, 1,
                gen.Quality, gen.SourceDbPath, gen.ConversationId, gen.CacheWriteTokens, CostQuality.ExactTokenSplit,
                null, isLong ? 1 : 0, 0, isLong ? gen.TotalInputTokens : 0, isLong ? gen.CacheReadTokens : 0,
                isLong ? gen.CacheWriteTokens : 0, isLong ? gen.OutputTokens : 0, true);
        });
        return _pricingService.CalculateAggregate(buckets).PricedCostUsd;
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
