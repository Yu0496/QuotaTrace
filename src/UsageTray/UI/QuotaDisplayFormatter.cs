using UsageTray.Core;
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
        var shortText = FormatCompact(antigravitySnapshots.FirstOrDefault(IsFiveHour));
        var weeklyText = FormatCompact(antigravitySnapshots.FirstOrDefault(IsWeekly));
        var codexModels = snapshot.Models.Where(item => item.Provider == ProviderKind.Codex).ToList();
        var codexQuota = snapshot.Quotas
            .Where(item => item.Snapshot.Provider == ProviderKind.Codex)
            .Select(item => item.Snapshot).ToList();
        var codexText = codexQuota.Count > 0
            ? $"5h {FormatCompact(codexQuota.FirstOrDefault(IsFiveHour))}"
            : codexModels.Count == 0 ? "无日志" : GetKnownCost(codexModels).HasValue ? "$" + GetKnownCost(codexModels)!.Value.ToString("0.00") : "—";
        var result = $"AG 5h {shortText} | 周 {weeklyText} | Codex {codexText}";
        if (result.Length > 63) result = result[..63];
        return result;
    }

    private static string BuildAntigravitySection(DashboardSnapshot snapshot)
    {
        var snapshots = snapshot.Quotas.Where(item => item.Snapshot.Provider == ProviderKind.Antigravity)
            .Select(item => item.Snapshot).ToList();
        var plan = snapshots.Select(item => item.PlanTier).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var status = snapshot.Quotas.Where(item => item.Snapshot.Provider == ProviderKind.Antigravity).ToList() is { Count: > 0 } views &&
            views.All(item => item.IsOffline) ? "离线，显示最后一次快照" : "当前快照";
        var lines = new List<string>
        {
            $"Antigravity 额度{(string.IsNullOrWhiteSpace(plan) ? string.Empty : $"（{plan}）")}",
            $"状态：{status}",
            FormatWindow("5 小时窗口", snapshots.Where(IsFiveHour).ToList()),
            FormatWindow("周窗口", snapshots.Where(IsWeekly).ToList())
        };
        var other = snapshots.Where(item => !IsFiveHour(item) && !IsWeekly(item)).ToList();
        if (other.Count > 0) lines.Add(FormatWindow("其他窗口", other));
        if (snapshots.Count == 0)
            lines.Add("暂无可用 quota 快照，请先刷新并确保 Antigravity 正在运行。");
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildCodexSection(DashboardSnapshot snapshot)
    {
        var models = snapshot.Models.Where(item => item.Provider == ProviderKind.Codex).ToList();
        var codexQuotaViews = snapshot.Quotas.Where(item => item.Snapshot.Provider == ProviderKind.Codex).ToList();
        var codexQuotas = codexQuotaViews.Select(item => item.Snapshot).ToList();
        var lines = new List<string>
        {
            "Codex 用量（本地 session 日志）",
            $"状态：{(models.Count == 0 ? "本次范围未发现可用日志" : "已读取本地 Codex session JSONL")}"
        };
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

        if (codexQuotas.Count > 0)
        {
            var status = codexQuotaViews.All(item => item.IsOffline) ? "离线，显示最后一次日志快照" : "当前日志快照";
            lines.Add($"额度状态：{status}");
            lines.Add(FormatWindow("5 小时窗口", codexQuotas.Where(IsFiveHour).ToList()));
            lines.Add(FormatWindow("周窗口", codexQuotas.Where(IsWeekly).ToList()));
        }
        else
        {
            lines.Add("5 小时窗口：剩余未知；重置时间未知（当前 session 未写入 rate_limits）");
            lines.Add("周窗口：剩余未知；重置时间未知（当前 session 未写入 rate_limits）");
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

    private static string FormatCompact(QuotaSnapshot? snapshot) => snapshot is null
        ? "—"
        : snapshot.RemainingFraction.HasValue ? $"{snapshot.RemainingFraction.Value:P0}" : "未知";

    private static string FormatRemaining(QuotaSnapshot snapshot) => snapshot.RemainingFraction.HasValue
        ? $"{snapshot.RemainingFraction.Value:P0} 剩余"
        : "剩余未知";

    private static string FormatReset(QuotaSnapshot snapshot) => snapshot.ResetAt.HasValue
        ? snapshot.ResetAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        : "未知";

    private static string ShortLabel(QuotaSnapshot snapshot) => string.IsNullOrWhiteSpace(snapshot.DisplayLabel) ||
        string.Equals(snapshot.DisplayLabel, snapshot.ModelOrPoolId, StringComparison.OrdinalIgnoreCase)
        ? snapshot.ModelOrPoolId
        : snapshot.DisplayLabel;

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
