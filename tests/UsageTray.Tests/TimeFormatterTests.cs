using UsageTray.UI;

namespace UsageTray.Tests;

public sealed class TimeFormatterTests
{
    [Fact]
    public void FormatRelativeFuture_DaysHoursMinutes_ReturnsExpected()
    {
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.FromHours(8));
        var target = now.AddDays(3).AddHours(2).AddMinutes(15);

        var result = TimeFormatter.FormatRelativeFuture(target, now);

        Assert.Equal("3天2小时15分后", result);
    }

    [Fact]
    public void FormatRelativeFuture_DaysHoursOnly_ReturnsExpected()
    {
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.FromHours(8));
        var target = now.AddDays(3).AddHours(2);

        var result = TimeFormatter.FormatRelativeFuture(target, now);

        Assert.Equal("3天2小时后", result);
    }

    [Fact]
    public void FormatRelativeFuture_DaysMinutesOnly_ReturnsExpected()
    {
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.FromHours(8));
        var target = now.AddDays(3).AddMinutes(45);

        var result = TimeFormatter.FormatRelativeFuture(target, now);

        Assert.Equal("3天45分后", result);
    }

    [Fact]
    public void FormatRelativeFuture_DaysOnly_ReturnsExpected()
    {
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.FromHours(8));
        var target = now.AddDays(5);

        var result = TimeFormatter.FormatRelativeFuture(target, now);

        Assert.Equal("5天后", result);
    }

    [Fact]
    public void FormatRelativeFuture_HoursAndMinutes_ReturnsExpected()
    {
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.FromHours(8));
        var target = now.AddHours(4).AddMinutes(54);

        var result = TimeFormatter.FormatRelativeFuture(target, now);

        Assert.Equal("4小时54分后", result);
    }

    [Fact]
    public void FormatRelativeFuture_HoursOnly_ReturnsExpected()
    {
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.FromHours(8));
        var target = now.AddHours(4);

        var result = TimeFormatter.FormatRelativeFuture(target, now);

        Assert.Equal("4小时后", result);
    }

    [Fact]
    public void FormatRelativeFuture_MinutesOnly_ReturnsExpected()
    {
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.FromHours(8));
        var target = now.AddMinutes(29);

        var result = TimeFormatter.FormatRelativeFuture(target, now);

        Assert.Equal("29分后", result);
    }

    [Fact]
    public void FormatRelativeFuture_LessThanOneMinute_ReturnsLessThanMinute()
    {
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.FromHours(8));
        var target = now.AddSeconds(30);

        var result = TimeFormatter.FormatRelativeFuture(target, now);

        Assert.Equal("不到1分钟后", result);
    }

    [Fact]
    public void FormatRelativeFuture_RecentlyPassed_ReturnsSoonOrReset()
    {
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.FromHours(8));
        var targetRecently = now.AddMinutes(-5);
        var targetLongAgo = now.AddHours(-3);

        Assert.Equal("即将重置", TimeFormatter.FormatRelativeFuture(targetRecently, now));
        Assert.Equal("已重置", TimeFormatter.FormatRelativeFuture(targetLongAgo, now));
    }

    [Fact]
    public void FormatResetWithRelative_CombinesAbsoluteAndRelative()
    {
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.FromHours(8));
        var target = new DateTimeOffset(2026, 8, 27, 2, 59, 0, TimeSpan.FromHours(8));

        var result = TimeFormatter.FormatResetWithRelative(target, "MM-dd HH:mm", now);
        var expectedAbsolute = target.ToLocalTime().ToString("MM-dd HH:mm");

        Assert.Equal($"{expectedAbsolute}，14小时59分后", result);
    }

    [Fact]
    public void FormatResetWithRelative_NullReturnsUnknown()
    {
        var result = TimeFormatter.FormatResetWithRelative(null);

        Assert.Equal("未知", result);
    }
}

