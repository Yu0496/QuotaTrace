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

    public static bool IsClaudeOrGpt(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return false;
        var model = modelId.Trim().ToLowerInvariant();
        return model.StartsWith("claude", StringComparison.OrdinalIgnoreCase) ||
               model.Contains("claude", StringComparison.OrdinalIgnoreCase) ||
               model.StartsWith("gpt", StringComparison.OrdinalIgnoreCase) ||
               model.Contains("gpt", StringComparison.OrdinalIgnoreCase) ||
               model.Contains("3p", StringComparison.OrdinalIgnoreCase);
    }

    public static string GetModelQuotaPool(string modelId) =>
        IsClaudeOrGpt(modelId) ? "3p" : "gemini";

    public static bool SnapshotMatchesPool(QuotaSnapshot snapshot, string poolCategory)
    {
        var id = $"{snapshot.ModelOrPoolId} {snapshot.DisplayLabel}".ToLowerInvariant();
        if (id.Contains("argon", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (poolCategory == "gemini")
        {
            return id.Contains("gemini", StringComparison.OrdinalIgnoreCase);
        }
        if (poolCategory == "3p")
        {
            return (id.Contains("3p", StringComparison.OrdinalIgnoreCase) ||
                    id.Contains("claude", StringComparison.OrdinalIgnoreCase) ||
                    id.Contains("gpt", StringComparison.OrdinalIgnoreCase) ||
                    id.Contains("other", StringComparison.OrdinalIgnoreCase)) &&
                   !id.Contains("gemini", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    public IReadOnlyList<AntigravityQuotaEstimate> EstimateAll(
        IReadOnlyList<QuotaSnapshot> allSnapshots,
        IReadOnlyList<AntigravityGenerationUsage> allGenerations)
    {
        var results = new List<AntigravityQuotaEstimate>();

        var poolList = new (string Category, string Display)[]
        {
            ("gemini", "Gemini Models"),
            ("3p", "Claude and GPT models")
        };

        var windows = new[] { "5h", "weekly" };

        foreach (var pool in poolList)
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

        decimal? fullValue = projection.FullValue;
        string? note = projection.Note;

        // 全周期宏观基准外推与防污染校准：
        // 当切片外推为 null、发生倒挂（fullValue < cost）、或严重受断层污染（如远低于合理阈值且宏观基准正常）时，
        // 若全周期已有有效额度消耗（consumedFraction >= 0.05）且有对应消费（cost > 0），采用宏观基准外推并保底。
        if (consumedFraction.HasValue && consumedFraction.Value >= 0.05 && cost.HasValue && cost.Value > 0)
        {
            var macroFull = Math.Round(cost.Value / (decimal)consumedFraction.Value, 2, MidpointRounding.AwayFromZero);
            var isContaminated = !fullValue.HasValue || fullValue.Value < cost.Value || (macroFull >= 15.0m && fullValue < 15.0m);
            if (isContaminated)
            {
                fullValue = Math.Max(macroFull, cost.Value);
                note = "按全周期累计消耗外推";
            }
            else
            {
                fullValue = Math.Max(fullValue!.Value, cost.Value);
            }
        }
        else if (fullValue.HasValue && cost.HasValue && fullValue.Value < cost.Value)
        {
            fullValue = cost.Value;
        }

        return new AntigravityQuotaEstimate(latestSnapshot.ModelOrPoolId, windowKind, poolDisplayName,
            remaining, resetAt, cost, consumedFraction, fullValue, QuotaEstimateConfidence.Low,
            cycleGens.Count, null, null, null, note);
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
