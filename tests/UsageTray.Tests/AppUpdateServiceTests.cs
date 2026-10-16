using UsageTray.Services;
using Xunit;

namespace UsageTray.Tests;

public sealed class AppUpdateServiceTests
{
    [Fact]
    public void GetCurrentVersionReturnsValidVersion()
    {
        var ver = AppUpdateService.GetCurrentVersion();
        Assert.NotNull(ver);
        Assert.True(ver.Major >= 1);
        Assert.Equal("v" + ver.ToString(3), AppUpdateService.GetCurrentVersionString());
    }

    [Fact]
    public void ParseReleaseJsonExtractsValidReleaseInfo()
    {
        var json = @"{
            ""tag_name"": ""v1.5.0"",
            ""name"": ""QuotaTrace v1.5.0"",
            ""body"": ""- 新增功能 A\r\n- 修复问题 B"",
            ""html_url"": ""https://github.com/Yu0496/QuotaTrace/releases/tag/v1.5.0"",
            ""published_at"": ""2026-10-18T10:00:00Z"",
            ""assets"": [
                {
                    ""name"": ""source.tar.gz"",
                    ""browser_download_url"": ""https://github.com/Yu0496/QuotaTrace/releases/download/v1.5.0/source.tar.gz"",
                    ""size"": 1024
                },
                {
                    ""name"": ""QuotaTrace-v1.5.0-win-x64.zip"",
                    ""browser_download_url"": ""https://github.com/Yu0496/QuotaTrace/releases/download/v1.5.0/QuotaTrace-v1.5.0-win-x64.zip"",
                    ""size"": 35000000
                }
            ]
        }";

        var release = AppUpdateService.ParseReleaseJson(json);
        Assert.NotNull(release);
        Assert.Equal("v1.5.0", release.TagName);
        Assert.Equal("1.5.0", release.VersionString);
        Assert.Equal(new Version(1, 5, 0), release.Version);
        Assert.Equal("QuotaTrace v1.5.0", release.Title);
        Assert.Contains("新增功能 A", release.Description);
        Assert.Equal("https://github.com/Yu0496/QuotaTrace/releases/tag/v1.5.0", release.HtmlUrl);

        Assert.NotNull(release.Asset);
        Assert.Equal("QuotaTrace-v1.5.0-win-x64.zip", release.Asset.Name);
        Assert.Equal("https://github.com/Yu0496/QuotaTrace/releases/download/v1.5.0/QuotaTrace-v1.5.0-win-x64.zip", release.Asset.DownloadUrl);
        Assert.Equal(35000000L, release.Asset.Size);
    }

    [Fact]
    public void ParseReleaseJsonHandlesPrereleaseTags()
    {
        var json = @"{
            ""tag_name"": ""v1.6.0-rc1"",
            ""name"": ""QuotaTrace v1.6.0 RC1"",
            ""body"": ""RC 测试版"",
            ""html_url"": ""https://github.com/Yu0496/QuotaTrace/releases/tag/v1.6.0-rc1"",
            ""published_at"": ""2026-10-20T12:00:00Z"",
            ""assets"": []
        }";

        var release = AppUpdateService.ParseReleaseJson(json);
        Assert.NotNull(release);
        Assert.Equal(new Version(1, 6, 0), release.Version);
        Assert.Null(release.Asset);
    }

    [Fact]
    public void ParseReleaseJsonHandlesCorruptOrMissingData()
    {
        Assert.Null(AppUpdateService.ParseReleaseJson(string.Empty));
        Assert.Null(AppUpdateService.ParseReleaseJson("{ invalid json"));
        Assert.Null(AppUpdateService.ParseReleaseJson("{}"));
        Assert.Null(AppUpdateService.ParseReleaseJson(@"{""tag_name"": ""invalid_version""}"));
    }

    [Fact]
    public void VersionComparisonCorrectlyIdentifiesNewVersion()
    {
        var current = AppUpdateService.GetCurrentVersion();
        var higher = new Version(current.Major, current.Minor + 1, 0);
        var lower = new Version(current.Major, Math.Max(0, current.Minor - 1), 0);

        Assert.True(higher > current);
        Assert.False(lower > current);
        Assert.False(new Version(current.Major, current.Minor, 0) > current);
    }
}
