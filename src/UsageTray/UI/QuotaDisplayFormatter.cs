using UsageTray.Core;
using UsageTray.Providers.Antigravity;
using UsageTray.Services;

namespace UsageTray.UI;

internal static class QuotaDisplayFormatter
{
    public static string BuildPopupText(DashboardSnapshot snapshot)
    {
        var sections = new[]
        {
            BuildAntigravitySection(snapshot),
            BuildCodexSection(snapshot)
        };
        var captured = snapshot.Quotas.Select(item => item.Snapshot.CapturedAt)
            .Append(snapshot.RefreshedAt)
            .Max()
            .ToLocalTime();
        return string.Join($"{Environment.NewLine}{Environment.NewLine}", sections) +
               $"{Environment.NewLine}{Environment.NewLine}采样时间：{captured:yyyy-MM-dd HH:mm:ss}";
    }

    public static string BuildCompactText(DashboardSnapshot snapshot)
    {
        var antigravitySnapshots = snapshot.Quotas
            .Where(item => item.Snapshot.Provider == ProviderKind.Antigravity)
            .Select(item => item.Snapshot).ToList();
        var ag5h = antigravitySnapshots.Where(IsFiveHour).OrderBy(s => s.RemainingFraction ?? 1.0).FirstOrDefault();
        var agWeekly = antigravitySnapshots.Where(IsWeekly).OrderBy(s => s.RemainingFraction ?? 1.0).FirstOrDefault();

        var shortText = FormatCompactWithResetOnZero(ag5h);
        var weeklyText = FormatCompactWithResetOnZero(agWeekly);

        var codexModels = snapshot.Models.Where(item => item.Provider == ProviderKind.Codex).ToList();
        var codexQuota = snapshot.Quotas
            .Where(item => item.Snapshot.Provider == ProviderKind.Codex)
            .Select(item => item.Snapshot).ToList();
        var codexWeekly = codexQuota.Where(s => IsWeekly(s) && !IsReserve(s)).OrderBy(s => s.RemainingFraction ?? 1.0).FirstOrDefault();
        var codex5h = codexQuota.Where(IsFiveHour).OrderBy(s => s.RemainingFraction ?? 1.0).FirstOrDefault();
        var codexReserve = codexQuota.Where(IsReserve).OrderBy(s => s.RemainingFraction ?? 1.0).FirstOrDefault();

        string codexText;
        if (codexWeekly != null)
        {
            var isWeeklyZero = codexWeekly.RemainingFraction.HasValue && codexWeekly.RemainingFraction.Value <= 0.0001 && !codexWeekly.IsResetPassed();
            var is5hZero = codex5h is { RemainingFraction: not null } && codex5h.RemainingFraction.Value <= 0.0001 && !codex5h.IsResetPassed();

            if (isWeeklyZero)
            {
                codexText = $"0%({FormatCompactReset(codexWeekly)})";
            }
            else if (is5hZero)
            {
                codexText = $"5h 0%({FormatCompactReset(codex5h!)}) / 周 {FormatCompact(codexWeekly)}";
            }
            else
            {
                codexText = FormatCompact(codexWeekly);
            }

            if (codexReserve != null && codexReserve.RemainingFraction.HasValue)
            {
                codexText += $" (备用 {FormatCompact(codexReserve)})";
            }
        }
        else if (codexReserve != null)
        {
            codexText = $"备用 {FormatCompactWithResetOnZero(codexReserve)}";
        }
        else if (codex5h != null)
        {
            codexText = FormatCompactWithResetOnZero(codex5h);
        }
        else
        {
            codexText = codexModels.Count == 0 ? "无日志" : GetKnownCost(codexModels).HasValue ? "$" + GetKnownCost(codexModels)!.Value.ToString("0.00") : "—";
        }

        var agPart = $"AG 5h {shortText} / 周 {weeklyText}";
        var result = $"{agPart} | Codex {codexText}";
        if (result.Length > 63) result = result[..63];
        return result;
    }

    private static string FormatCompactWithResetOnZero(QuotaSnapshot? snapshot)
    {
        if (snapshot == null) return "—";
        if (!snapshot.RemainingFraction.HasValue) return "未知";
        var frac = snapshot.RemainingFraction.Value;
        if (frac <= 0.0001)
        {
            if (snapshot.IsResetPassed()) return "100%";
            return $"0%({FormatCompactReset(snapshot)})";
        }
        return $"{frac:P0}";
    }



    private static string FormatCompactReset(QuotaSnapshot snapshot)
    {
        if (!snapshot.ResetAt.HasValue) return "未知";
        var local = snapshot.ResetAt.Value.ToLocalTime();
        return local.Date == DateTime.Today
            ? local.ToString("HH:mm")
            : local.ToString("MM-dd HH:mm");
    }

    private static string BuildAntigravitySection(DashboardSnapshot snapshot)
    {
        var models = snapshot.Models.Where(item => item.Provider == ProviderKind.Antigravity).ToList();
        var snapshots = snapshot.Quotas.Where(item => item.Snapshot.Provider == ProviderKind.Antigravity)
            .Select(item => item.Snapshot).ToList();
        var plan = snapshots
            .Where(item => !string.IsNullOrWhiteSpace(item.PlanTier))
            .OrderByDescending(item => item.CapturedAt)
            .Select(item => item.PlanTier)
            .FirstOrDefault();
        var status = snapshot.Quotas.Where(item => item.Snapshot.Provider == ProviderKind.Antigravity).ToList() is { Count: > 0 } views &&
            views.All(item => item.IsOffline) ? "离线，显示最后一次快照" : "当前快照";
        var lines = new List<string>
        {
            $"Antigravity 额度与用量{(string.IsNullOrWhiteSpace(plan) ? string.Empty : $"（{plan}）")}",
            $"状态：{status}"
        };
        var ag5h = snapshots.Where(IsFiveHour).ToList();
        if (ag5h.Count > 0) lines.Add(FormatWindow("5 小时窗口", ag5h));
        var agWeekly = snapshots.Where(IsWeekly).ToList();
        if (agWeekly.Count > 0) lines.Add(FormatWindow("周窗口", agWeekly));
        var other = snapshots.Where(item => !IsFiveHour(item) && !IsWeekly(item)).ToList();
        if (other.Count > 0) lines.Add(FormatWindow("其他窗口", other));
        if (snapshots.Count == 0)
            lines.Add("暂无可用 quota 快照，请先刷新并确保 Antigravity 正在运行。");

        // Quota Equivalent Estimates
        if (snapshot.AntigravityEstimates is { Count: > 0 } estimates)
        {
            var weeklyEst = estimates.FirstOrDefault(e => e.WindowKind == "weekly" && e.EstimatedFullQuotaUsd.HasValue);
            var fiveHourEst = estimates.FirstOrDefault(e => e.WindowKind == "5h" && e.EstimatedFullQuotaUsd.HasValue);
            if (weeklyEst?.EstimatedFullQuotaUsd is not null)
            {
                var conf = FormatConfidence(weeklyEst.Confidence);
                lines.Add($"完整 Weekly API 等值估算：约 ${weeklyEst.EstimatedFullQuotaUsd.Value:0.00}（置信度：{conf}）");
            }
            if (fiveHourEst?.EstimatedFullQuotaUsd is not null)
            {
                var conf = FormatConfidence(fiveHourEst.Confidence);
                lines.Add($"完整 5h API 等值估算：约 ${fiveHourEst.EstimatedFullQuotaUsd.Value:0.00}（置信度：{conf}）");
            }
        }

        if (models.Count > 0)
        {
            var input = models.Sum(item => item.NonCachedInputTokens);
            var cacheRead = models.Sum(item => item.CachedTokens);
            var cacheCreation = models.Sum(item => item.CacheCreationTokens);
            var output = models.Sum(item => item.OutputTokens);
            lines.Add($"Input（未命中）：{FormatTokens(input)}；Cache Read：{FormatTokens(cacheRead)}");
            lines.Add($"Cache Creation：{FormatTokens(cacheCreation)}；Output：{FormatTokens(output)}");
            lines.Add($"API 已用等值：{FormatCost(GetKnownCost(models))}（范围 {FormatRange(snapshot.Range)}）");
        }
        else
        {
            lines.Add("本次统计范围没有可显示的 Antigravity token。");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatConfidence(QuotaEstimateConfidence confidence) => confidence switch
    {
        QuotaEstimateConfidence.High => "High",
        QuotaEstimateConfidence.Medium => "Medium",
        _ => "Low"
    };

    private static string BuildCodexSection(DashboardSnapshot snapshot)
    {
        var models = snapshot.Models.Where(item => item.Provider == ProviderKind.Codex).ToList();
        var codexQuotaViews = snapshot.Quotas.Where(item => item.Snapshot.Provider == ProviderKind.Codex).ToList();
        var codexQuotas = codexQuotaViews.Select(item => item.Snapshot).ToList();
        var codexPlan = codexQuotas
            .Where(s => !IsReserve(s) && !string.IsNullOrWhiteSpace(s.PlanTier))
            .OrderByDescending(s => s.CapturedAt)
            .Select(s => s.PlanTier)
            .FirstOrDefault()
            ?? codexQuotas
            .Where(s => !string.IsNullOrWhiteSpace(s.PlanTier))
            .OrderByDescending(s => s.CapturedAt)
            .Select(s => s.PlanTier)
            .FirstOrDefault();
        var lines = new List<string>
        {
            $"Codex 额度与用量{(string.IsNullOrWhiteSpace(codexPlan) ? string.Empty : $"（{codexPlan}）")}"
        };

        if (codexQuotas.Count > 0)
        {
            var codex5h = codexQuotas.Where(IsFiveHour).ToList();
            if (codex5h.Count > 0) lines.Add(FormatWindow("5 小时窗口", codex5h));
            var codexWeekly = codexQuotas.Where(IsWeekly).ToList();
            if (codexWeekly.Count > 0) lines.Add(FormatWindow("周窗口", codexWeekly));
            var others = codexQuotas.Where(s => !IsFiveHour(s) && !IsWeekly(s)).ToList();
            if (others.Count > 0) lines.Add(FormatWindow("其他窗口", others));
        }
        else
        {
            lines.Add("暂无可用 quota 快照（当前 session 未写入 rate_limits）");
        }

        var codexCycles = snapshot.CodexWeeklyCycles.Count > 0
            ? snapshot.CodexWeeklyCycles
            : snapshot.CodexWeeklyCycle != null
                ? [snapshot.CodexWeeklyCycle]
                : [];

        foreach (var cycle in codexCycles)
        {
            var usedText = cycle.UsedFraction.HasValue ? $"{cycle.UsedFraction.Value:P0}" : "未知";
            var cycleCostText = cycle.CycleCostUsd.HasValue ? "$" + cycle.CycleCostUsd.Value.ToString("0.00") : "—";
            var estCostText = cycle.EstimatedWeeklyCostUsd.HasValue ? $"约 ${cycle.EstimatedWeeklyCostUsd.Value:0.00}" : "待产生消耗后推算";
            var prefix = codexCycles.Count > 1 ? $"{cycle.PoolName} " : string.Empty;
            lines.Add($"{prefix}本轮周消耗：{cycleCostText}（已消耗 {usedText}）");
            lines.Add($"{prefix}周满额预估：{estCostText}");
        }

        if (models.Count > 0)
        {
            var input = models.Sum(item => item.NonCachedInputTokens);
            var cacheRead = models.Sum(item => item.CachedTokens);
            var cacheCreation = models.Sum(item => item.CacheCreationTokens);
            var output = models.Sum(item => item.OutputTokens);
            lines.Add($"Input（未命中）：{FormatTokens(input)}；Cache Read：{FormatTokens(cacheRead)}");
            lines.Add($"Cache Creation：{FormatTokens(cacheCreation)}；Output：{FormatTokens(output)}");
            lines.Add($"API 等值：{FormatCost(GetKnownCost(models))}（范围 {FormatRange(snapshot.Range)}）");
        }
        else
        {
            lines.Add("本次统计范围没有可显示的 Codex token。");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatWindow(string label, IReadOnlyList<QuotaSnapshot> snapshots)
    {
        if (snapshots.Count == 0) return $"{label}：暂无数据";
        var values = snapshots.Select(snapshot =>
            $"{ShortLabel(snapshot)} {FormatRemaining(snapshot)}（重置 {FormatReset(snapshot)}）");
        return $"{label}：{string.Join("；", values)}";
    }

    private static string FormatCompact(QuotaSnapshot? snapshot)
    {
        if (snapshot is null) return "—";
        var eff = snapshot.EffectiveRemainingFraction();
        return eff.HasValue ? $"{eff.Value:P0}" : "未知";
    }

    private static string FormatRemaining(QuotaSnapshot snapshot)
    {
        if (!snapshot.RemainingFraction.HasValue) return "剩余未知";
        if (snapshot.IsResetPassed() && snapshot.RemainingFraction.Value <= 0.0001)
            return "100% 剩余 (推断已重置)";
        return $"{snapshot.RemainingFraction.Value:P0} 剩余";
    }



    private static string FormatReset(QuotaSnapshot snapshot) =>
        TimeFormatter.FormatResetWithRelative(snapshot.ResetAt, "yyyy-MM-dd HH:mm");

    private static string ShortLabel(QuotaSnapshot snapshot) => string.IsNullOrWhiteSpace(snapshot.DisplayLabel) ||
        string.Equals(snapshot.DisplayLabel, snapshot.ModelOrPoolId, StringComparison.OrdinalIgnoreCase)
        ? snapshot.ModelOrPoolId
        : snapshot.DisplayLabel;

    private static bool IsReserve(QuotaSnapshot snapshot) =>
        snapshot.ModelOrPoolId.Contains("reserve", StringComparison.OrdinalIgnoreCase) ||
        snapshot.DisplayLabel.Contains("reserve", StringComparison.OrdinalIgnoreCase);

    private static decimal? GetKnownCost(IReadOnlyList<ModelUsageView> models) =>
        models.Count > 0 && models.All(item => item.ApiEquivalentUsd.HasValue)
            ? models.Sum(item => item.ApiEquivalentUsd!.Value)
            : null;

    private static string FormatCost(decimal? value) => value.HasValue ? "$" + value.Value.ToString("0.00") : "—（部分模型未配置价格）";

    private static string FormatTokens(long value) => value switch
    {
        >= 1_000_000 => $"{value / 1_000_000d:0.##}M",
        >= 1_000 => $"{value / 1_000d:0.##}K",
        _ => value.ToString("N0")
    };

    private static string FormatRange(DateRange range) => range.From == range.To
        ? range.From.ToString("yyyy-MM-dd")
        : $"{range.From:yyyy-MM-dd} 至 {range.To:yyyy-MM-dd}";

    private static bool IsFiveHour(QuotaSnapshot snapshot)
    {
        var value = $"{snapshot.WindowKind} {snapshot.ModelOrPoolId} {snapshot.DisplayLabel}".ToLowerInvariant();
        return value.Contains("5h", StringComparison.Ordinal) || value.Contains("5 h", StringComparison.Ordinal) ||
            value.Contains("5-hour", StringComparison.Ordinal) || value.Contains("5 hour", StringComparison.Ordinal) ||
            value.Contains("five_hour", StringComparison.Ordinal) || value.Contains("five hour", StringComparison.Ordinal) ||
            value.Contains("5小时", StringComparison.Ordinal);
    }

    private static bool IsWeekly(QuotaSnapshot snapshot)
    {
        var value = $"{snapshot.WindowKind} {snapshot.ModelOrPoolId} {snapshot.DisplayLabel}".ToLowerInvariant();
        return value.Contains("week", StringComparison.Ordinal) || value.Contains("weekly", StringComparison.Ordinal) ||
            value.Contains("7-day", StringComparison.Ordinal) || value.Contains("7 day", StringComparison.Ordinal) ||
            value.Contains("7d", StringComparison.Ordinal) || value.Contains("周", StringComparison.Ordinal);
    }
}
