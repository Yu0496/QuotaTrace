using UsageTray.Core;
using UsageTray.Pricing;
using UsageTray.Providers.Codex;

namespace UsageTray.Tests;
 
public sealed class PricingServiceTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public PricingServiceTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void DebugRealBucketsCalculation()
    {
        var dbPath = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "UsageTray", "usage.db");
        if (!System.IO.File.Exists(dbPath)) return;
        var pricingPath = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "UsageTray", "pricing.json");
        var pricing = PricingService.LoadOrCreate(pricingPath);
        var db = new UsageTray.Data.UsageDatabase(dbPath);
        var repo = new UsageTray.Data.UsageRepository(db);
        var sessionFile = @"C:\Users\xiong\.codex\sessions\2026\09\05\rollout-2026-09-05T14-00-02-01a07027-0b2d-7882-9c1b-41482c1699ee.jsonl";
        if (System.IO.File.Exists(sessionFile))
        {
            using var fs = new System.IO.FileStream(sessionFile, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
            using var sr = new System.IO.StreamReader(fs);
            var records = new List<(int line, string type, string? subtype)>();
            int lineIdx = 0;
            while (sr.ReadLine() is { } line)
            {
                lineIdx++;
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = System.Text.Json.JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("type", out var t))
                {
                    var tStr = t.GetString();
                    if (tStr == "token_usage_record")
                    {
                        records.Add((lineIdx, tStr, null));
                    }
                    else if (tStr == "event_msg" && root.TryGetProperty("payload", out var p) && p.TryGetProperty("type", out var pt) && pt.GetString() == "token_count")
                    {
                        records.Add((lineIdx, "token_count", null));
                    }
                }
            }
            var tokenUsageLines = records.Where(r => r.type == "token_usage_record").Select(r => r.line).ToHashSet();
            var tokenCountLines = records.Where(r => r.type == "token_count").Select(r => r.line).ToHashSet();
            _output.WriteLine($"token_usage_record count: {tokenUsageLines.Count}, token_count count: {tokenCountLines.Count}");
            int isolatedCount = 0;
            for (int i = 0; i < records.Count; i++)
            {
                if (records[i].type == "token_usage_record")
                {
                    bool hasNextTokenCount = (i + 1 < records.Count && records[i + 1].type == "token_count" && records[i + 1].line - records[i].line <= 5);
                    if (!hasNextTokenCount)
                    {
                        isolatedCount++;
                        if (isolatedCount <= 5) _output.WriteLine($"Isolated token_usage_record at line {records[i].line}");
                    }
                }
            }
            _output.WriteLine($"Isolated token_usage_record total: {isolatedCount}");
        }
    }

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
        Assert.Contains(loaded.Rules, r => r.ModelPattern == "gpt-6");
        Assert.Contains(loaded.Rules, r => r.ModelPattern == "gpt-6-astra*");
        Assert.Contains(loaded.Rules, r => r.ModelPattern == "gemini-3.8-flash*");
        Assert.Contains(loaded.Rules, r => r.ModelPattern == "claude-sonnet-4-6*");
        Assert.Contains(loaded.Rules, r => r.ModelPattern == "gemini-3.7-flash*");
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
    public void CodexAutoReviewModelCalculatesCostAtLunaPricing()
    {
        var service = PricingService.BuiltInDefaults();
        var pricing = new PricingService("dummy", service);

        // codex-auto-review (1M uncached input = $0.2, 10M cached = $0.2, 100K output = $0.12 => $0.52)
        var reviewBucket = new UsageBucket(ProviderKind.Codex, new DateOnly(2026, 8, 31), null, "codex-auto-review",
            11_000_000, 10_000_000, 100_000, 1, DataQuality.Exact, "fixture", null, 0, CostQuality.ExactTokenSplit);
        var result = pricing.Calculate(reviewBucket);
        Assert.NotNull(result.CostUsd);
        Assert.Equal(0.52m, result.CostUsd.Value);
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
        Assert.Equal(122.0m, fastResult.CostUsd.Value);

        var serviceTierFastBucket = new UsageBucket(ProviderKind.Codex, new DateOnly(2026, 9, 5), null, "gpt-6-astra",
            2_000_000, 1_000_000, 1_000_000, 1, DataQuality.Exact, "fixture", null, 0, CostQuality.ExactTokenSplit,
            "fast");
        var serviceTierResult = pricing.Calculate(serviceTierFastBucket);
        Assert.NotNull(serviceTierResult.CostUsd);
        Assert.Equal(122.0m, serviceTierResult.CostUsd.Value);
    }

    [Fact]
    public void Gpt53CodexSparkPricing_CalculatesCorrectCost()
    {
        var pricing = new PricingService("dummy", PricingService.BuiltInDefaults());
        var bucket = new UsageBucket(ProviderKind.Codex, new DateOnly(2026, 9, 6), "test", "gpt-5.3-codex-spark",
            27992, 9856, 74, 1, DataQuality.Exact, "path", "sess1");
        var result = pricing.Calculate(bucket);

        Assert.True(result.IsPriced);
        Assert.NotNull(result.CostUsd);
        // nonCached = 27992 - 9856 = 18136 -> 18136 * 0.2 / 1M = 0.0036272
        // cached = 9856 -> 9856 * 0.02 / 1M = 0.00019712
        // output = 74 -> 74 * 1.2 / 1M = 0.0000888
        // total = 0.00391312
        Assert.Equal(0.00391312m, result.CostUsd.Value);
    }
}

