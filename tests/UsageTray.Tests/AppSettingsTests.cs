using UsageTray.App;

namespace UsageTray.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void WindowSizeSettingsAreClampedToSafeMinimumAndMaximum()
    {
        var settings = new AppSettings { MainWindowWidth = 100, MainWindowHeight = 5000 };

        settings.Normalize();

        Assert.Equal(740, settings.MainWindowWidth);
        Assert.Equal(3000, settings.MainWindowHeight);
    }
}
