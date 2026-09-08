using UsageTray.Core;
using UsageTray.Pricing;
using UsageTray.Providers.Codex;

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

        // Claude Opus 4.6 Thinking (1M uncached = $5, 1M cached = $0.5, 1M output = $25 => $30.5)
        var opusBucket = new UsageBucket(ProviderKind.Antigravity, new DateOnly(2026, 8, 20), null, "claude-opus-4-6-thinking",
            2_000_000, 1_000_000, 1_000_000, 1, DataQuality.Exact, "fixture", null, 0, CostQuality.ExactTokenSplit);
        var opusResult = pricing.Calculate(opusBucket);
        Assert.NotNull(opusResult.CostUsd);
        Assert.Equal(30.5m, opusResult.CostUsd.Value);
    }

    [Theory]
    [InlineData("gemini-pro-default")]
    [InlineData("gemini-3-flash-a")]
    [InlineData("gemini-default")]
    public void UnverifiedAliasesKeepTokensUnpriced(string model)
    {
        var pricing = new PricingService("dummy", PricingService.BuiltInDefaults());
        var result = pricing.Calculate(new UsageBucket(ProviderKind.Antigravity, DateOnly.FromDateTime(DateTime.Today), null,
            model, 1_000_000, 0, 1_000_000, 1, DataQuality.Exact, "fixture"));
        Assert.Null(result.CostUsd);
        Assert.Contains("未定价", result.Warning);
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
        Assert.Contains(loaded.Rules, r => r.ModelPattern == "gpt-6");
        Assert.Contains(loaded.Rules, r => r.ModelPattern == "gpt-6-astra");
        Assert.Contains(loaded.Rules, r => r.ModelPattern == "gemini-3.8-flash");
        Assert.Contains(loaded.Rules, r => r.ModelPattern == "claude-sonnet-4-6*");
        Assert.Contains(loaded.Rules, r => r.ModelPattern == "gemini-3.7-flash");
        Assert.Contains(loaded.Rules, r => r.ModelPattern == "gemini-pro-default*");
    }

    [Fact]
    public void DisplayNameInferenceResolvesUnknownModels()
    {
        Assert.Equal("claude-sonnet-4-6", UsageTray.Providers.Antigravity.AntigravitySqliteHistoryParser.InferModelFromDisplayName("Claude Sonnet 4.6 (Thinking)"));
        Assert.Equal("claude-opus-4-6-thinking", UsageTray.Providers.Antigravity.AntigravitySqliteHistoryParser.InferModelFromDisplayName("Claude Opus 4.6 (Thinking)"));
        Assert.Equal("gemini-3.8-flash", UsageTray.Providers.Antigravity.AntigravitySqliteHistoryParser.InferModelFromDisplayName("Gemini 3.8 Flash (High)"));
        Assert.Equal("gemini-3.5-flash", UsageTray.Providers.Antigravity.AntigravitySqliteHistoryParser.InferModelFromDisplayName("Gemini 3.5 Flash (High)"));
        Assert.Equal("gemini-3.1-pro", UsageTray.Providers.Antigravity.AntigravitySqliteHistoryParser.InferModelFromDisplayName("Gemini 3.1 Pro (High)"));
        Assert.Equal("gpt-6-astra", UsageTray.Providers.Antigravity.AntigravitySqliteHistoryParser.InferModelFromDisplayName("GPT-6 Astra (Preview)"));
        Assert.Equal("gpt-oss-120b-medium", UsageTray.Providers.Antigravity.AntigravitySqliteHistoryParser.InferModelFromDisplayName("GPT-OSS 120B (Medium)"));
    }

    [Fact]
    public void CodexAutoReviewUsesAgreedLunaReference()
    {
        var service = PricingService.BuiltInDefaults();
        var pricing = new PricingService("dummy", service);

        // codex-auto-review (1M uncached input = $0.2, 10M cached = $0.2, 100K output = $0.12 => $0.52)
        var reviewBucket = new UsageBucket(ProviderKind.Codex, new DateOnly(2026, 8, 31), null, "codex-auto-review",
            11_000_000, 10_000_000, 100_000, 1, DataQuality.Exact, "fixture", null, 0, CostQuality.ExactTokenSplit);
        var result = pricing.Calculate(reviewBucket);
        Assert.Equal(0.52m, result.CostUsd);
        Assert.Equal(CostQuality.ExactTokenSplit, result.Quality);
    }

    [Fact]
    public void Gpt6AstraCalculatesCorrectStandardAndLongContextCost()
    {
        var service = PricingService.BuiltInDefaults();
        var pricing = new PricingService("dummy", service);

        // 1. gpt-6-astra standard tier: 2M input (1M uncached @ $10, 1M cached @ $1) + 1M output @ $50 = $61.00
        var astraBucket = new UsageBucket(ProviderKind.Codex, new DateOnly(2026, 9, 5), null, "gpt-6-astra",
            2_000_000, 1_000_000, 1_000_000, 1, DataQuality.Exact, "fixture", null, 0, CostQuality.ExactTokenSplit);
        var astraResult = pricing.Calculate(astraBucket);
        Assert.NotNull(astraResult.CostUsd);
        Assert.Equal(61.0m, astraResult.CostUsd.Value);

        // 2. gpt-6 exact match with cache write: 2M input (1M uncached @ $10, 500k cached @ $1, 500k write @ $12.5) + 1M output @ $50
        // Cost = 1M * 10 + 0.5M * 1 + 0.5M * 12.5 + 1M * 50 = 10 + 0.5 + 6.25 + 50 = $66.75
        var gpt6Bucket = new UsageBucket(ProviderKind.Codex, new DateOnly(2026, 9, 5), null, "gpt-6",
            2_000_000, 500_000, 1_000_000, 1, DataQuality.Exact, "fixture", null, 500_000, CostQuality.ExactTokenSplit);
        var gpt6Result = pricing.Calculate(gpt6Bucket);
        Assert.NotNull(gpt6Result.CostUsd);
        Assert.Equal(66.75m, gpt6Result.CostUsd.Value);

        // 3. gpt-6-astra long context (>272k tokens): 500k input (300k uncached @ $20, 200k cached @ $2) + 100k output @ $75
        // Cost = 0.3M * 20 + 0.2M * 2 + 0.1M * 75 = 6.0 + 0.4 + 7.5 = $13.90
        var longBucket = new UsageBucket(ProviderKind.Codex, new DateOnly(2026, 9, 5), null, "gpt-6-astra",
            500_000, 200_000, 100_000, 1, DataQuality.Exact, "fixture", null, 0, CostQuality.ExactTokenSplit,
            null, 1, 0, 500_000, 200_000, 0, 100_000, true);
        var longResult = pricing.Calculate(longBucket);
        Assert.NotNull(longResult.CostUsd);
        Assert.Equal(13.90m, longResult.CostUsd.Value);

        // 4. gpt-6 fast mode / service tier "fast": 2M input (1M uncached @ $20, 1M cached @ $2) + 1M output @ $100 = $122.00
        var fastBucket = new UsageBucket(ProviderKind.Codex, new DateOnly(2026, 9, 5), null, "gpt-6-fast",
            2_000_000, 1_000_000, 1_000_000, 1, DataQuality.Exact, "fixture", null, 0, CostQuality.ExactTokenSplit);
        var fastResult = pricing.Calculate(fastBucket);
        Assert.NotNull(fastResult.CostUsd);
        Assert.Equal(152.5m, fastResult.CostUsd.Value);

        var serviceTierFastBucket = new UsageBucket(ProviderKind.Codex, new DateOnly(2026, 9, 5), null, "gpt-6-astra",
            2_000_000, 1_000_000, 1_000_000, 1, DataQuality.Exact, "fixture", null, 0, CostQuality.ExactTokenSplit,
            "fast");
        var serviceTierResult = pricing.Calculate(serviceTierFastBucket);
        Assert.NotNull(serviceTierResult.CostUsd);
        Assert.Equal(152.5m, serviceTierResult.CostUsd.Value);
    }

    [Fact]
    public void SparkIsUnpricedAndPlainCodex53HasItsOwnReference()
    {
        var pricing = new PricingService("dummy", PricingService.BuiltInDefaults());
        var bucket = new UsageBucket(ProviderKind.Codex, DateOnly.FromDateTime(DateTime.Today), null,
            "gpt-5.3-codex-spark", 1_000_000, 0, 100_000, 1, DataQuality.Exact, "fixture");
        Assert.Null(pricing.Calculate(bucket).CostUsd);
        Assert.Equal(3.15m, pricing.Calculate(bucket with { ModelId = "gpt-5.3-codex" }).CostUsd);
    }


}

