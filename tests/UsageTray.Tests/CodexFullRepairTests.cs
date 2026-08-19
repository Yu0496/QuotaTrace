using System.Text.Json;
using Microsoft.Data.Sqlite;
using UsageTray.Core;
using UsageTray.Pricing;
using UsageTray.Data;
using UsageTray.App;
using UsageTray.Providers;
using UsageTray.Providers.Codex;

namespace UsageTray.Tests;

public sealed class CodexFullRepairTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 20, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ModelSwitchUsesOneGlobalCounterAndAttributesDeltasToEventModel()
    {
        var normalizer = new CodexUsageNormalizer();
        var result = normalizer.Normalize([
            Snapshot("s", 0, "gpt-5.6-sol", 100, 100, 10, lastOutput: 10),
            Snapshot("s", 1, "gpt-5.6-sol", 160, 60, 20, lastOutput: 10),
            Snapshot("s", 2, "gpt-5.6-terra", 220, 60, 30, lastOutput: 10),
            Snapshot("s", 3, "gpt-5.6-sol", 280, 60, 40, lastOutput: 10)
        ]);

        Assert.Equal(280, result.Buckets.Sum(item => item.InputTokens));
        Assert.Equal(40, result.Buckets.Sum(item => item.OutputTokens));
        Assert.Equal(220, result.Buckets.Single(item => item.ModelId == "gpt-5.6-sol").InputTokens);
        Assert.Equal(60, result.Buckets.Single(item => item.ModelId == "gpt-5.6-terra").InputTokens);
    }

    [Fact]
    public void CounterRewindStartsNewEpochWithoutNegativeOrDoubleDelta()
    {
        var result = new CodexUsageNormalizer().Normalize([
            Snapshot("s", 0, "gpt-5.6-sol", 100, 100, 1, lastOutput: 1),
            Snapshot("s", 1, "gpt-5.6-sol", 180, 80, 2, lastOutput: 1),
            Snapshot("s", 2, "gpt-5.6-sol", 50, 0, 3, lastOutput: 1),
            Snapshot("s", 3, "gpt-5.6-sol", 90, 40, 4, lastOutput: 1)
        ]);

        Assert.Equal(220, result.Buckets.Sum(item => item.InputTokens));
        Assert.Equal(1, result.CounterRewindCount);
        Assert.All(result.Buckets, item => Assert.True(item.InputTokens >= 0));
    }

    [Fact]
    public void DuplicateEventKeysAreCountedOnceAcrossSourcePaths()
    {
        var first = Snapshot("s", 0, "gpt-5.6-sol", 100, 100, 1, "active.jsonl", lastOutput: 1);
        var duplicate = first with { SourcePath = "archived.jsonl" };
        var result = new CodexUsageNormalizer().Normalize([first, duplicate,
            Snapshot("s", 1, "gpt-5.6-sol", 160, 60, 2, "archived.jsonl", lastOutput: 1)]);

        Assert.Equal(160, result.Buckets.Sum(item => item.InputTokens));
        Assert.Equal(1, result.DuplicateEventCount);
        Assert.Contains(result.Events, item => item.IsDuplicate);
    }

    [Fact]
    public void DominatedOverlappingBranchIsAuditedButNotAddedToLedger()
    {
        var result = new CodexUsageNormalizer().Normalize([
            Snapshot("s", 0, "gpt-5.6-sol", 100, 100, 1, "complete.jsonl", lastOutput: 1),
            Snapshot("s", 3, "gpt-5.6-sol", 200, 100, 2, "complete.jsonl", lastOutput: 1),
            Snapshot("s", 1, "gpt-5.6-sol", 100, 100, 1, "branch.jsonl", lastOutput: 1)
        ]);

        Assert.Equal(200, result.Buckets.Sum(item => item.InputTokens));
        Assert.Equal(1, result.SuppressedSourceCount);
        Assert.Equal(1, result.SuppressedEventCount);
        Assert.Contains(result.Sources, item => item.SourcePath == "branch.jsonl");
    }

    [Theory]
    [InlineData(272000, false)]
    [InlineData(272001, true)]
    public void LongContextStartsOnlyAbove272KWhenLastUsageMatches(long lastInput, bool expectedLong)
    {
        var result = new CodexUsageNormalizer().Normalize([
            Snapshot("s", 0, "gpt-5.6-sol", 1, 1, 1),
            Snapshot("s", 1, "gpt-5.6-sol", lastInput + 1, lastInput, 2, lastOutput: 1)
        ]);

        var bucket = Assert.Single(result.Buckets);
        Assert.Equal(expectedLong ? 1 : 0, bucket.LongContextRequestCount);
        Assert.Equal(expectedLong ? lastInput : 0, bucket.LongContextInputTokens);
    }

    [Fact]
    public void MissingCacheWriteIsNotSilentlyConvertedToZero()
    {
        var result = new CodexUsageNormalizer().Normalize([
            Snapshot("s", 0, "gpt-5.6-sol", 100, 100, 1, cacheWrite: null, cached: 50)
        ]);
        var bucket = Assert.Single(result.Buckets);
        Assert.False(bucket.CacheWriteAvailable);
        Assert.Equal(50, bucket.CachedInputTokens);
        var price = new PricingService("pricing.json", new PricingDocument(2, DateOnly.FromDateTime(DateTime.Today), [
            new PricingRule("Codex", "gpt-5.6-sol", MatchMode.Exact, 5m, 0.5m, 6.25m, 30m, "fixture", DateOnly.FromDateTime(DateTime.Today))
        ])).Calculate(bucket);
        Assert.Null(price.CostUsd);
        Assert.Equal(CostQuality.CacheWriteUnavailable, price.Quality);
    }

    [Fact]
    public void LongContextPricingUsesSeparatePriceSetAndUnknownModelHasNoWildcardFallback()
    {
        var rule = new PricingRule("Codex", "gpt-5.6-sol", MatchMode.Exact, 5m, 0.5m, 6.25m, 30m,
            "fixture", DateOnly.FromDateTime(DateTime.Today), true, new TokenPriceSet(10m, 1m, 12.5m, 45m));
        var service = new PricingService("pricing.json", new PricingDocument(2, DateOnly.FromDateTime(DateTime.Today), [rule]));
        var longBucket = new UsageBucket(ProviderKind.Codex, DateOnly.FromDateTime(DateTime.Today), null, "gpt-5.6-sol",
            1_000_000, 0, 1_000_000, 1, DataQuality.Exact, "fixture", null, 0, CostQuality.ExactTokensNoCache,
            null, 1, 0, 1_000_000, 0, 0, 1_000_000, true);
        Assert.Equal(55m, service.Calculate(longBucket).CostUsd);

        var unknown = new PricingService("pricing.json", new PricingDocument(2, DateOnly.FromDateTime(DateTime.Today), [
            new PricingRule("Codex", "gpt-5.6-sol", MatchMode.Exact, 5m, 0.5m, null, 30m, "fixture", DateOnly.FromDateTime(DateTime.Today))
        ])).Calculate(new UsageBucket(ProviderKind.Codex, DateOnly.FromDateTime(DateTime.Today), null, "gpt-5.9-unknown",
            100, 0, 10, 1, DataQuality.Exact, "fixture"));
        Assert.Null(unknown.CostUsd);
        Assert.Equal(CostQuality.Unavailable, unknown.Quality);
    }

    [Fact]
    public void NonStandardServiceTierDoesNotSilentlyUseStandardApiPrice()
    {
        var rule = new PricingRule("Codex", "gpt-5.6-sol", MatchMode.Exact, 5m, 0.5m, null, 30m,
            "fixture", DateOnly.FromDateTime(DateTime.Today));
        var bucket = new UsageBucket(ProviderKind.Codex, DateOnly.FromDateTime(DateTime.Today), null, "gpt-5.6-sol",
            100, 0, 10, 1, DataQuality.Exact, "fixture", null, 0, CostQuality.ExactTokensNoCache, "fast");
        var result = new PricingService("pricing.json", new PricingDocument(2, DateOnly.FromDateTime(DateTime.Today), [rule])).Calculate(bucket);
        Assert.Null(result.CostUsd);
        Assert.Equal(CostQuality.Unavailable, result.Quality);
    }

    [Fact]
    public void ReasoningOutputIsAReportedSubsetAndIsNotAddedAgain()
    {
        var snapshot = Snapshot("s", 0, "gpt-5.6-sol", 100, 100, 20) with
        {
            TotalUsage = new CodexCumulativeUsage(100, 0, 20, 0, 18),
            LastUsage = new CodexRequestUsage(100, 0, 20, 0, 18)
        };
        var result = new CodexUsageNormalizer().Normalize([snapshot]);
        Assert.Equal(20, result.Buckets.Sum(item => item.OutputTokens));
    }

    [Fact]
    public void SourceClassifierDoesNotLeaveUnclassifiedOverlap()
    {
        var a = new[] { "a", "b" };
        var b = new[] { "a", "b", "c" };
        var c = new[] { "b", "x" };
        Assert.Equal("prefix", CodexSourceClassifier.Classify(a, b));
        Assert.Equal("partial", CodexSourceClassifier.Classify(a, c));
        Assert.Equal("independent", CodexSourceClassifier.Classify(["a"], ["x"]));
    }

    [Fact]
    public void ReferenceParserAgreesWithProductionParserOnFixtureWithoutCallingIt()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("reference.jsonl");
        File.WriteAllLines(path, [
            "{\"timestamp\":\"2026-08-20T00:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":100,\"cached_input_tokens\":20,\"output_tokens\":4},\"last_token_usage\":{\"input_tokens\":100,\"cached_input_tokens\":20,\"output_tokens\":4}}}}",
            "{\"timestamp\":\"2026-08-20T00:01:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":170,\"cached_input_tokens\":30,\"output_tokens\":9},\"last_token_usage\":{\"input_tokens\":70,\"cached_input_tokens\":10,\"output_tokens\":5}}}}"
        ]);

        var reference = ReferenceParser.Parse(path);
        var production = new CodexJsonlParser().ParseFile(path);
        Assert.Equal(reference.Input, production.Buckets.Sum(item => item.InputTokens));
        Assert.Equal(reference.Cached, production.Buckets.Sum(item => item.CachedInputTokens));
        Assert.Equal(reference.Output, production.Buckets.Sum(item => item.OutputTokens));
    }

    [Fact]
    public void RealShortGoldenSessionMatchesExpectedCumulativeFinal()
    {
        const string path = "C:\\Users\\xiong\\.codex\\archived_sessions\\rollout-2026-08-13T21-57-20-019ffb69-c293-7f01-a81b-be85256432f2.jsonl";
        if (!File.Exists(path)) return;
        var result = new CodexJsonlParser().ParseFile(path);
        Assert.Equal(185286, result.Buckets.Sum(item => item.InputTokens));
        Assert.Equal(157952, result.Buckets.Sum(item => item.CachedInputTokens));
        Assert.Equal(2518, result.Buckets.Sum(item => item.OutputTokens));
    }

    [Fact]
    public void RealLongGoldenSessionMatchesExpectedGlobalFinal()
    {
        const string path = "C:\\Users\\xiong\\.codex\\archived_sessions\\rollout-2026-07-11T02-18-37-019f4d40-afae-74b1-8c32-89927f0860e0.jsonl";
        if (!File.Exists(path)) return;
        var result = new CodexJsonlParser().ParseFile(path);
        Assert.Equal(100366344, result.Buckets.Sum(item => item.InputTokens));
        Assert.Equal(97597440, result.Buckets.Sum(item => item.CachedInputTokens));
        Assert.Equal(247824, result.Buckets.Sum(item => item.OutputTokens));
    }

    [Fact]
    public async Task ProviderRescanRestartAndDeleteAreIdempotent()
    {
        using var workspace = new TempWorkspace();
        var root = workspace.File("codex-root");
        Directory.CreateDirectory(root);
        var session = Path.Combine(root, "session.jsonl");
        File.WriteAllText(session, "{\"timestamp\":\"2026-08-20T00:00:00Z\",\"type\":\"event_msg\",\"model\":\"gpt-5.6-sol\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":100,\"cached_input_tokens\":20,\"cache_write_input_tokens\":3,\"output_tokens\":5},\"last_token_usage\":{\"input_tokens\":100,\"cached_input_tokens\":20,\"cache_write_input_tokens\":3,\"output_tokens\":5}}}}" + Environment.NewLine);
        var (database, repository) = RepositoryFactory.Create(workspace);
        var pricing = new PricingService(workspace.File("pricing.json"), new PricingDocument(2, DateOnly.FromDateTime(DateTime.Today), []));
        var settings = new AppSettings();
        var provider = new CodexProvider(new CodexSessionLocator([root]));
        var context = new RefreshContext(settings, repository, pricing);

        await provider.RefreshAsync(context, CancellationToken.None);
        Assert.Equal(100, repository.GetUsage(new DateRange(new DateOnly(2026, 8, 20), new DateOnly(2026, 8, 20)), ProviderKind.Codex).Sum(item => item.InputTokens));
        await provider.RefreshAsync(context, CancellationToken.None);
        Assert.Equal(100, repository.GetUsage(new DateRange(new DateOnly(2026, 8, 20), new DateOnly(2026, 8, 20)), ProviderKind.Codex).Sum(item => item.InputTokens));
        database.Dispose();

        var (restartedDatabase, restartedRepository) = RepositoryFactory.Create(workspace);
        Assert.Equal(100, restartedRepository.GetUsage(new DateRange(new DateOnly(2026, 8, 20), new DateOnly(2026, 8, 20)), ProviderKind.Codex).Sum(item => item.InputTokens));
        File.Delete(session);
        await new CodexProvider(new CodexSessionLocator([root])).RefreshAsync(new RefreshContext(settings, restartedRepository, pricing), CancellationToken.None);
        Assert.Empty(restartedRepository.GetUsage(new DateRange(new DateOnly(2026, 8, 20), new DateOnly(2026, 8, 20)), ProviderKind.Codex));
        restartedDatabase.Dispose();
    }

    [Fact]
    public void MovingSameSessionBetweenSourcesDoesNotDuplicateLogicalUsage()
    {
        using var workspace = new TempWorkspace();
        var oldPath = workspace.File("active.jsonl");
        var newPath = workspace.File("archived.jsonl");
        var snapshots = new[] { Snapshot("s", 0, "gpt-5.6-sol", 100, 100, 1, oldPath, lastOutput: 1) };
        using var database = new UsageDatabase(workspace.File("usage.db"));
        var repository = new UsageRepository(database);
        var oldInfo = new FileInfo(oldPath); File.WriteAllText(oldPath, "{}"); oldInfo.Refresh();
        var newInfo = new FileInfo(newPath); File.WriteAllText(newPath, "{}"); newInfo.Refresh();
        repository.ReplaceCodexSource(oldInfo, snapshots, "s", "Demo", "gpt-5.6-sol", null);
        repository.ReplaceCodexSource(newInfo, snapshots.Select(item => item with { SourcePath = newPath }).ToArray(), "s", "Demo", "gpt-5.6-sol", null);
        var normalized = new CodexUsageNormalizer().Normalize(repository.GetCodexSnapshots());
        repository.ReplaceCodexLogicalUsage(normalized.Buckets);
        Assert.Equal(100, repository.GetUsage(new DateRange(new DateOnly(2026, 8, 20), new DateOnly(2026, 8, 20)), ProviderKind.Codex).Sum(item => item.InputTokens));
    }

    [Fact]
    public void ExistingV1DatabaseGetsNewColumnsAndRebuildFlagWithoutDroppingLegacyRows()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("legacy.db");
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
CREATE TABLE source_files(provider TEXT NOT NULL,path TEXT NOT NULL,file_size INTEGER NOT NULL,mtime_utc_ticks INTEGER NOT NULL,
parsed_bytes INTEGER NOT NULL DEFAULT 0,parser_version INTEGER NOT NULL DEFAULT 1,session_id TEXT NULL,project_key TEXT NULL,last_model TEXT NULL,parser_state_json TEXT NULL,last_error TEXT NULL,PRIMARY KEY(provider,path));
CREATE TABLE file_usage(provider TEXT NOT NULL,source_path TEXT NOT NULL,local_date TEXT NOT NULL,project_key TEXT NOT NULL DEFAULT '',model_id TEXT NOT NULL DEFAULT '',input_tokens INTEGER NOT NULL,cached_input_tokens INTEGER NOT NULL,cache_write_input_tokens INTEGER NOT NULL DEFAULT 0,output_tokens INTEGER NOT NULL,request_count INTEGER NOT NULL DEFAULT 0,data_quality INTEGER NOT NULL,cost_quality INTEGER NOT NULL DEFAULT 1,session_id TEXT NULL,PRIMARY KEY(provider,source_path,local_date,project_key,model_id));
CREATE TABLE app_metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL);
INSERT INTO file_usage(provider,source_path,local_date,project_key,model_id,input_tokens,cached_input_tokens,output_tokens,request_count,data_quality,cost_quality) VALUES('Codex','legacy.jsonl','2026-08-20','','gpt-5.6-sol',999,100,3,1,1,1);";
            command.ExecuteNonQuery();
        }
        using var database = new UsageDatabase(path);
        var repository = new UsageRepository(database);
        Assert.Equal("1", repository.GetFlag("codex_rebuild_required"));
        Assert.Equal("3", repository.GetFlag("codex_schema_version"));
        Assert.Equal(999, repository.GetUsage(new DateRange(new DateOnly(2026, 8, 20), new DateOnly(2026, 8, 20)), ProviderKind.Codex).Sum(item => item.InputTokens));
    }

    private static CodexTokenSnapshot Snapshot(string session, int seconds, string model, long totalInput, long lastInput,
        long output, string source = "fixture.jsonl", long? cacheWrite = 0, long cached = 0, long? lastOutput = null) =>
        new(session, T0.AddSeconds(seconds), model, "Demo", null,
            new CodexCumulativeUsage(totalInput, cached, output, cacheWrite),
            new CodexRequestUsage(lastInput, Math.Min(cached, lastInput), lastOutput ?? output, cacheWrite), null, source, seconds);

    private sealed record ReferenceResult(long Input, long Cached, long Output);

    private static class ReferenceParser
    {
        public static ReferenceResult Parse(string path)
        {
            long input = 0, cached = 0, output = 0, previousInput = 0, previousCached = 0, previousOutput = 0;
            foreach (var line in File.ReadLines(path))
            {
                using var doc = JsonDocument.Parse(line);
                var total = doc.RootElement.GetProperty("payload").GetProperty("info").GetProperty("total_token_usage");
                var currentInput = total.GetProperty("input_tokens").GetInt64();
                var currentCached = total.GetProperty("cached_input_tokens").GetInt64();
                var currentOutput = total.GetProperty("output_tokens").GetInt64();
                input += Math.Max(0, currentInput - previousInput);
                cached += Math.Max(0, currentCached - previousCached);
                output += Math.Max(0, currentOutput - previousOutput);
                previousInput = currentInput;
                previousCached = currentCached;
                previousOutput = currentOutput;
            }
            return new ReferenceResult(input, cached, output);
        }
    }
}
