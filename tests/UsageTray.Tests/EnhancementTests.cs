using System.Text.Json;
using UsageTray.Core;
using UsageTray.Pricing;
using UsageTray.Providers.Antigravity;
using UsageTray.Services;

namespace UsageTray.Tests;

public sealed class EnhancementTests
{
    [Fact]
    public void CompleteJsonArrayIsParsedAsMultipleHistoryEvents()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("conversation.json");
        File.WriteAllText(path, """
        [
          { "timestamp": "2026-08-19T01:00:00Z", "conversation_id": "c1", "model": "gemini-3.5-flash-lite", "usage": { "input_tokens": 100, "output_tokens": 10 } },
          { "timestamp": "2026-08-19T01:05:00Z", "conversation_id": "c1", "model": "gemini-3.5-flash-lite", "usage": { "input_tokens": 50, "output_tokens": 5 } }
        ]
        """);

        var result = new AntigravityHistoryParser().ParseFile(path);

        var bucket = Assert.Single(result.Buckets);
        Assert.Equal(150, bucket.InputTokens);
        Assert.Equal(15, bucket.OutputTokens);
        Assert.Equal(2, bucket.RequestCount);
    }

    [Fact]
    public void AggregateQualityCannotBeDowngradedByLaterPricedBucket()
    {
        using var workspace = new TempWorkspace();
        var service = new PricingService(workspace.File("pricing.json"), new PricingDocument(1, DateOnly.FromDateTime(DateTime.Today),
        [
            new PricingRule("Codex", "known", MatchMode.Exact, 1m, null, null, 1m, "https://example.invalid/pricing", DateOnly.FromDateTime(DateTime.Today))
        ]));
        var buckets = new UsageBucket[]
        {
            new(ProviderKind.Codex, DateOnly.FromDateTime(DateTime.Today), null, "unknown", 100, 0, 0, 1, DataQuality.Exact, "fixture-unknown"),
            new(ProviderKind.Codex, DateOnly.FromDateTime(DateTime.Today), null, "known", 100, 0, 0, 1, DataQuality.Exact, "fixture-known")
        };

        var result = service.CalculateAggregate(buckets);

        Assert.Equal(CostQuality.Unavailable, result.Quality);
        Assert.Equal(100, result.UnpricedTokens);
        Assert.Equal(1, result.UnpricedBucketCount);
        Assert.Equal(0.0001m, result.PricedCostUsd);
    }

    [Fact]
    public void BundledPricingContainsCurrentModelFamilies()
    {
        using var workspace = new TempWorkspace();
        var bundledPath = Path.Combine(AppContext.BaseDirectory, "Pricing", "default-pricing.json");
        var service = PricingService.LoadOrCreate(workspace.File("pricing.json"), bundledPath);

        var gpt = service.Calculate(new UsageBucket(ProviderKind.Codex, DateOnly.FromDateTime(DateTime.Today), null, "gpt-5.6-luna", 1_000_000, 0, 0, 1, DataQuality.Exact, "fixture"));
        var gemini = service.Calculate(new UsageBucket(ProviderKind.Antigravity, DateOnly.FromDateTime(DateTime.Today), null, "gemini-3.5-flash-lite", 1_000_000, 0, 0, 1, DataQuality.Exact, "fixture"));

        Assert.Equal(0.2m, gpt.CostUsd);
        Assert.Equal(0.3m, gemini.CostUsd);
    }

    [Fact]
    public void QuotaSummaryControlInstantiatesAndRendersSnapshotWithoutExceptions()
    {
        using var control = new UsageTray.UI.Controls.QuotaSummaryControl();
        control.Size = new System.Drawing.Size(500, 400);

        var now = DateTimeOffset.UtcNow;
        var snapshot = new DashboardSnapshot
        {
            Range = DateRange.LastDays(7),
            Quotas =
            [
                new QuotaView(new QuotaSnapshot(ProviderKind.Antigravity, now, "gemini-5h", "Gemini (5h)", 0.8, now.AddHours(4), "5h", "local", "Pro"), false),
                new QuotaView(new QuotaSnapshot(ProviderKind.Codex, now, "codex-weekly", "Codex weekly", 0.95, now.AddDays(7), "weekly", "rate_limits", "Plus"), false)
            ]
        };

        control.SetSnapshot(snapshot);
        control.Size = new System.Drawing.Size(600, 500);

        var height = control.MeasureHeight(600);
        Assert.True(height > 0);
    }

    [Fact]
    public void AppIconCanBeLoadedFromEmbeddedResource()
    {
        using var icon = UsageTray.UI.AppIcon.Create();
        Assert.NotNull(icon);
        Assert.True(icon.Width > 0);
        Assert.True(icon.Height > 0);

        using var icon16 = UsageTray.UI.AppIcon.Create(16, 16);
        Assert.NotNull(icon16);
        Assert.Equal(16, icon16.Width);
        Assert.Equal(16, icon16.Height);
    }
}

