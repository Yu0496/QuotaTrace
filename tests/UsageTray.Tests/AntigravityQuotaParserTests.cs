using System.Text.Json;
using UsageTray.Providers.Antigravity;

namespace UsageTray.Tests;

public sealed class AntigravityQuotaParserTests
{
    [Fact]
    public void QuotaParserDoesNotTrustArrayOrderAndRejectsInvalidFractions()
    {
        using var document = JsonDocument.Parse("""
        {
          "plan_tier": "Pro",
          "quotaInfo": [
            { "model_id": "claude", "display_name": "Claude", "remainingFraction": 1.4, "windowKind": "5h" },
            { "model_id": "gemini-flash", "display_name": "Gemini Flash", "remainingFraction": 0.64, "windowKind": "5h", "resetTime": "2026-08-19T12:00:00Z" },
            { "model_id": "gemini-pro", "display_name": "Gemini Pro", "remainingFraction": 0.82, "windowKind": "weekly" }
          ]
        }
        """);

        var result = new AntigravityQuotaParser().Parse(document.RootElement, DateTimeOffset.UtcNow, "fixture");

        Assert.Equal(2, result.Snapshots.Count);
        Assert.Contains(result.Snapshots, snapshot => snapshot.ModelOrPoolId == "gemini-flash" && snapshot.RemainingFraction == 0.64);
        Assert.Contains(result.Warnings, warning => warning.Contains("范围外", StringComparison.Ordinal));
    }
}
