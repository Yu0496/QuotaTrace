using System.Text.Json;
using UsageTray.Core;

namespace UsageTray.Providers.Antigravity;

public static class AntigravityManualQuotaHelper
{
    public static DateTimeOffset CalculateResetAtFromCountdown(DateTimeOffset now, int days, int hours, int minutes)
    {
        if (days < 0 || days > 7) throw new ArgumentOutOfRangeException(nameof(days), "天数必须在 0 到 7 之间。");
        if (hours < 0 || hours > 23) throw new ArgumentOutOfRangeException(nameof(hours), "小时必须在 0 到 23 之间。");
        if (minutes < 0 || minutes > 59) throw new ArgumentOutOfRangeException(nameof(minutes), "分钟必须在 0 到 59 之间。");

        var span = new TimeSpan(days, hours, minutes, 0);
        if (span <= TimeSpan.Zero || span > TimeSpan.FromDays(7))
        {
            throw new ArgumentOutOfRangeException(nameof(days), "剩余重置时间必须大于 0 分钟且不超过 7 天。");
        }

        return now.Add(span);
    }

    public static (int Days, int Hours, int Minutes) SplitCountdown(DateTimeOffset now, DateTimeOffset resetAt)
    {
        var remaining = resetAt - now;
        if (remaining <= TimeSpan.Zero) return (0, 0, 0);

        var totalMinutes = Math.Clamp((int)Math.Round(remaining.TotalMinutes), 1, 7 * 24 * 60);
        var days = totalMinutes / (24 * 60);
        var remMinutes = totalMinutes % (24 * 60);
        var hours = remMinutes / 60;
        var minutes = remMinutes % 60;
        return (days, hours, minutes);
    }

    public static QuotaSnapshot CreateManualWeeklySnapshot(
        string poolCategory,
        double remainingPercent,
        DateTimeOffset resetAt,
        DateTimeOffset? capturedAt = null)
    {
        if (double.IsNaN(remainingPercent) || double.IsInfinity(remainingPercent) || remainingPercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(remainingPercent), "剩余额度百分比必须在 0% 到 100% 之间。");
        }

        var at = (capturedAt ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var utcReset = resetAt.ToUniversalTime();
        if (utcReset <= at)
        {
            throw new ArgumentOutOfRangeException(nameof(resetAt), "重置时间必须晚于当前时间。");
        }
        if (utcReset - at > TimeSpan.FromDays(7) + TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(resetAt), "周额度重置时间距离现在不能超过 7 天。");
        }

        var normalizedPool = poolCategory.Trim().ToLowerInvariant();
        var (poolId, label) = normalizedPool switch
        {
            "gemini" => ("gemini-weekly", "Gemini (周额度)"),
            "3p" or "claude" => ("3p-weekly", "Claude / GPT (周额度)"),
            _ => throw new ArgumentException($"未知的 Antigravity 模型池：{poolCategory}", nameof(poolCategory))
        };

        return new QuotaSnapshot(
            ProviderKind.Antigravity,
            at,
            poolId,
            label,
            Math.Round(remainingPercent / 100.0, 6),
            utcReset,
            "weekly",
            QuotaSources.AntigravityManual,
            null);
    }

    public static string BuildAgyStatusLineConfigSnippet(string executablePath)
    {
        var path = string.IsNullOrWhiteSpace(executablePath) ? "UsageTray.exe" : executablePath.Trim();
        var payload = new
        {
            statusLine = new
            {
                type = "command",
                command = $"\"{path}\" --status-recorder",
                enabled = true,
                stack_with_default = true
            }
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }
}
