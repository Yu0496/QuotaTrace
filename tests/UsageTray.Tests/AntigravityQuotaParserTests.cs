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

    [Fact]
    public void QuotaParserCorrectlyExtractsRetrieveUserQuotaSummaryGroupsAndBuckets()
    {
        using var document = JsonDocument.Parse("""
        {
          "response": {
            "groups": [
              {
                "displayName": "Gemini Models",
                "buckets": [
                  {
                    "bucketId": "gemini-weekly",
                    "displayName": "Weekly Limit Remaining",
                    "window": "weekly",
                    "remainingFraction": 0.864,
                    "resetTime": "2026-08-25T18:34:46Z"
                  },
                  {
                    "bucketId": "gemini-5h",
                    "displayName": "Five Hour Limit Remaining",
                    "window": "5h",
                    "remainingFraction": 0.911,
                    "resetTime": "2026-08-20T10:38:07Z"
                  }
                ]
              },
              {
                "displayName": "Claude and GPT models",
                "buckets": [
                  {
                    "bucketId": "3p-weekly",
                    "displayName": "Weekly Limit Remaining",
                    "window": "weekly",
                    "remainingFraction": 0.999,
                    "resetTime": "2026-08-24T08:40:36Z"
                  }
                ]
              }
            ]
          }
        }
        """);

        var result = new AntigravityQuotaParser().Parse(document.RootElement, DateTimeOffset.UtcNow, "fixture");

        Assert.Equal(3, result.Snapshots.Count);
        var geminiWeekly = Assert.Single(result.Snapshots, s => s.ModelOrPoolId == "gemini-weekly");
        Assert.Equal(0.864, geminiWeekly.RemainingFraction);
        Assert.Equal("Gemini (周额度)", geminiWeekly.DisplayLabel);
        Assert.Equal("weekly", geminiWeekly.WindowKind);

        var claudeWeekly = Assert.Single(result.Snapshots, s => s.ModelOrPoolId == "3p-weekly");
        Assert.Equal(0.999, claudeWeekly.RemainingFraction);
        Assert.Equal("Claude / GPT (周额度)", claudeWeekly.DisplayLabel);
    }
}

