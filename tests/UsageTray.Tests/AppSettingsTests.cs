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

    [Fact]
    public void LastFullScanUtcIsPreservedAcrossSerialization()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"settings_test_{Guid.NewGuid():N}.json");
        try
        {
            var store = new AppSettingsStore(tempFile);
            var timestamp = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
            var original = new AppSettings
            {
                LastFullScanUtc = timestamp
            };
            store.Save(original);

            var loaded = store.Load();
            Assert.NotNull(loaded.LastFullScanUtc);
            Assert.Equal(timestamp, loaded.LastFullScanUtc.Value);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(8, true)]
    [InlineData(7, true)]
    [InlineData(6, false)]
    [InlineData(1, false)]
    public void WeeklyFullScanDueCalculation(int? daysAgo, bool expectedDue)
    {
        var now = DateTimeOffset.UtcNow;
        var lastScan = daysAgo.HasValue ? now.AddDays(-daysAgo.Value) : (DateTimeOffset?)null;
        var isDue = !lastScan.HasValue || (now - lastScan.Value).TotalDays >= 7;

        Assert.Equal(expectedDue, isDue);
    }

    [Fact]
    public void ColumnWidthsAreClampedAndPreservedAcrossSerialization()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"settings_col_test_{Guid.NewGuid():N}.json");
        try
        {
            var store = new AppSettingsStore(tempFile);
            var original = new AppSettings
            {
                ModelColumnWidths = new Dictionary<string, int>
                {
                    ["模型"] = 250,
                    ["Provider"] = 10,  // will be clamped to 30
                    ["Input（未命中）"] = 5000 // will be clamped to 2000
                },
                ProjectColumnWidths = new Dictionary<string, int>
                {
                    ["项目"] = 300
                }
            };
            store.Save(original);

            var loaded = store.Load();
            Assert.Equal(250, loaded.ModelColumnWidths["模型"]);
            Assert.Equal(30, loaded.ModelColumnWidths["Provider"]);
            Assert.Equal(2000, loaded.ModelColumnWidths["Input（未命中）"]);
            Assert.Equal(300, loaded.ProjectColumnWidths["项目"]);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void UpdateMethodAtomicallyMutatesAndPreservesOtherFields()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"settings_update_test_{Guid.NewGuid():N}.json");
        try
        {
            var store = new AppSettingsStore(tempFile);
            var initial = new AppSettings
            {
                MainWindowWidth = 1200,
                MainWindowHeight = 800,
                ModelColumnWidths = new Dictionary<string, int> { ["模型"] = 400, ["Provider"] = 200 },
                ProjectColumnWidths = new Dictionary<string, int> { ["项目"] = 300 }
            };
            store.Save(initial);

            // 模拟全量扫描触发只更新 LastFullScanUtc
            var scanTime = DateTimeOffset.UtcNow;
            store.Update(s => s.LastFullScanUtc = scanTime);

            var updated = store.Load();
            Assert.Equal(scanTime, updated.LastFullScanUtc);
            Assert.Equal(1200, updated.MainWindowWidth);
            Assert.Equal(800, updated.MainWindowHeight);
            Assert.Equal(400, updated.ModelColumnWidths["模型"]);
            Assert.Equal(200, updated.ModelColumnWidths["Provider"]);
            Assert.Equal(300, updated.ProjectColumnWidths["项目"]);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
