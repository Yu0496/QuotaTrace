namespace UsageTray.Core;

public sealed record DateRange
{
    public DateOnly From { get; }
    public DateOnly To { get; }

    public DateRange(DateOnly from, DateOnly to)
    {
        if (to < from) throw new ArgumentException("日期范围的结束日期不能早于开始日期。", nameof(to));
        From = from;
        To = to;
    }

    public DateTime StartLocal => From.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local);
    public DateTime EndExclusiveLocal => To.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local);
    public bool Contains(DateOnly date) => date >= From && date <= To;

    public static DateRange Today(TimeProvider? clock = null)
    {
        var now = (clock ?? TimeProvider.System).GetLocalNow().Date;
        var today = DateOnly.FromDateTime(now);
        return new DateRange(today, today);
    }

    public static DateRange LastDays(int days, TimeProvider? clock = null)
    {
        if (days <= 0) throw new ArgumentOutOfRangeException(nameof(days));
        var today = DateOnly.FromDateTime((clock ?? TimeProvider.System).GetLocalNow().Date);
        return new DateRange(today.AddDays(-(days - 1)), today);
    }

    public static DateRange ThisMonth(TimeProvider? clock = null)
    {
        var today = DateOnly.FromDateTime((clock ?? TimeProvider.System).GetLocalNow().Date);
        return new DateRange(new DateOnly(today.Year, today.Month, 1), today);
    }
}
