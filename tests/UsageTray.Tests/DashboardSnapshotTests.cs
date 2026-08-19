using UsageTray.Services;

namespace UsageTray.Tests;

public sealed class DashboardSnapshotTests
{
    [Fact]
    public void CalculatesNonCachedInputAsInputMinusCached()
    {
        var snapshot = new DashboardSnapshot { InputTokens = 1000, CachedTokens = 400 };

        Assert.Equal(600, snapshot.NonCachedInputTokens);
    }

    [Fact]
    public void ClampsNonCachedInputWhenSourceCountersAreInconsistent()
    {
        var snapshot = new DashboardSnapshot { InputTokens = 300, CachedTokens = 400 };

        Assert.Equal(0, snapshot.NonCachedInputTokens);
    }
}
