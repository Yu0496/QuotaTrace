using UsageTray.App;
using UsageTray.Core;
using UsageTray.Services;
using UsageTray.UI;
using Xunit;

namespace UsageTray.Tests;

public sealed class I18nAndProviderToggleTests
{
    [Fact]
    public void I18n_SetLanguage_SwitchesLanguageAndFiresEvent()
    {
        var fired = false;
        void Handler() => fired = true;

        I18n.LanguageChanged += Handler;
        try
        {
            I18n.SetLanguage("en-US");
            Assert.True(I18n.IsEnglish);
            Assert.Equal("Hello", I18n.T("你好", "Hello"));
            Assert.Equal("Value: 42", I18n.Format("数值：{0}", "Value: {0}", 42));
            Assert.True(fired);

            fired = false;
            I18n.SetLanguage("zh-CN");
            Assert.False(I18n.IsEnglish);
            Assert.Equal("你好", I18n.T("你好", "Hello"));
            Assert.Equal("数值：42", I18n.Format("数值：{0}", "Value: {0}", 42));
            Assert.True(fired);
        }
        finally
        {
            I18n.LanguageChanged -= Handler;
            I18n.SetLanguage("zh-CN");
        }
    }

    [Fact]
    public void AppSettings_Normalize_EnsuresAtLeastOneProviderEnabled()
    {
        var settings = new AppSettings
        {
            EnableCodex = false,
            EnableAntigravity = false,
            Language = "EN"
        };

        settings.Normalize();

        Assert.True(settings.EnableCodex);
        Assert.True(settings.EnableAntigravity);
        Assert.Equal("en-US", settings.Language);
    }

    [Fact]
    public void AppSettings_Normalize_AllowsSingleProvider()
    {
        var codexOnly = new AppSettings
        {
            EnableCodex = true,
            EnableAntigravity = false
        };
        codexOnly.Normalize();
        Assert.True(codexOnly.EnableCodex);
        Assert.False(codexOnly.EnableAntigravity);

        var antiOnly = new AppSettings
        {
            EnableCodex = false,
            EnableAntigravity = true
        };
        antiOnly.Normalize();
        Assert.False(antiOnly.EnableCodex);
        Assert.True(antiOnly.EnableAntigravity);
    }

    [Fact]
    public void QuotaDisplayFormatter_RespectsProviderToggles()
    {
        var snapshot = new DashboardSnapshot
        {
            Range = DateRange.Today(),
            InputTokens = 1000,
            CachedTokens = 200,
            CacheCreationTokens = 10,
            OutputTokens = 300,
            CodexApiEquivalentUsd = 1.50m,
            CodexStandardApiEquivalentUsd = 1.50m,
            CodexSparkApiEquivalentUsd = 0m,
            CodexReserveApiEquivalentUsd = 0m,
            AntigravityApiEquivalentUsd = 2.50m
        };

        // Both enabled
        var both = QuotaDisplayFormatter.BuildCompactText(snapshot, enableCodex: true, enableAntigravity: true);
        Assert.Contains("AG", both);
        Assert.Contains("Codex", both);

        // Only Codex
        var codexOnly = QuotaDisplayFormatter.BuildCompactText(snapshot, enableCodex: true, enableAntigravity: false);
        Assert.StartsWith("Codex", codexOnly);
        Assert.DoesNotContain("AG", codexOnly);

        // Only Antigravity
        var agOnly = QuotaDisplayFormatter.BuildCompactText(snapshot, enableCodex: false, enableAntigravity: true);
        Assert.StartsWith("AG", agOnly);
        Assert.DoesNotContain("Codex", agOnly);
    }

    [Fact]
    public void TimeFormatter_LocalizesResetAndRelativeStrings()
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var targetFuture = now.AddDays(2).AddHours(3).AddMinutes(15);

            I18n.SetLanguage("zh-CN");
            var zhStr = TimeFormatter.FormatRelativeFuture(targetFuture, now);
            Assert.Contains("天", zhStr);
            Assert.Contains("小时", zhStr);
            Assert.Contains("分后", zhStr);

            I18n.SetLanguage("en-US");
            var enStr = TimeFormatter.FormatRelativeFuture(targetFuture, now);
            Assert.Contains("d", enStr);
            Assert.Contains("h", enStr);
            Assert.Contains("m left", enStr);
        }
        finally
        {
            I18n.SetLanguage("zh-CN");
        }
    }
}
