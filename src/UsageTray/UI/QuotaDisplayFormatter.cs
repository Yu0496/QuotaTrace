using UsageTray.App;
using UsageTray.Core;
using UsageTray.Providers.Antigravity;
using UsageTray.Services;

namespace UsageTray.UI;

public static class QuotaDisplayFormatter
{
    public static string BuildPopupText(DashboardSnapshot snapshot, bool enableCodex = true, bool enableAntigravity = true)
    {
        var sections = new List<string>();
        if (enableAntigravity) sections.Add(BuildAntigravitySection(snapshot));
        if (enableCodex) sections.Add(BuildCodexSection(snapshot));

        var refreshedText = I18n.T("界面刷新：", "Refreshed: ");
        var noteText = I18n.T("订阅参考金额按固定基准计量，并非账单或可用余额；满额金额仅按本机样本外推。",
            "Subscription reference values are calculated using fixed benchmarks and do not represent actual bills or balances; full quota estimates are projected solely from local samples.");

        return string.Join($"{Environment.NewLine}{Environment.NewLine}", sections) +
               $"{Environment.NewLine}{Environment.NewLine}{refreshedText}{snapshot.RefreshedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}" +
               $"{Environment.NewLine}{noteText}";
    }

    public static string BuildCompactText(DashboardSnapshot snapshot, bool enableCodex = true, bool enableAntigravity = true)
    {
        string? agPart = null;
        if (enableAntigravity)
        {
            var antigravitySnapshots = snapshot.Quotas
                .Where(item => item.Snapshot.Provider == ProviderKind.Antigravity)
                .Select(item => item.Snapshot).ToList();
            var ag5h = antigravitySnapshots.Where(IsFiveHour).OrderBy(s => s.RemainingFraction ?? 1.0).FirstOrDefault();
            var agWeekly = antigravitySnapshots.Where(IsWeekly).OrderBy(s => s.RemainingFraction ?? 1.0).FirstOrDefault();

            var shortText = FormatCompactWithResetOnZero(ag5h);
            var weeklyText = FormatCompactWithResetOnZero(agWeekly);
            var wkLabel = I18n.T("周", "Wk");
            agPart = $"AG 5h {shortText} / {wkLabel} {weeklyText}";
        }

        string? codexText = null;
        if (enableCodex)
        {
            var codexModels = snapshot.Models.Where(item => item.Provider == ProviderKind.Codex).ToList();
            var codexQuota = snapshot.Quotas
                .Where(item => item.Snapshot.Provider == ProviderKind.Codex)
                .Select(item => item.Snapshot).ToList();

            var stdWeekly = codexQuota
                .Where(s => IsWeekly(s) && !UsageAggregator.IsSparkSnapshot(s) && !IsReserve(s))
                .OrderByDescending(s => s.CapturedAt)
                .FirstOrDefault();
            var std5h = codexQuota
                .Where(s => IsFiveHour(s) && !UsageAggregator.IsSparkSnapshot(s) && !IsReserve(s))
                .OrderByDescending(s => s.CapturedAt)
                .FirstOrDefault();
            var sparkWeekly = codexQuota
                .Where(s => IsWeekly(s) && UsageAggregator.IsSparkSnapshot(s))
                .OrderByDescending(s => s.CapturedAt)
                .FirstOrDefault();
            var codexReserve = codexQuota
                .Where(IsReserve)
                .OrderByDescending(s => s.CapturedAt)
                .FirstOrDefault();

            var wkLabel = I18n.T("周", "Wk");
            var resLabel = I18n.T("备用", "Res");

            if (stdWeekly != null)
            {
                var isWeeklyZero = stdWeekly.RemainingFraction.HasValue && stdWeekly.RemainingFraction.Value <= 0.0001 && !stdWeekly.IsResetPassed();
                var is5hZero = std5h is { RemainingFraction: not null } && std5h.RemainingFraction.Value <= 0.0001 && !std5h.IsResetPassed();

                string stdPart;
                if (isWeeklyZero)
                {
                    stdPart = $"0%({FormatCompactReset(stdWeekly)})";
                }
                else if (is5hZero)
                {
                    stdPart = $"5h 0%({FormatCompactReset(std5h!)}) / {wkLabel} {FormatCompact(stdWeekly)}";
                }
                else
                {
                    stdPart = FormatCompact(stdWeekly);
                }

                if (sparkWeekly != null && sparkWeekly.RemainingFraction.HasValue)
                {
                    codexText = $"{stdPart} / Spark {FormatCompact(sparkWeekly)}";
                }
                else
                {
                    codexText = stdPart;
                }

                if (codexReserve != null && codexReserve.RemainingFraction.HasValue)
                {
                    codexText += $" ({resLabel} {FormatCompact(codexReserve)})";
                }
            }
            else if (sparkWeekly != null)
            {
                codexText = $"Spark {FormatCompactWithResetOnZero(sparkWeekly)}";
            }
            else if (codexReserve != null)
            {
                codexText = $"{resLabel} {FormatCompactWithResetOnZero(codexReserve)}";
            }
            else if (std5h != null)
            {
                codexText = FormatCompactWithResetOnZero(std5h);
            }
            else
            {
                codexText = codexModels.Count == 0 ? I18n.T("无日志", "No logs") : GetKnownCost(codexModels).HasValue ? "$" + GetKnownCost(codexModels)!.Value.ToString("0.00") : "—";
            }
        }

        string result;
        if (enableAntigravity && enableCodex)
        {
            result = $"{agPart} | Codex {codexText}";
        }
        else if (enableAntigravity)
        {
            result = agPart ?? "AG: —";
        }
        else if (enableCodex)
        {
            result = $"Codex {codexText}";
        }
        else
        {
            result = "QuotaTrace";
        }

        if (result.Length > 63) result = result[..63];
        return result;
    }

    private static string FormatCompactWithResetOnZero(QuotaSnapshot? snapshot)
    {
        if (snapshot is null) return "—";
        if (snapshot.IsResetPassed()) return I18n.T("待同步", "Syncing");
        if (!snapshot.HasValidFraction || !snapshot.RemainingFraction.HasValue) return I18n.T("未知", "Unknown");
        if (snapshot.RemainingFraction.Value <= 0.0001 && !snapshot.IsStale())
            return $"0%({FormatCompactReset(snapshot)})";
        return FormatCompact(snapshot);
    }

    private static string FormatCompactReset(QuotaSnapshot snapshot)
    {
        if (!snapshot.ResetAt.HasValue) return I18n.T("未知", "Unknown");
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
            views.All(item => item.IsOffline)
            ? I18n.T("离线，显示最后一次快照", "Offline, showing last snapshot")
            : I18n.T("当前快照", "Active snapshot");

        var titlePrefix = I18n.T("Antigravity 额度与用量", "Antigravity Quota & Usage");
        var statusPrefix = I18n.T("状态：", "Status: ");
        var lines = new List<string>
        {
            $"{titlePrefix}{(string.IsNullOrWhiteSpace(plan) ? string.Empty : $"（{plan}）")}",
            $"{statusPrefix}{status}"
        };

        var ag5h = snapshots.Where(IsFiveHour).ToList();
        if (ag5h.Count > 0) lines.Add(FormatWindow(I18n.T("5 小时窗口", "5-Hour Window"), ag5h));
        var agWeekly = snapshots.Where(IsWeekly).ToList();
        if (agWeekly.Count > 0) lines.Add(FormatWindow(I18n.T("周窗口", "Weekly Window"), agWeekly));
        var other = snapshots.Where(item => !IsFiveHour(item) && !IsWeekly(item)).ToList();
        if (other.Count > 0) lines.Add(FormatWindow(I18n.T("其他窗口", "Other Windows"), other));
        if (snapshots.Count == 0)
            lines.Add(I18n.T("暂无可用 quota 快照，请先刷新并确保 Antigravity 正在运行。", "No quota snapshots available. Refresh and ensure Antigravity is running."));

        foreach (var est in snapshot.AntigravityEstimates ?? [])
        {
            var isStale = est.CalculationDetails == "快照待更新";
            var badge = isStale ? I18n.T("（待更新）", " (Pending Update)") : I18n.T("（仅本机样本外推）", " (Local Sample Projection)");
            var value = est.EstimatedFullQuotaUsd.HasValue ? $"约 ${est.EstimatedFullQuotaUsd:0.00}{badge}" : est.CalculationDetails ?? I18n.T("样本不足", "Insufficient samples");
            var projLabel = I18n.T("满额样本外推：", "Est. Full Quota: ");
            lines.Add($"{est.DisplayName} {est.WindowKind} {projLabel}{value}");
        }

        if (models.Count > 0)
        {
            var input = models.Sum(item => item.NonCachedInputTokens);
            var cacheRead = models.Sum(item => item.CachedTokens);
            var cacheCreation = models.Sum(item => item.CacheCreationTokens);
            var output = models.Sum(item => item.OutputTokens);
            var inputLbl = I18n.T("Input（未命中）：", "Input (Uncached): ");
            var cacheReadLbl = I18n.T("；Cache Read：", "; Cache Read: ");
            var cacheCreationLbl = I18n.T("Cache Creation：", "Cache Creation: ");
            var outputLbl = I18n.T("；Output：", "; Output: ");
            var subRefLbl = I18n.T("订阅参考金额：", "Sub Ref Value: ");
            var rangeLbl = I18n.T("（范围 ", " (Range ");
            lines.Add($"{inputLbl}{FormatTokens(input)}{cacheReadLbl}{FormatTokens(cacheRead)}");
            lines.Add($"{cacheCreationLbl}{FormatTokens(cacheCreation)}{outputLbl}{FormatTokens(output)}");
            lines.Add($"{subRefLbl}{FormatCost(GetKnownCost(models))}{rangeLbl}{FormatRange(snapshot.Range)}）");
        }
        else
        {
            lines.Add(I18n.T("本次统计范围没有可显示的 Antigravity token。", "No Antigravity tokens found in the selected range."));
        }

        return string.Join(Environment.NewLine, lines);
    }

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

        var titlePrefix = I18n.T("Codex 额度与用量", "Codex Quota & Usage");
        var lines = new List<string>
        {
            $"{titlePrefix}{(string.IsNullOrWhiteSpace(codexPlan) ? string.Empty : $"（{codexPlan}）")}"
        };

        if (codexQuotas.Count > 0)
        {
            var codex5h = codexQuotas.Where(IsFiveHour).ToList();
            if (codex5h.Count > 0) lines.Add(FormatWindow(I18n.T("5 小时窗口", "5-Hour Window"), codex5h));
            var codexWeekly = codexQuotas.Where(IsWeekly).ToList();
            if (codexWeekly.Count > 0) lines.Add(FormatWindow(I18n.T("周窗口", "Weekly Window"), codexWeekly));
            var others = codexQuotas.Where(s => !IsFiveHour(s) && !IsWeekly(s)).ToList();
            if (others.Count > 0) lines.Add(FormatWindow(I18n.T("其他窗口", "Other Windows"), others));
        }
        else
        {
            lines.Add(I18n.T("暂无可用 quota 快照（当前 session 未写入 rate_limits）", "No quota snapshots available (rate_limits not written to active sessions)"));
        }

        var codexCycles = snapshot.CodexWeeklyCycles.Count > 0
            ? snapshot.CodexWeeklyCycles
            : snapshot.CodexWeeklyCycle != null
                ? [snapshot.CodexWeeklyCycle]
                : [];

        foreach (var cycle in codexCycles)
        {
            var usedText = cycle.UsedFraction.HasValue ? $"{cycle.UsedFraction.Value:P0}" : I18n.T("未知", "Unknown");
            var cycleCostText = cycle.CycleCostUsd.HasValue ? "$" + cycle.CycleCostUsd.Value.ToString("0.00") : "—";
            var isStale = cycle.EstimateNote == "快照待更新";
            var badge = isStale ? I18n.T("（待更新）", " (Pending Update)") : "";
            var estCostText = cycle.EstimatedWeeklyCostUsd.HasValue ? $"约 ${cycle.EstimatedWeeklyCostUsd.Value:0.00}{badge}" : cycle.EstimateNote ?? I18n.T("样本不足", "Insufficient samples");
            var prefix = codexCycles.Count > 1 ? $"{cycle.PoolName} " : string.Empty;
            var subRefLbl = I18n.T("本轮订阅参考金额：", "Cycle Sub Ref Value: ");
            var usedLbl = I18n.T("（已消耗 ", " (Consumed ");
            var estLbl = I18n.T("满额样本外推：", "Est. Full Quota: ");
            lines.Add($"{prefix}{subRefLbl}{cycleCostText}{usedLbl}{usedText}）");
            lines.Add($"{prefix}{estLbl}{estCostText}");
        }

        if (models.Count > 0)
        {
            var input = models.Sum(item => item.NonCachedInputTokens);
            var cacheRead = models.Sum(item => item.CachedTokens);
            var cacheCreation = models.Sum(item => item.CacheCreationTokens);
            var output = models.Sum(item => item.OutputTokens);
            var inputLbl = I18n.T("Input（未命中）：", "Input (Uncached): ");
            var cacheReadLbl = I18n.T("；Cache Read：", "; Cache Read: ");
            var cacheCreationLbl = I18n.T("Cache Creation：", "Cache Creation: ");
            var outputLbl = I18n.T("；Output：", "; Output: ");
            var subRefLbl = I18n.T("订阅参考金额：", "Sub Ref Value: ");
            var rangeLbl = I18n.T("（范围 ", " (Range ");
            lines.Add($"{inputLbl}{FormatTokens(input)}{cacheReadLbl}{FormatTokens(cacheRead)}");
            lines.Add($"{cacheCreationLbl}{FormatTokens(cacheCreation)}{outputLbl}{FormatTokens(output)}");
            lines.Add($"{subRefLbl}{FormatCost(GetKnownCost(models))}{rangeLbl}{FormatRange(snapshot.Range)}）");
        }
        else
        {
            lines.Add(I18n.T("本次统计范围没有可显示的 Codex token。", "No Codex tokens found in the selected range."));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatWindow(string label, IReadOnlyList<QuotaSnapshot> snapshots)
    {
        if (snapshots.Count == 0) return $"{label}：{I18n.T("暂无数据", "No data")}";
        var sampleLbl = I18n.T("采样 ", "Sampled ");
        var resetLbl = I18n.T("；重置 ", "; Reset ");
        var values = snapshots.Select(snapshot =>
            $"{ShortLabel(snapshot)} {FormatRemaining(snapshot)}（{sampleLbl}{snapshot.CapturedAt.ToLocalTime():MM-dd HH:mm}{resetLbl}{FormatReset(snapshot)}）");
        return $"{label}：{string.Join("；", values)}";
    }

    private static string FormatCompact(QuotaSnapshot? snapshot)
    {
        if (snapshot is null) return "—";
        if (snapshot.IsResetPassed()) return I18n.T("待同步", "Syncing");
        var eff = snapshot.EffectiveRemainingFraction();
        var staleText = snapshot.IsStale() ? I18n.T("旧", "Stale") : "";
        return eff.HasValue ? $"{eff.Value:P0}{staleText}" : I18n.T("未知", "Unknown");
    }

    private static string FormatRemaining(QuotaSnapshot snapshot)
    {
        if (!snapshot.HasValidFraction || !snapshot.RemainingFraction.HasValue) return I18n.T("剩余未知", "Remaining Unknown");
        if (snapshot.IsResetPassed()) return I18n.Format("待同步（上次 {0:P0}）", "Syncing (Last {0:P0})", snapshot.RemainingFraction.Value);
        var staleNote = snapshot.IsStale() ? I18n.T("（旧快照）", " (Stale)") : "";
        return I18n.Format("{0:P0} 剩余{1}", "{0:P0} Remaining{1}", snapshot.RemainingFraction.Value, staleNote);
    }

    private static string FormatReset(QuotaSnapshot snapshot) =>
        TimeFormatter.FormatResetWithRelative(snapshot.ResetAt, "yyyy-MM-dd HH:mm");

    private static string ShortLabel(QuotaSnapshot snapshot)
    {
        var label = string.IsNullOrWhiteSpace(snapshot.DisplayLabel) ||
            string.Equals(snapshot.DisplayLabel, snapshot.ModelOrPoolId, StringComparison.OrdinalIgnoreCase)
            ? snapshot.ModelOrPoolId
            : snapshot.DisplayLabel;
        return label
            .Replace("(5小时额度)", "")
            .Replace("(周额度)", "")
            .Replace("(5h)", "")
            .Replace("(weekly)", "")
            .Trim();
    }

    private static bool IsReserve(QuotaSnapshot snapshot) =>
        snapshot.ModelOrPoolId.Contains("reserve", StringComparison.OrdinalIgnoreCase) ||
        snapshot.DisplayLabel.Contains("reserve", StringComparison.OrdinalIgnoreCase);

    private static decimal? GetKnownCost(IReadOnlyList<ModelUsageView> models) =>
        models.Count > 0 && models.All(item => item.ApiEquivalentUsd.HasValue)
            ? models.Sum(item => item.ApiEquivalentUsd!.Value)
            : null;

    private static string FormatCost(decimal? value) => value.HasValue ? "$" + value.Value.ToString("0.00") : I18n.T("—（含未定价用量）", "— (Includes unpriced usage)");

    private static string FormatTokens(long value) => value switch
    {
        >= 1_000_000 => $"{value / 1_000_000d:0.##}M",
        >= 1_000 => $"{value / 1_000d:0.##}K",
        _ => value.ToString("N0")
    };

    private static string FormatRange(DateRange range) => range.From == range.To
        ? range.From.ToString("yyyy-MM-dd")
        : I18n.Format("{0:yyyy-MM-dd} 至 {1:yyyy-MM-dd}", "{0:yyyy-MM-dd} to {1:yyyy-MM-dd}", range.From, range.To);

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
