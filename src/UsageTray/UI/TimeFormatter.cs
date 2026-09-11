using UsageTray.App;

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
            return diff.TotalHours < -1 ? I18n.T("已重置", "Reset") : I18n.T("即将重置", "Resetting soon");
        }

        var days = diff.Days;
        var hours = diff.Hours;
        var minutes = diff.Minutes;

        if (days > 0)
        {
            if (hours > 0 && minutes > 0)
                return I18n.Format("{0}天{1}小时{2}分后", "{0}d {1}h {2}m left", days, hours, minutes);
            if (hours > 0)
                return I18n.Format("{0}天{1}小时后", "{0}d {1}h left", days, hours);
            if (minutes > 0)
                return I18n.Format("{0}天{1}分后", "{0}d {1}m left", days, minutes);
            return I18n.Format("{0}天后", "{0}d left", days);
        }

        if (hours > 0)
        {
            if (minutes > 0)
                return I18n.Format("{0}小时{1}分后", "{0}h {1}m left", hours, minutes);
            return I18n.Format("{0}小时后", "{0}h left", hours);
        }

        if (minutes > 0)
        {
            return I18n.Format("{0}分后", "{0}m left", minutes);
        }

        return I18n.T("不到1分钟后", "< 1m left");
    }

    /// <summary>
    /// 格式化重置时间，输出形如“08-27 02:59，4小时50分后”的字符串。
    /// </summary>
    public static string FormatResetWithRelative(DateTimeOffset? resetAt, string dateFormat = "MM-dd HH:mm", DateTimeOffset? now = null)
    {
        if (!resetAt.HasValue) return I18n.T("未知", "Unknown");
        var local = resetAt.Value.ToLocalTime();
        var dateStr = local.ToString(dateFormat);
        var relStr = FormatRelativeFuture(resetAt.Value, now);
        var sep = I18n.T("，", ", ");
        return string.IsNullOrEmpty(relStr) ? dateStr : $"{dateStr}{sep}{relStr}";
    }
}
