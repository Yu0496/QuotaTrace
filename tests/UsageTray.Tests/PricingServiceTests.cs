using UsageTray.Core;
using UsageTray.Pricing;

namespace UsageTray.Tests;

public sealed class PricingServiceTests
{
    [Fact]
    public void CachedInputIsNotDoubleBilled()
    {
        using var workspace = new TempWorkspace();
        var rule = new PricingRule("Codex", "test-model", MatchMode.Exact, 2m, 0.5m, null, 8m,
            "https://example.invalid/pricing", new DateOnly(2026, 8, 19));
        var service = new PricingService(workspace.File("pricing.json"), new PricingDocument(1, new DateOnly(2026, 8, 19), [rule]));
        var bucket = new UsageBucket(ProviderKind.Codex, new DateOnly(2026, 8, 19), null, "test-model",
            1_000_000, 400_000, 1_000_000, 1, DataQuality.Exact, "fixture", null, 0, CostQuality.ExactTokenSplit);

        var result = service.Calculate(bucket);

        Assert.Equal(9.4m, result.CostUsd);
        Assert.Equal(CostQuality.ExactTokenSplit, result.Quality);
    }

    [Fact]
    public void UnknownModelDoesNotUseFallbackPrice()
    {
        using var workspace = new TempWorkspace();
        var service = new PricingService(workspace.File("pricing.json"), new PricingDocument(1, DateOnly.FromDateTime(DateTime.Today), []));
        var bucket = new UsageBucket(ProviderKind.Codex, DateOnly.FromDateTime(DateTime.Today), null, "not-configured",
            100, 0, 20, 1, DataQuality.Exact, "fixture");

        var result = service.Calculate(bucket);

        Assert.Null(result.CostUsd);
        Assert.Equal(CostQuality.Unavailable, result.Quality);
    }

    [Fact]
    public void ClaudeSonnetAndOpusAntigravityModelsCalculateCorrectCost()
    {
        var service = PricingService.BuiltInDefaults();
        var pricing = new PricingService("dummy", service);

        // Claude Sonnet 4.6 (1M uncached input = $3.0, 1M cached = $0.3, 1M output = $15.0 => $18.3)
        var sonnetBucket = new UsageBucket(ProviderKind.Antigravity, new DateOnly(2026, 8, 20), null, "claude-sonnet-4-6",
            2_000_000, 1_000_000, 1_000_000, 1, DataQuality.Exact, "fixture", null, 0, CostQuality.ExactTokenSplit);
        var sonnetResult = pricing.Calculate(sonnetBucket);
        Assert.NotNull(sonnetResult.CostUsd);
        Assert.Equal(18.3m, sonnetResult.CostUsd.Value);

        // Claude Opus 4.6 Thinking (1M uncached = $15.0, 1M cached = $1.5, 1M output = $75.0 => $91.5)
        var opusBucket = new UsageBucket(ProviderKind.Antigravity, new DateOnly(2026, 8, 20), null, "claude-opus-4-6-thinking",
            2_000_000, 1_000_000, 1_000_000, 1, DataQuality.Exact, "fixture", null, 0, CostQuality.ExactTokenSplit);
        var opusResult = pricing.Calculate(opusBucket);
        Assert.NotNull(opusResult.CostUsd);
        Assert.Equal(91.5m, opusResult.CostUsd.Value);
    }

    [Fact]
    public void GeminiAliasesMatchAndCalculateCost()
    {
        var service = PricingService.BuiltInDefaults();
        var pricing = new PricingService("dummy", service);

        // gemini-pro-default -> Gemini 3.1 Pro ($2.0 in, $0.5 cached, $12.0 out)
        var proBucket = new UsageBucket(ProviderKind.Antigravity, new DateOnly(2026, 8, 20), null, "gemini-pro-default",
            1_000_000, 0, 1_000_000, 1, DataQuality.Exact, "fixture", null, 0, CostQuality.ExactTokenSplit);
        var proResult = pricing.Calculate(proBucket);
        Assert.NotNull(proResult.CostUsd);
        Assert.Equal(14.0m, proResult.CostUsd.Value);

        // gemini-3-flash-a -> Gemini 3.5 Flash ($1.5 in, $0.15 cached, $9.0 out)
        var flashBucket = new UsageBucket(ProviderKind.Antigravity, new DateOnly(2026, 8, 20), null, "gemini-3-flash-a",
            1_000_000, 0, 1_000_000, 1, DataQuality.Exact, "fixture", null, 0, CostQuality.ExactTokenSplit);
        var flashResult = pricing.Calculate(flashBucket);
        Assert.NotNull(flashResult.CostUsd);
        Assert.Equal(10.5m, flashResult.CostUsd.Value);

        // gemini-default -> Gemini 3.5 Flash
        var defaultFlashBucket = new UsageBucket(ProviderKind.Antigravity, new DateOnly(2026, 8, 20), null, "gemini-default",
            1_000_000, 0, 1_000_000, 1, DataQuality.Exact, "fixture", null, 0, CostQuality.ExactTokenSplit);
        var defaultFlashResult = pricing.Calculate(defaultFlashBucket);
        Assert.NotNull(defaultFlashResult.CostUsd);
        Assert.Equal(10.5m, defaultFlashResult.CostUsd.Value);
    }

    [Fact]
    public void AutoMergeMissingRulesOnLoadOrCreate()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("pricing.json");

        // Old document only containing 1 rule
        var oldDoc = new PricingDocument(1, new DateOnly(2026, 8, 19), [
            new PricingRule("Codex", "custom-model", MatchMode.Exact, 1m, 0.1m, null, 2m, "url", new DateOnly(2026, 8, 19))
        ]);
        System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(oldDoc));

        // LoadOrCreate with built-in template
        var loaded = PricingService.LoadOrCreate(path);

        // Custom rule is preserved AND built-in rules are merged
        Assert.Contains(loaded.Rules, r => r.ModelPattern == "custom-model");
        Assert.Contains(loaded.Rules, r => r.ModelPattern == "claude-sonnet-4-6*");
        Assert.Contains(loaded.Rules, r => r.ModelPattern == "gemini-3.7-flash*");
        Assert.Contains(loaded.Rules, r => r.ModelPattern == "gemini-pro-default*");
    }

    [Fact]
    public void DisplayNameInferenceResolvesUnknownModels()
    {
        Assert.Equal("claude-sonnet-4-6", UsageTray.Providers.Antigravity.AntigravitySqliteHistoryParser.InferModelFromDisplayName("Claude Sonnet 4.6 (Thinking)"));
        Assert.Equal("claude-opus-4-6-thinking", UsageTray.Providers.Antigravity.AntigravitySqliteHistoryParser.InferModelFromDisplayName("Claude Opus 4.6 (Thinking)"));
        Assert.Equal("gemini-3.5-flash", UsageTray.Providers.Antigravity.AntigravitySqliteHistoryParser.InferModelFromDisplayName("Gemini 3.5 Flash (High)"));
        Assert.Equal("gemini-3.1-pro", UsageTray.Providers.Antigravity.AntigravitySqliteHistoryParser.InferModelFromDisplayName("Gemini 3.1 Pro (High)"));
        Assert.Equal("gpt-oss-120b-medium", UsageTray.Providers.Antigravity.AntigravitySqliteHistoryParser.InferModelFromDisplayName("GPT-OSS 120B (Medium)"));
    }
}

