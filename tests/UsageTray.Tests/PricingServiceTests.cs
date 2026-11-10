using System.Drawing;
using System.Windows.Forms;
using UsageTray.Core;
using UsageTray.Pricing;
using UsageTray.Providers.Codex;
using UsageTray.UI;

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
    [InlineData("claude-sonnet-5-5-thinking", 14.7)]
    [InlineData("claude-opus-5-5-thinking", 29.2)]
    public void Claude55ModelsUseTheirOwnReferencePrices(string model, double expected)
    {
        var pricing = new PricingService("dummy", PricingService.BuiltInDefaults());
        var bucket = new UsageBucket(ProviderKind.Antigravity, new DateOnly(2026, 10, 4), null, model,
            3_000_000, 1_000_000, 1_000_000, 1, DataQuality.Exact, "fixture", null,
            1_000_000, CostQuality.ExactTokenSplit);

        Assert.Equal((decimal)expected, pricing.Calculate(bucket).CostUsd);
    }

    [Theory]
    [InlineData("claude-sonnet")]
    [InlineData("claude-opus")]
    [InlineData("claude-sonnet-5-6")]
    public void UnverifiedClaudeVersionsRemainUnpriced(string model)
    {
        var pricing = new PricingService("dummy", PricingService.BuiltInDefaults());
        var bucket = new UsageBucket(ProviderKind.Antigravity, new DateOnly(2026, 10, 4), null, model,
            1_000_000, 0, 1_000_000, 1, DataQuality.Exact, "fixture");

        Assert.Null(pricing.Calculate(bucket).CostUsd);
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
        Assert.Contains(loaded.Rules, r => r.ModelPattern == "gpt-6-sol");
        Assert.Contains(loaded.Rules, r => r.Provider == "Codex" && r.ModelPattern == "gpt-6.1-terra" && r.MatchMode == MatchMode.Exact);
        Assert.Contains(loaded.Rules, r => r.Provider == "Codex" && r.ModelPattern == "gpt-6.1-terra*" && r.MatchMode == MatchMode.Wildcard);
        Assert.Contains(loaded.Rules, r => r.Provider == "Codex" && r.ModelPattern == "gpt-6.1-terra-fast*" && r.MatchMode == MatchMode.Wildcard);
        Assert.Contains(loaded.Rules, r => r.Provider == "Antigravity" && r.ModelPattern == "gpt-6.1-terra" && r.MatchMode == MatchMode.Exact);
        Assert.Contains(loaded.Rules, r => r.Provider == "Antigravity" && r.ModelPattern == "gpt-6.1-terra*" && r.MatchMode == MatchMode.Wildcard);
        Assert.Contains(loaded.Rules, r => r.ModelPattern == "gpt-6-luna");
        Assert.Contains(loaded.Rules, r => r.ModelPattern == "gemini-3.8-flash");
        Assert.Contains(loaded.Rules, r => r.ModelPattern == "claude-sonnet-4-6*");
        Assert.Contains(loaded.Rules, r => r.ModelPattern == "claude-sonnet-5-5*");
        Assert.Contains(loaded.Rules, r => r.ModelPattern == "claude-opus-5-5*");
        Assert.Equal(12, loaded.Document.SchemaVersion);
        Assert.Equal(PricingService.DefaultDocumentVersion, loaded.Document.SchemaVersion);
        Assert.Contains(loaded.Rules, r => r.ModelPattern == "gemini-3.7-flash");
        Assert.Contains(loaded.Rules, r => r.ModelPattern == "gemini-pro-default*");
    }

    [Fact]
    public void DisplayNameInferenceResolvesUnknownModels()
    {
        Assert.Equal("claude-sonnet-4-6", UsageTray.Providers.Antigravity.AntigravitySqliteHistoryParser.InferModelFromDisplayName("Claude Sonnet 4.6 (Thinking)"));
        Assert.Equal("claude-opus-4-6-thinking", UsageTray.Providers.Antigravity.AntigravitySqliteHistoryParser.InferModelFromDisplayName("Claude Opus 4.6 (Thinking)"));
        Assert.Equal("claude-sonnet-5-5", UsageTray.Providers.Antigravity.AntigravitySqliteHistoryParser.InferModelFromDisplayName("Claude Sonnet 5.5 (Thinking)"));
        Assert.Equal("claude-opus-5-5-thinking", UsageTray.Providers.Antigravity.AntigravitySqliteHistoryParser.InferModelFromDisplayName("Claude Opus 5.5 (Thinking)"));
        Assert.Equal("claude-sonnet-4-5", UsageTray.Providers.Antigravity.AntigravitySqliteHistoryParser.InferModelFromDisplayName("Claude Sonnet 4.5"));
        Assert.Equal("claude-opus", UsageTray.Providers.Antigravity.AntigravitySqliteHistoryParser.InferModelFromDisplayName("Claude Opus"));
        Assert.Equal("gemini-3.8-flash", UsageTray.Providers.Antigravity.AntigravitySqliteHistoryParser.InferModelFromDisplayName("Gemini 3.8 Flash (High)"));
        Assert.Equal("gemini-3.5-flash", UsageTray.Providers.Antigravity.AntigravitySqliteHistoryParser.InferModelFromDisplayName("Gemini 3.5 Flash (High)"));
        Assert.Equal("gemini-3.1-pro", UsageTray.Providers.Antigravity.AntigravitySqliteHistoryParser.InferModelFromDisplayName("Gemini 3.1 Pro (High)"));
        Assert.Equal("gpt-6-astra", UsageTray.Providers.Antigravity.AntigravitySqliteHistoryParser.InferModelFromDisplayName("GPT-6 Astra (Preview)"));
        Assert.Equal("gpt-6-sol", UsageTray.Providers.Antigravity.AntigravitySqliteHistoryParser.InferModelFromDisplayName("GPT-6 Sol (Preview)"));
        Assert.Equal("gpt-6.1-terra", UsageTray.Providers.Antigravity.AntigravitySqliteHistoryParser.InferModelFromDisplayName("GPT-6.1 Terra (Thinking)"));
        Assert.Equal("gpt-6.1-terra", UsageTray.Providers.Antigravity.AntigravitySqliteHistoryParser.InferModelFromDisplayName("Terra"));
        Assert.Equal("gpt-5.6-terra", UsageTray.Providers.Antigravity.AntigravitySqliteHistoryParser.InferModelFromDisplayName("GPT-5.6 Terra"));
        Assert.Equal("gpt-6-luna", UsageTray.Providers.Antigravity.AntigravitySqliteHistoryParser.InferModelFromDisplayName("GPT-6 Luna (Thinking)"));
        Assert.Equal("gpt-6-sol", UsageTray.Providers.Antigravity.AntigravitySqliteHistoryParser.InferModelFromDisplayName("Sol"));
        Assert.Equal("gpt-6-luna", UsageTray.Providers.Antigravity.AntigravitySqliteHistoryParser.InferModelFromDisplayName("Luna"));
        Assert.Equal("gpt-oss-120b-medium", UsageTray.Providers.Antigravity.AntigravitySqliteHistoryParser.InferModelFromDisplayName("GPT-OSS 120B (Medium)"));
    }

    [Fact]
    public void Gpt6SolAndLunaCalculateAccurateCostsWithLongContext()
    {
        var pricing = new PricingService("dummy", PricingService.BuiltInDefaults());

        // gpt-6-sol: 1M uncached input = $2.0, 1M cache-read = $0.2, 1M cache-write = $2.5, 100k output = $1.0 => $5.70
        var solBucket = new UsageBucket(ProviderKind.Codex, new DateOnly(2026, 9, 23), null, "gpt-6-sol",
            3_000_000, 1_000_000, 100_000, 1, DataQuality.Exact, "fixture", null, 1_000_000, CostQuality.ExactTokenSplit);
        var solResult = pricing.Calculate(solBucket);
        Assert.NotNull(solResult.CostUsd);
        Assert.Equal(5.70m, solResult.CostUsd.Value);

        // gpt-6-luna: 1M uncached input = $0.10, 1M cache-read = $0.01, 1M cache-write = $0.125, 100k output = $0.05 => $0.285
        var lunaBucket = new UsageBucket(ProviderKind.Codex, new DateOnly(2026, 9, 23), null, "gpt-6-luna",
            3_000_000, 1_000_000, 100_000, 1, DataQuality.Exact, "fixture", null, 1_000_000, CostQuality.ExactTokenSplit);
        var lunaResult = pricing.Calculate(lunaBucket);
        Assert.NotNull(lunaResult.CostUsd);
        Assert.Equal(0.285m, lunaResult.CostUsd.Value);
    }

    [Fact]
    public void CodexAutoReviewIsFreeAndNotPriced()
    {
        var service = PricingService.BuiltInDefaults();
        var pricing = new PricingService("dummy", service);

        // codex-auto-review 官方现已直接免费，审核模型不再计费/计价 ($0.00)
        var reviewBucket = new UsageBucket(ProviderKind.Codex, new DateOnly(2026, 8, 31), null, "codex-auto-review",
            11_000_000, 10_000_000, 100_000, 1, DataQuality.Exact, "fixture", null, 0, CostQuality.ExactTokenSplit);
        var result = pricing.Calculate(reviewBucket);
        Assert.Equal(0.0m, result.CostUsd);
        Assert.Equal(CostQuality.ExactTokenSplit, result.Quality);

        // 通用 moderation 模型同样免费
        var modBucket = new UsageBucket(ProviderKind.Codex, new DateOnly(2026, 10, 8), null, "omni-moderation-latest",
            500_000, 200_000, 10_000, 1, DataQuality.Exact, "fixture", null, 0, CostQuality.ExactTokenSplit);
        var modResult = pricing.Calculate(modBucket);
        Assert.Equal(0.0m, modResult.CostUsd);
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

    [Fact]
    public void Gpt61SolAndLunaCalculateCorrectStandardAndFastCost()
    {
        var pricing = new PricingService("dummy", PricingService.BuiltInDefaults());

        // 1. gpt-6.1-sol standard tier: 2M input (1M uncached @ $2.0, 1M cached @ $0.2) + 1M output @ $10.0 = $12.20
        var solBucket = new UsageBucket(ProviderKind.Codex, new DateOnly(2026, 10, 11), null, "gpt-6.1-sol",
            2_000_000, 1_000_000, 1_000_000, 1, DataQuality.Exact, "fixture", null, 0, CostQuality.ExactTokenSplit);
        var solResult = pricing.Calculate(solBucket);
        Assert.NotNull(solResult.CostUsd);
        Assert.Equal(12.2m, solResult.CostUsd.Value);

        // 2. gpt-6.1-sol fast tier: 2M input (1M uncached @ $5.0, 1M cached @ $0.5) + 1M output @ $25.0 = $30.50
        var solFastBucket = solBucket with { ServiceTier = "fast" };
        var solFastResult = pricing.Calculate(solFastBucket);
        Assert.NotNull(solFastResult.CostUsd);
        Assert.Equal(30.5m, solFastResult.CostUsd.Value);

        // 3. gpt-6.1-luna standard tier: 2M input (1M uncached @ $0.1, 1M cached @ $0.01) + 1M output @ $0.5 = $0.61
        var lunaBucket = new UsageBucket(ProviderKind.Codex, new DateOnly(2026, 10, 11), null, "gpt-6.1-luna",
            2_000_000, 1_000_000, 1_000_000, 1, DataQuality.Exact, "fixture", null, 0, CostQuality.ExactTokenSplit);
        var lunaResult = pricing.Calculate(lunaBucket);
        Assert.NotNull(lunaResult.CostUsd);
        Assert.Equal(0.61m, lunaResult.CostUsd.Value);

        // 4. gpt-6.1-luna fast tier: 2M input (1M uncached @ $0.2, 1M cached @ $0.02) + 1M output @ $1.0 = $1.22
        var lunaFastBucket = lunaBucket with { ServiceTier = "fast" };
        var lunaFastResult = pricing.Calculate(lunaFastBucket);
        Assert.NotNull(lunaFastResult.CostUsd);
        Assert.Equal(1.22m, lunaFastResult.CostUsd.Value);
    }

    [Fact]
    public void Gemini38FlashNCalculatesCorrectCostAndMatchesNormalizedVariants()
    {
        var pricing = new PricingService("dummy", PricingService.BuiltInDefaults());

        // 1. Exact model ID: gemini-3.8-flash-n: 2M input (1M uncached @ $1.5, 1M cached @ $0.15) + 1M output @ $7.5 = $9.15
        var bucket = new UsageBucket(ProviderKind.Antigravity, new DateOnly(2026, 10, 11), null, "gemini-3.8-flash-n",
            2_000_000, 1_000_000, 1_000_000, 1, DataQuality.Exact, "fixture", null, 0, CostQuality.ExactTokenSplit);
        var result = pricing.Calculate(bucket);
        Assert.NotNull(result.CostUsd);
        Assert.Equal(9.15m, result.CostUsd.Value);

        // 2. Space separated model ID: "gemini 3.8 flash n" normalizes and matches
        var spaceResult = pricing.Calculate(bucket with { ModelId = "gemini 3.8 flash n" });
        Assert.NotNull(spaceResult.CostUsd);
        Assert.Equal(9.15m, spaceResult.CostUsd.Value);

        // 3. Suffix variant: gemini-3.8-flash-n-preview matches wildcard
        var variantResult = pricing.Calculate(bucket with { ModelId = "gemini-3.8-flash-n-preview" });
        Assert.NotNull(variantResult.CostUsd);
        Assert.Equal(9.15m, variantResult.CostUsd.Value);

        // 4. InferModelFromDisplayName parses "Gemini 3.8 Flash N"
        var inferred = UsageTray.Providers.Antigravity.AntigravitySqliteHistoryParser.InferModelFromDisplayName("Gemini 3.8 Flash N");
        Assert.Equal("gemini-3.8-flash-n", inferred);
    }

    [Fact]
    public void Gpt61TerraCalculatesAccurateStandardLongContextAndFastCost()
    {
        var pricing = new PricingService("dummy", PricingService.BuiltInDefaults());

        // 1. Codex 标准档: 2M input (1M uncached @ $1, 1M cached @ $0.05) + 1M output @ $5 = $6.05
        var stdBucket = new UsageBucket(ProviderKind.Codex, new DateOnly(2026, 11, 10), null, "gpt-6.1-terra",
            2_000_000, 1_000_000, 1_000_000, 1, DataQuality.Exact, "fixture", null, 0, CostQuality.ExactTokenSplit);
        Assert.Equal(6.05m, pricing.Calculate(stdBucket).CostUsd);

        // 2. Codex 含 cache write: 1M uncached @ $1 + 500k read @ $0.05 + 500k write @ $1.25 + 1M output @ $5 = $6.65
        var cacheWriteBucket = new UsageBucket(ProviderKind.Codex, new DateOnly(2026, 11, 10), null, "gpt-6.1-terra",
            2_000_000, 500_000, 1_000_000, 1, DataQuality.Exact, "fixture", null, 500_000, CostQuality.ExactTokenSplit);
        Assert.Equal(6.65m, pricing.Calculate(cacheWriteBucket).CostUsd);

        // 3. Codex Fast / priority 档位 (2.5x): 1M uncached @ $2.5 + 1M cached @ $0.125 + 1M output @ $12.5 = $15.125
        Assert.Equal(15.125m, pricing.Calculate(stdBucket with { ServiceTier = "fast" }).CostUsd);
        Assert.Equal(15.125m, pricing.Calculate(stdBucket with { ServiceTier = "priority" }).CostUsd);

        // 4. gpt-6.1-terra-fast 模型前缀匹配: 1M uncached @ $2.5 + 500k read @ $0.125 + 500k write @ $3.125 + 1M output @ $12.5 = $16.625
        Assert.Equal(16.625m, pricing.Calculate(cacheWriteBucket with { ModelId = "gpt-6.1-terra-fast" }).CostUsd);

        // 5. Codex 长上下文 (>272K): 500k input (300k uncached @ $2, 200k cached @ $0.1) + 100k output @ $7.5 = $1.37
        var longBucket = new UsageBucket(ProviderKind.Codex, new DateOnly(2026, 11, 10), null, "gpt-6.1-terra",
            500_000, 200_000, 100_000, 1, DataQuality.Exact, "fixture", null, 0, CostQuality.ExactTokenSplit,
            null, 1, 0, 500_000, 200_000, 0, 100_000, true);
        Assert.Equal(1.37m, pricing.Calculate(longBucket).CostUsd);

        // 6. Codex 长上下文 Fast: 500k input (300k uncached @ $5, 200k cached @ $0.25) + 100k output @ $18.75 = $3.425
        Assert.Equal(3.425m, pricing.Calculate(longBucket with { ServiceTier = "fast" }).CostUsd);

        // 7. Antigravity gpt-6.1-terra 与变体后缀通配
        var agBucket = stdBucket with { Provider = ProviderKind.Antigravity };
        Assert.Equal(6.05m, pricing.Calculate(agBucket).CostUsd);
        Assert.Equal(6.05m, pricing.Calculate(agBucket with { ModelId = "gpt-6.1-terra-preview" }).CostUsd);
    }

    [Fact]
    public void MigrateDocumentPreservesWildcardAndDeduplicatesClonedGpt6Rules()
    {
        using var workspace = new TempWorkspace();
        var defaults = PricingService.BuiltInDefaults();
        var codexGpt6 = defaults.Rules.Single(r => r.Provider == "Codex" && r.ModelPattern == "gpt-6" && r.MatchMode == MatchMode.Exact);

        // 1. 模拟 schema v11 中出现多次重复的 Codex | gpt-6 | Exact 规则，确认迁移后会自动去重且补齐 gpt-6.1-terra
        var duplicatedV11 = new PricingDocument(11, new DateOnly(2026, 10, 17), [
            codexGpt6,
            codexGpt6 with { },
            codexGpt6 with { }
        ]);
        var migratedFromV11 = new PricingService(workspace.File("v11.json"), duplicatedV11);
        Assert.Equal(1, migratedFromV11.Rules.Count(r => r.Provider == "Codex" && r.ModelPattern == "gpt-6" && r.MatchMode == MatchMode.Exact));
        Assert.Contains(migratedFromV11.Rules, r => r.Provider == "Codex" && r.ModelPattern == "gpt-6.1-terra");

        // 2. 模拟 legacy v2 文档包含旧版 gpt-6* Wildcard，确认不会被错误映射为 gpt-6 Exact 克隆体
        var legacyGpt6Wildcard = new PricingRule(
            "Codex",
            "gpt-6*",
            MatchMode.Wildcard,
            10.0m,
            1.0m,
            12.5m,
            50.0m,
            "https://developers.openai.com/api/docs/models/gpt-6-astra",
            new DateOnly(2026, 9, 5),
            true,
            new TokenPriceSet(20.0m, 2.0m, 25.0m, 75.0m),
            272000);
        var legacyDoc = new PricingDocument(2, new DateOnly(2026, 9, 5), [legacyGpt6Wildcard]);
        var migratedFromV2 = new PricingService(workspace.File("v2.json"), legacyDoc);

        var gpt6Wildcard = Assert.Single(migratedFromV2.Rules, r => r.Provider == "Codex" && r.ModelPattern == "gpt-6*" && r.MatchMode == MatchMode.Wildcard);
        Assert.NotNull(gpt6Wildcard.UnverifiedReason);
        Assert.Equal(1, migratedFromV2.Rules.Count(r => r.Provider == "Codex" && r.ModelPattern == "gpt-6" && r.MatchMode == MatchMode.Exact));
    }

    [Fact]
    public void PricingViewerForm_LayoutPreventsTextTruncation()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var largeFont = new Font("Segoe UI", 18F);
                using var restoredFont = new Font("Segoe UI", 9F);
                using var form = new PricingViewerForm(() => PricingService.BuiltInDefaults().Rules)
                {
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(-32000, -32000),
                    ShowInTaskbar = false
                };
                form.Show();
                Application.DoEvents();

                var topPanel = form.Controls.OfType<FlowLayoutPanel>().Single();
                var combo = topPanel.Controls.OfType<ComboBox>().Single();
                var searchBox = topPanel.Controls.OfType<TextBox>().Single();
                var refreshBtn = topPanel.Controls.OfType<Button>().Single();
                var grid = form.Controls.OfType<DataGridView>().Single();
                var bottomPanel = form.Controls.OfType<Panel>().First(p => p is not FlowLayoutPanel);
                var statusLabel = bottomPanel.Controls.OfType<Label>().Single();
                var closeBtn = bottomPanel.Controls.OfType<Button>().Single();

                static void AssertLayoutFits(Form targetForm, FlowLayoutPanel topPanel, ComboBox combo, TextBox searchBox,
                    Button refreshBtn, DataGridView grid, Label statusLabel, Button closeBtn)
                {
                    Assert.True(combo.Width >= TextRenderer.MeasureText("全部 Provider", combo.Font).Width + 20,
                        $"ComboBox 宽度不足以完整显示文字: Width={combo.Width}");
                    Assert.True(searchBox.Width >= TextRenderer.MeasureText("按模型名称搜索…", searchBox.Font).Width + 12,
                        $"搜索框宽度不足: Width={searchBox.Width}");
                    Assert.True(refreshBtn.Width >= TextRenderer.MeasureText(refreshBtn.Text, refreshBtn.Font).Width + 12,
                        $"刷新按钮宽度不足: Width={refreshBtn.Width}");
                    Assert.True(combo.Bottom <= topPanel.ClientSize.Height, $"ComboBox 底部超出顶部工具栏: Bottom={combo.Bottom}, TopH={topPanel.ClientSize.Height}");
                    Assert.True(searchBox.Bottom <= topPanel.ClientSize.Height, $"搜索框底部超出顶部工具栏: Bottom={searchBox.Bottom}, TopH={topPanel.ClientSize.Height}");
                    Assert.True(refreshBtn.Bottom <= topPanel.ClientSize.Height, $"刷新按钮底部超出顶部工具栏: Bottom={refreshBtn.Bottom}, TopH={topPanel.ClientSize.Height}");

                    var headerFont = grid.ColumnHeadersDefaultCellStyle.Font ?? grid.Font;
                    var headerTextH = TextRenderer.MeasureText("模型模式 (Pattern)", headerFont).Height;
                    Assert.True(grid.ColumnHeadersHeight >= headerTextH + grid.ColumnHeadersDefaultCellStyle.Padding.Vertical,
                        $"表头高度不足导致裁剪: HeaderH={grid.ColumnHeadersHeight}, TextH={headerTextH}");

                    foreach (DataGridViewColumn col in grid.Columns)
                    {
                        var textW = TextRenderer.MeasureText(col.HeaderText, headerFont).Width;
                        Assert.True(col.Width >= textW + grid.ColumnHeadersDefaultCellStyle.Padding.Horizontal,
                            $"列 {col.HeaderText} 宽度不足: Width={col.Width}, TextW={textW}");
                    }

                    var cellFont = grid.DefaultCellStyle.Font ?? grid.Font;
                    var cellTextH = TextRenderer.MeasureText("Agpy0123", cellFont).Height;
                    Assert.True(grid.Rows.Count > 0);
                    foreach (DataGridViewRow row in grid.Rows)
                    {
                        Assert.True(row.Height >= cellTextH + grid.DefaultCellStyle.Padding.Vertical,
                            $"第 {row.Index} 行高不足导致文字截断: RowH={row.Height}, TextH={cellTextH}");
                    }

                    Assert.True(closeBtn.Width >= TextRenderer.MeasureText(closeBtn.Text, closeBtn.Font).Width + 12);
                    Assert.True(statusLabel.Right <= closeBtn.Left + 2);
                    Assert.True(closeBtn.Bottom <= targetForm.ClientSize.Height);
                }

                AssertLayoutFits(form, topPanel, combo, searchBox, refreshBtn, grid, statusLabel, closeBtn);

                var terraRow = grid.Rows.Cast<DataGridViewRow>()
                    .FirstOrDefault(r => string.Equals(r.Cells[0].Value?.ToString(), "Codex", StringComparison.Ordinal) &&
                                         string.Equals(r.Cells[1].Value?.ToString(), "gpt-6.1-terra", StringComparison.Ordinal));
                Assert.NotNull(terraRow);
                Assert.Equal("$1", terraRow!.Cells[3].Value);
                Assert.Equal("$0.05", terraRow.Cells[4].Value);
                Assert.Equal("$1.25", terraRow.Cells[5].Value);
                Assert.Equal("$5", terraRow.Cells[6].Value);

                var terraFastRow = grid.Rows.Cast<DataGridViewRow>()
                    .FirstOrDefault(r => string.Equals(r.Cells[0].Value?.ToString(), "Codex", StringComparison.Ordinal) &&
                                         string.Equals(r.Cells[1].Value?.ToString(), "gpt-6.1-terra-fast*", StringComparison.Ordinal));
                Assert.NotNull(terraFastRow);
                Assert.Equal("$0.125", terraFastRow!.Cells[4].Value);
                Assert.Equal("$3.125", terraFastRow.Cells[5].Value);

                form.Font = largeFont;
                Application.DoEvents();
                AssertLayoutFits(form, topPanel, combo, searchBox, refreshBtn, grid, statusLabel, closeBtn);
                form.Font = restoredFont;

                searchBox.Text = "gpt-6.1-terra";
                Application.DoEvents();
                Assert.Equal(5, grid.Rows.Count);
                Assert.All(grid.Rows.Cast<DataGridViewRow>(), r =>
                    Assert.Contains("gpt-6.1-terra", r.Cells[1].Value?.ToString(), StringComparison.OrdinalIgnoreCase));

                using var bmp = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                form.Close();
                Application.DoEvents();
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (threadEx is not null)
            throw new Exception("PricingViewerForm layout test failed", threadEx);
    }
}

