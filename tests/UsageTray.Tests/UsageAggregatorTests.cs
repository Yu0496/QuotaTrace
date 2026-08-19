using UsageTray.Core;
using UsageTray.Pricing;
using UsageTray.Services;

namespace UsageTray.Tests;

public sealed class UsageAggregatorTests
{
    [Fact]
    public void UnpricedModelDoesNotBecomeZeroCostOrDisappearFromTokenTotals()
    {
        using var workspace = new TempWorkspace();
        var (database, repository) = RepositoryFactory.Create(workspace);
        using (database)
        {
            var date = new DateOnly(2026, 8, 19);
            var infoPath = workspace.File("fixture.jsonl");
            File.WriteAllText(infoPath, "fixture");
            var info = new FileInfo(infoPath);
            repository.ReplaceFileUsage(ProviderKind.Codex, info.FullName, info, [
                new UsageBucket(ProviderKind.Codex, date, null, "unknown-model", 1000, 0, 500, 1, DataQuality.Exact, info.FullName)
            ], "fixture", null, "unknown-model");
            var pricing = new PricingService(workspace.File("pricing.json"), new PricingDocument(1, date, []));
            var snapshot = new UsageAggregator(repository, pricing).BuildSnapshot(new DateRange(date, date));

            Assert.Null(snapshot.ApiEquivalentUsd);
            Assert.Equal(1500, snapshot.InputTokens + snapshot.OutputTokens);
            Assert.Equal(1500, snapshot.UnpricedTokens);
        }
    }
}
