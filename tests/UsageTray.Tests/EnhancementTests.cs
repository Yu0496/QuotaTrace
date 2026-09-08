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
        Assert.Null(result.PricedCostUsd);
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
    public void QuotaSummaryControl_ShowsReported5HourWindow_WhenPlanTierIsProOrAbove()
    {
        using var control = new UsageTray.UI.Controls.QuotaSummaryControl();
        control.Size = new System.Drawing.Size(600, 500);

        var now = DateTimeOffset.UtcNow;
        var plusSnapshot = new DashboardSnapshot
        {
            Range = DateRange.LastDays(7),
            Quotas =
            [
                new QuotaView(new QuotaSnapshot(ProviderKind.Codex, now, "codex-primary", "5-hour limit", 0.5, now.AddHours(2), "5h", "rate_limits", "Plus"), false),
                new QuotaView(new QuotaSnapshot(ProviderKind.Codex, now, "codex-weekly", "Weekly limit", 0.8, now.AddDays(5), "weekly", "rate_limits", "Plus"), false)
            ]
        };
        control.SetSnapshot(plusSnapshot);
        var heightPlus = control.MeasureHeight(600);

        var proSnapshot = new DashboardSnapshot
        {
            Range = DateRange.LastDays(7),
            Quotas =
            [
                new QuotaView(new QuotaSnapshot(ProviderKind.Codex, now, "codex-primary", "5-hour limit", 0.5, now.AddHours(2), "5h", "rate_limits", "ProLite"), false),
                new QuotaView(new QuotaSnapshot(ProviderKind.Codex, now, "codex-weekly", "Weekly limit", 0.8, now.AddDays(5), "weekly", "rate_limits", "ProLite"), false)
            ]
        };
        control.SetSnapshot(proSnapshot);
        var heightPro = control.MeasureHeight(600);

        // The same reported windows occupy the same height for either plan.
        Assert.Equal(heightPlus, heightPro);
    }

    [Fact]
    public void QuotaDisplayFormatter_ShowsReported5HourWindow_WhenPlanIsProLite()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new DashboardSnapshot
        {
            Range = DateRange.Today(),
            Quotas =
            [
                new QuotaView(new QuotaSnapshot(ProviderKind.Codex, now, "codex-primary", "5-hour limit", 0.95, now.AddHours(2), "5h", "rate_limits", "ProLite"), false),
                new QuotaView(new QuotaSnapshot(ProviderKind.Codex, now, "codex-weekly", "Weekly limit", 0.98, now.AddDays(7), "weekly", "rate_limits", "ProLite"), false)
            ]
        };

        var popupText = UsageTray.UI.QuotaDisplayFormatter.BuildPopupText(snapshot);
        Assert.Contains("5 小时窗口", popupText);
        Assert.Contains("周窗口", popupText);
        Assert.Contains("98%", popupText);
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

    [Fact]
    public void MemoryOptimizerTrimMemoryExecutesWithoutExceptions()
    {
        // 验证 MemoryOptimizer 执行垃圾回收与修剪时安全无异常
        MemoryOptimizer.TrimMemory();
    }

    [Fact]
    public void ProtobufSpanReaderCorrectlyDecodesFields()
    {
        // 编码简单的 protobuf 结构：field 1 (varint = 150), field 2 (len-delimited = "hello")
        // field 1: tag = (1 << 3) | 0 = 8, varint 150 = 0x96, 0x01
        // field 2: tag = (2 << 3) | 2 = 18, len = 5, bytes = "hello"
        byte[] data = [0x08, 0x96, 0x01, 0x12, 0x05, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o'];

        var reader = new ProtobufSpanReader(data);

        Assert.True(reader.ReadNext());
        Assert.Equal(1, reader.FieldNumber);
        Assert.Equal(0, reader.WireType);
        Assert.Equal(150UL, reader.Varint);

        Assert.True(reader.ReadNext());
        Assert.Equal(2, reader.FieldNumber);
        Assert.Equal(2, reader.WireType);
        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(reader.Bytes));

        Assert.False(reader.ReadNext());
    }

    [Fact]
    public void CompactTooltip_SingleSourceOfTruth_DecouplesStandardAndSparkWithoutMisleading77Percent()
    {
        var now = DateTimeOffset.UtcNow;
        var resetAt = now.AddDays(6);

        var snapshot = new DashboardSnapshot
        {
            Quotas =
            [
                new QuotaView(new QuotaSnapshot(ProviderKind.Codex, now, "codex-weekly", "Codex 主力模型", 0.98, resetAt, "weekly", "rate_limits", "ProLite"), false),
                new QuotaView(new QuotaSnapshot(ProviderKind.Codex, now.AddSeconds(10), "codex-spark-5h", "GPT-5.3 Spark", 0.48, now.AddHours(4), "5h", "rate_limits", "ProLite"), false),
                new QuotaView(new QuotaSnapshot(ProviderKind.Codex, now.AddSeconds(10), "codex-spark-weekly", "GPT-5.3 Spark", 0.77, resetAt.AddHours(1), "weekly", "rate_limits", "ProLite"), false)
            ]
        };

        var compact = UsageTray.UI.QuotaDisplayFormatter.BuildCompactText(snapshot);
        // 核心验证：单行紧凑显示必须准确反映主力 98% 与 Spark 77%，绝不能被误导显示成 "Codex 77%"
        Assert.Contains("Codex 98% / Spark 77%", compact);
        Assert.DoesNotContain("Codex 77%", compact);
    }
}
