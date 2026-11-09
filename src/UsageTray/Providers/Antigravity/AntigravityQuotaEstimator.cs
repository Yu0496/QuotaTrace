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

    public static bool IsArgon(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return false;
        var model = modelId.Trim();
        return model.Contains("argon", StringComparison.OrdinalIgnoreCase);
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
        GetModelQuotaPool(modelId, null, null);

    public static string GetModelQuotaPool(string modelId, string argonPool)
    {
        if (IsClaudeOrGpt(modelId))
        {
            return "3p";
        }
        if (IsArgon(modelId))
        {
            return argonPool;
        }
        return "gemini";
    }

    public static string GetModelQuotaPool(
        string modelId,
        IReadOnlyList<QuotaSnapshot>? snapshots = null,
        IReadOnlyList<AntigravityGenerationUsage>? generations = null)
    {
        if (IsClaudeOrGpt(modelId))
        {
            return "3p";
        }
        if (IsArgon(modelId))
        {
            return ResolveArgonQuotaPool(snapshots, generations);
        }
        return "gemini";
    }

    public static string ResolveArgonQuotaPool(
        IReadOnlyList<QuotaSnapshot>? snapshots,
        IReadOnlyList<AntigravityGenerationUsage>? generations)
    {
        // 1. 若无快照数据，无法基于实际配额扣减进行判断，默认作为独立 Argon 池处理
        if (snapshots == null || snapshots.Count == 0)
        {
            return "argon";
        }

        // 查找所有属于 Argon 的快照
        var argonSnapshots = snapshots
            .Where(s => SnapshotMatchesPool(s, "argon"))
            .OrderBy(s => s.CapturedAt)
            .ToList();

        // 1.1 若配额接口中根本不存在任何 Argon 专属配额桶（例如 Ultra 订阅用户），则只能扣减 Gemini 主池
        if (argonSnapshots.Count == 0)
        {
            return "gemini";
        }

        var latestArgon = argonSnapshots.Last();

        // 2. 统计 Argon 桶在当前快照/序列中的实际扣减量
        var argonConsumed = latestArgon.RemainingFraction.HasValue
            ? Math.Max(0.0, 1.0 - latestArgon.RemainingFraction.Value)
            : 0.0;
        var argonStep = 0.0;
        if (argonSnapshots.Count >= 2)
        {
            var validF = argonSnapshots.Where(s => s.RemainingFraction.HasValue).Select(s => s.RemainingFraction!.Value).ToList();
            if (validF.Count >= 2)
            {
                argonStep = Math.Max(0.0, validF.Max() - validF.Min());
            }
        }
        var argonDeduction = Math.Max(argonConsumed, argonStep);

        // 3. 统计 Gemini 主池的实际扣减量
        var geminiSnapshots = snapshots
            .Where(s => SnapshotMatchesPool(s, "gemini"))
            .OrderBy(s => s.CapturedAt)
            .ToList();
        var geminiDeduction = 0.0;
        if (geminiSnapshots.Count > 0)
        {
            var latestGemini = geminiSnapshots.Last();
            var geminiConsumed = latestGemini.RemainingFraction.HasValue
                ? Math.Max(0.0, 1.0 - latestGemini.RemainingFraction.Value)
                : 0.0;
            var geminiStep = 0.0;
            if (geminiSnapshots.Count >= 2)
            {
                var validG = geminiSnapshots.Where(s => s.RemainingFraction.HasValue).Select(s => s.RemainingFraction!.Value).ToList();
                if (validG.Count >= 2)
                {
                    geminiStep = Math.Max(0.0, validG.Max() - validG.Min());
                }
            }
            geminiDeduction = Math.Max(geminiConsumed, geminiStep);
        }

        // 4. 结合生成请求裁决：
        var hasArgonGens = generations != null && generations.Any(g => IsArgon(g.Model));
        if (hasArgonGens)
        {
            // 当产生 Argon 请求时：
            // 若 Gemini 主池扣减显著（>= 5%）且扣减量显著主导（高于 Argon 扣减 3 倍以上或 Argon 扣减 < 1%），判定为扣减 Gemini 主池
            if (geminiDeduction >= 0.05 && (geminiDeduction > argonDeduction * 3 || argonDeduction < 0.01))
            {
                return "gemini";
            }

            // 若 Argon 专属池有实质扣减（>= 0.5%）或主导扣减，判定为扣减 Argon 专属池
            if (argonDeduction >= 0.005)
            {
                return "argon";
            }
        }
        else
        {
            // 若尚未检测到请求或生成未传入，若 Argon 专属池已有实质扣减，直接认定
            if (argonDeduction >= 0.005)
            {
                return "argon";
            }
        }

        // 默认根据是否存在有效 Argon 专属配额桶归入专属池
        return "argon";
    }

    public static bool SnapshotMatchesPool(QuotaSnapshot snapshot, string poolCategory)
    {
        var id = $"{snapshot.ModelOrPoolId} {snapshot.DisplayLabel}".ToLowerInvariant();
        if (poolCategory == "argon")
        {
            return id.Contains("argon", StringComparison.OrdinalIgnoreCase);
        }
        if (poolCategory == "gemini")
        {
            return id.Contains("gemini", StringComparison.OrdinalIgnoreCase) && !id.Contains("argon", StringComparison.OrdinalIgnoreCase);
        }
        return (id.Contains("3p", StringComparison.OrdinalIgnoreCase) ||
               id.Contains("claude", StringComparison.OrdinalIgnoreCase) ||
               id.Contains("gpt", StringComparison.OrdinalIgnoreCase) ||
               id.Contains("other", StringComparison.OrdinalIgnoreCase)) &&
               !id.Contains("gemini", StringComparison.OrdinalIgnoreCase) &&
               !id.Contains("argon", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<AntigravityQuotaEstimate> EstimateAll(
        IReadOnlyList<QuotaSnapshot> allSnapshots,
        IReadOnlyList<AntigravityGenerationUsage> allGenerations)
    {
        var results = new List<AntigravityQuotaEstimate>();

        var argonPool = ResolveArgonQuotaPool(allSnapshots, allGenerations);
        var hasArgonSnapshot = allSnapshots.Any(s => SnapshotMatchesPool(s, "argon"));

        var poolList = new List<(string Category, string Display)>
        {
            ("gemini", "Gemini Models"),
            ("3p", "Claude and GPT models")
        };

        if (hasArgonSnapshot && argonPool == "argon")
        {
            poolList.Add(("argon", "Gemini 4 Argon"));
        }

        var windows = new[] { "5h", "weekly" };

        foreach (var pool in poolList)
        {
            foreach (var win in windows)
            {
                var estimate = EstimatePoolWindow(pool.Category, pool.Display, win, allSnapshots, allGenerations, argonPool);
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
        IReadOnlyList<AntigravityGenerationUsage> allGenerations,
        string? resolvedArgonPool = null)
    {
        var argonPool = resolvedArgonPool ?? ResolveArgonQuotaPool(allSnapshots, allGenerations);

        // Filter snapshots
        var matchingSnapshots = allSnapshots
            .Where(s => SnapshotMatchesPool(s, poolCategory) && IsMatchingWindow(s, windowKind) && s.RemainingFraction.HasValue)
            .OrderBy(s => s.CapturedAt)
            .ToList();

        if (matchingSnapshots.Count == 0) return null;

        var latestSnapshot = matchingSnapshots.Last();
        var poolGenerations = allGenerations
            .Where(g => GetModelQuotaPool(g.Model, argonPool) == poolCategory)
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
