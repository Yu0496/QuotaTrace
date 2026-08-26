namespace UsageTray.UI;

public static class TimeFormatter
{
    /// <summary>
    /// 将未来的目标时间格式化为“X天X小时X分后”、“X小时X分后”、“X分后”或“即将重置”等相对描述。
    /// </summary>
    public static string FormatRelativeFuture(DateTimeOffset target, DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.Now;
        var diff = target - current;

        if (diff <= TimeSpan.Zero)
        {
            return diff.TotalHours < -1 ? "已重置" : "即将重置";
        }

        var days = diff.Days;
        var hours = diff.Hours;
        var minutes = diff.Minutes;

        if (days > 0)
        {
            if (hours > 0 && minutes > 0)
                return $"{days}天{hours}小时{minutes}分后";
            if (hours > 0)
                return $"{days}天{hours}小时后";
            if (minutes > 0)
                return $"{days}天{minutes}分后";
            return $"{days}天后";
        }

        if (hours > 0)
        {
            if (minutes > 0)
                return $"{hours}小时{minutes}分后";
            return $"{hours}小时后";
        }

        if (minutes > 0)
        {
            return $"{minutes}分后";
        }

        return "不到1分钟后";
    }

    /// <summary>
    /// 格式化重置时间，输出形如“08-27 02:59，4小时50分后”的字符串。
    /// </summary>
    public static string FormatResetWithRelative(DateTimeOffset? resetAt, string dateFormat = "MM-dd HH:mm", DateTimeOffset? now = null)
    {
        if (!resetAt.HasValue) return "未知";
        var local = resetAt.Value.ToLocalTime();
        var dateStr = local.ToString(dateFormat);
        var relStr = FormatRelativeFuture(resetAt.Value, now);
        return string.IsNullOrEmpty(relStr) ? dateStr : $"{dateStr}，{relStr}";
    }
}
