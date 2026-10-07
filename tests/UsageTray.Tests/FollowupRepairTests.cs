using System.Reflection;
using UsageTray.Core;
using UsageTray.Pricing;
using UsageTray.Providers.Codex;
using UsageTray.Services;
using UsageTray.UI;

namespace UsageTray.Tests;

public sealed class FollowupRepairTests
{
    [Theory]
    [InlineData("gpt-reserve")]
    public void AgreedLunaMappingIncludesCacheLongContextAndFast(string model)
    {
        var pricing = new PricingService("dummy", PricingService.BuiltInDefaults());
        var bucket = new UsageBucket(ProviderKind.Codex, DateOnly.FromDateTime(DateTime.Today), null, model,
            400_000, 100_000, 30_000, 1, DataQuality.Exact, "fixture", CacheWriteInputTokens: 50_000,
            LongContextInputTokens: 300_000, LongContextCachedInputTokens: 80_000,
            LongContextCacheWriteInputTokens: 40_000, LongContextOutputTokens: 20_000, LongContextRequestCount: 1);
        foreach (var tier in new string?[] { null, "fast" })
        {
            var result = pricing.Calculate(bucket with { ServiceTier = tier });
            Assert.NotNull(result.CostUsd);
            Assert.Equal(pricing.Calculate(bucket with { ModelId = "gpt-5.6-luna", ServiceTier = tier }).CostUsd, result.CostUsd);
        }
    }

    [Fact]
    public void CodexAutoReviewIsFreeAcrossTiersAndLongContext()
    {
        var pricing = new PricingService("dummy", PricingService.BuiltInDefaults());
        var bucket = new UsageBucket(ProviderKind.Codex, DateOnly.FromDateTime(DateTime.Today), null, "codex-auto-review",
            400_000, 100_000, 30_000, 1, DataQuality.Exact, "fixture", CacheWriteInputTokens: 50_000,
            LongContextInputTokens: 300_000, LongContextCachedInputTokens: 80_000,
            LongContextCacheWriteInputTokens: 40_000, LongContextOutputTokens: 20_000, LongContextRequestCount: 1);
        foreach (var tier in new string?[] { null, "fast" })
        {
            var result = pricing.Calculate(bucket with { ServiceTier = tier });
            Assert.NotNull(result.CostUsd);
            Assert.Equal(0.0m, result.CostUsd.Value);
        }
    }

    [Fact]
    public void Version4UnpricedReserveRuleMigratesBackToLuna()
    {
        var rule = PricingService.BuiltInDefaults().Rules.Single(r => r.ModelPattern == "gpt-reserve*") with
        {
            UnverifiedReason = "Reserve 底层模型及价格未经官方确认", ReferenceBasis = null,
            ServiceTierPrices = null, ServiceTierLongContextPrices = null
        };
        var service = new PricingService("dummy", new(4, new DateOnly(2026, 9, 8), [rule]));
        var migrated = service.Rules.Single(r => r.ModelPattern == "gpt-reserve*");
        Assert.Null(migrated.UnverifiedReason);
        Assert.Contains("Luna", migrated.ReferenceBasis);
        Assert.Equal(PricingService.DefaultDocumentVersion, service.Document.SchemaVersion);
    }

    [Fact]
    public void Version7AutoReviewRuleMigratesToFree()
    {
        var legacyV7 = new PricingRule("Codex", "codex-auto-review*", MatchMode.Wildcard,
            0.2m, 0.02m, 0.25m, 1.2m, "https://developers.openai.com/api/docs/models/gpt-5.6-luna",
            new DateOnly(2026, 9, 9), true,
            new(0.4m, 0.04m, 0.5m, 1.8m), 272000,
            new Dictionary<string, TokenPriceSet> { ["fast"] = new(0.5m, 0.05m, 0.625m, 3.0m) },
            new Dictionary<string, TokenPriceSet> { ["fast"] = new(1.0m, 0.1m, 1.25m, 4.5m) },
            null, "用户约定按 GPT-5.6 Luna 参考价计量；包含缓存、长上下文及 Fast 档位");

        var service = new PricingService("dummy", new(7, new DateOnly(2026, 10, 4), [legacyV7]));
        var migrated = service.Rules.Single(r => r.ModelPattern == "codex-auto-review*");
        Assert.Null(migrated.UnverifiedReason);
        Assert.Equal(0.0m, migrated.InputPerMillionUsd);
        Assert.Equal(0.0m, migrated.OutputPerMillionUsd);
        Assert.Contains("免费", migrated.ReferenceBasis);
        Assert.Equal(PricingService.DefaultDocumentVersion, service.Document.SchemaVersion);
    }

    [Fact]
    public void OtherCycleObservationCannotEraseMatchingCycleBaseline()
    {
        var now = DateTimeOffset.UtcNow;
        var latest = new QuotaSnapshot(ProviderKind.Codex, now, "codex-weekly", "Codex", .89,
            now.AddDays(6), "weekly", "fixture", "ProLite");
        var baseline = latest with { CapturedAt = now.AddHours(-3), RemainingFraction = 1.0 };
        var unrelated = latest with { CapturedAt = now.AddHours(-2), ResetAt = now.AddDays(6).AddHours(1), RemainingFraction = .77 };
        var recent = latest with { CapturedAt = now.AddMinutes(-5), RemainingFraction = .89 };
        var result = QuotaProjector.Estimate(latest, [baseline, unrelated, recent, latest], (start, end) =>
        {
            Assert.Equal(baseline.CapturedAt.AddTicks(1), start);
            return 2.2m;
        });
        Assert.Equal(20m, result.FullValue);
    }

    [Fact]
    public void CodexWeekIncludesAutoReviewAndDisplaysCostAndFullEstimate()
    {
        using var workspace = new TempWorkspace();
        var (database, repo) = RepositoryFactory.Create(workspace);
        using (database)
        {
            var now = DateTimeOffset.UtcNow;
            var latest = new QuotaSnapshot(ProviderKind.Codex, now, "codex-weekly", "Codex", .9,
                now.AddDays(6), "weekly", "fixture", "Pro");
            repo.AddQuotaSnapshots([latest with { CapturedAt = now.AddHours(-2), RemainingFraction = 1.0 }, latest]);
            var path = workspace.File("review.jsonl"); File.WriteAllText(path, "fixture");
            CodexTokenSnapshot record = new("review", now.AddHours(-1), "codex-auto-review", null, "standard",
                new(100_000, 0, 10_000, 0), new(100_000, 0, 10_000, 0), null, path, 1);
            repo.ReplaceCodexSource(new FileInfo(path), [record], "review", null, "codex-auto-review", null);
            var dashboard = new UsageAggregator(repo, new("dummy", PricingService.BuiltInDefaults()))
                .BuildSnapshot(DateRange.LastDays(7), ProviderKind.Codex, true);
            // 审核模型现已免费，单独使用时不产生金额；缺少付费本机用量时周满额为 null
            Assert.Equal(0.0m, dashboard.CodexStandardApiEquivalentUsd);
            Assert.Equal(0.0m, dashboard.CodexWeeklyCycle!.CycleCostUsd);
            Assert.Null(dashboard.CodexWeeklyCycle.EstimatedWeeklyCostUsd);
            Assert.Contains("$0.00", QuotaDisplayFormatter.BuildPopupText(dashboard));

            // 当加入主任务付费模型（如 gpt-5.6-sol）时，仅主任务模型计费，Auto-Review 不计价也不增加总额
            var solPath = workspace.File("sol.jsonl"); File.WriteAllText(solPath, "fixture");
            CodexTokenSnapshot solRecord = new("sol", now.AddHours(-1), "gpt-5.6-sol", null, "standard",
                new(1_000_000, 0, 100_000, 0), new(1_000_000, 0, 100_000, 0), null, solPath, 1);
            repo.ReplaceCodexSource(new FileInfo(solPath), [solRecord], "sol", null, "gpt-5.6-sol", null);
            var mixedDashboard = new UsageAggregator(repo, new("dummy", PricingService.BuiltInDefaults()))
                .BuildSnapshot(DateRange.LastDays(7), ProviderKind.Codex, true);

            // gpt-5.6-sol: 1M uncached input @ $10 (长上下文) + 100K output @ $45 (长上下文) = $10 + $4.5 = $14.50; Auto-Review = $0.00
            Assert.Equal(14.50m, mixedDashboard.CodexStandardApiEquivalentUsd);
            Assert.Equal(14.50m, mixedDashboard.CodexWeeklyCycle!.CycleCostUsd);
            // 满额 = $14.50 / (1.0 - 0.9) = $145.00
            Assert.Equal(145.00m, mixedDashboard.CodexWeeklyCycle.EstimatedWeeklyCostUsd);
            Assert.Contains("$14.50", QuotaDisplayFormatter.BuildPopupText(mixedDashboard));
            Assert.Contains("$145.00", QuotaDisplayFormatter.BuildPopupText(mixedDashboard));
        }
    }

    [Fact]
    public void PopupClickClosesAfterDraggingAndDragReleaseDoesNotClose()
    {
        RunSta(() =>
        {
            using var popup = new QuotaPopupForm();
            var content = popup.Controls[0];
            var closeCount = 0;
            popup.CloseRequested += (_, _) => closeCount++;
            void Mouse(string name, System.Windows.Forms.MouseButtons button, int x, int y) =>
                typeof(System.Windows.Forms.Control).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(content, [new System.Windows.Forms.MouseEventArgs(button, 1, x, y, 0)]);
            Mouse("OnMouseDown", System.Windows.Forms.MouseButtons.Left, 30, 30);
            Mouse("OnMouseUp", System.Windows.Forms.MouseButtons.Left, 30, 30);
            Assert.Equal(1, closeCount);
            Mouse("OnMouseDown", System.Windows.Forms.MouseButtons.Left, 30, 30);
            Mouse("OnMouseMove", System.Windows.Forms.MouseButtons.Left, 60, 60);
            Mouse("OnMouseUp", System.Windows.Forms.MouseButtons.Left, 60, 60);
            Assert.Equal(1, closeCount);
            Mouse("OnMouseDown", System.Windows.Forms.MouseButtons.Left, 30, 30);
            Mouse("OnMouseUp", System.Windows.Forms.MouseButtons.Left, 30, 30);
            Assert.Equal(2, closeCount);
            Mouse("OnMouseDown", System.Windows.Forms.MouseButtons.Right, 30, 30);
            Mouse("OnMouseUp", System.Windows.Forms.MouseButtons.Right, 30, 30);
            Assert.Equal(2, closeCount);
        });
    }

    [Fact]
    public void TallPopupFitsWorkingAreaInsteadOfLosingCodexSectionOffScreen()
    {
        var area = new System.Drawing.Rectangle(-1920, 0, 1920, 1040);
        var result = QuotaPopupForm.FitToWorkingArea(new(-1, 1040), new(750, 1600), area);
        Assert.True(area.Contains(result));
        Assert.Equal(1032, result.Height);
        Assert.Equal(750, result.Width);
    }

    [Fact]
    public void SparkRateLimitsAreIgnoredAndDoNotCreateQuotas()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("spark.jsonl");
        File.WriteAllText(path, """
            {"timestamp":"2026-09-08T09:55:03.468Z","type":"turn_context","payload":{"model":"gpt-5.3-codex-spark"}}
            {"timestamp":"2026-09-08T09:55:07.716Z","type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{"limit_id":"codex","limit_name":null,"plan_type":"prolite","primary":{"used_percent":52,"window_minutes":300,"resets_at":1788874852},"secondary":{"used_percent":23,"window_minutes":10080,"resets_at":1789461652}}}}
            """);
        var quotas = new CodexJsonlParser().ParseFile(path).Quotas;
        Assert.Empty(quotas);
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
