using System;
using System.IO;
using System.Linq;
using UsageTray.Core;
using UsageTray.Providers.Antigravity;
using Xunit;
using Xunit.Abstractions;

namespace UsageTray.Tests;

public sealed class AntigravityRealDataInvariantTests
{
    private readonly ITestOutputHelper _output;

    public AntigravityRealDataInvariantTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void RealMachineDatabasesInvariantHoldsZeroErrors()
    {
        var locator = new AntigravityHistoryLocator();
        var files = locator.DiscoverConversationDatabaseFiles();
        if (files.Count == 0)
        {
            _output.WriteLine("No real machine databases found, skipping.");
            return;
        }

        var parser = new AntigravitySqliteHistoryParser();
        var resolver = new AntigravityProjectResolver();
        resolver.LoadSummariesFromRoots(locator.GetCandidateAppRoots());

        long totalInvariantErrors = 0;
        long totalGenerations = 0;
        long totalUncachedInput = 0;
        long totalCacheRead = 0;
        long totalCacheWrite = 0;
        long totalThinking = 0;
        long totalResponse = 0;
        long totalAggregateOutput = 0;

        var allModels = new HashSet<(string Model, string? DisplayName)>();

        foreach (var file in files)
        {
            var parseResult = parser.ParseFile(file, resolver);
            foreach (var gen in parseResult.Generations)
            {
                allModels.Add((gen.Model, gen.DisplayName));
                totalGenerations++;
                totalUncachedInput += gen.InputTokens;
                totalCacheRead += gen.CacheReadTokens;
                totalCacheWrite += gen.CacheWriteTokens;
                totalThinking += gen.ThinkingOutputTokens;
                totalResponse += gen.ResponseOutputTokens;
                totalAggregateOutput += gen.OutputTokens;

                if (!gen.InvariantHolds)
                {
                    totalInvariantErrors++;
                    _output.WriteLine($"Invariant violation in {file}: aggregate={gen.OutputTokens}, thinking={gen.ThinkingOutputTokens}, response={gen.ResponseOutputTokens}");
                }
            }
        }

        var pricingService = new UsageTray.Pricing.PricingService("dummy", UsageTray.Pricing.PricingService.BuiltInDefaults());
        long unpricedGenerations = 0;
        decimal totalEstimatedCostUsd = 0;

        foreach (var file in files)
        {
            var parseResult = parser.ParseFile(file, resolver);
            foreach (var bucket in parseResult.Buckets)
            {
                var calc = pricingService.Calculate(bucket);
                if (calc.CostUsd.HasValue)
                {
                    totalEstimatedCostUsd += calc.CostUsd.Value;
                }
                else if (bucket.DisplayedTotalTokens > 0)
                {
                    unpricedGenerations += bucket.RequestCount;
                    _output.WriteLine($"Unpriced bucket in {file}: Model='{bucket.ModelId}', Requests={bucket.RequestCount}, Tokens={bucket.DisplayedTotalTokens}");
                }

            }
        }

        _output.WriteLine($"Scanned {files.Count} databases, {totalGenerations} generations.");
        foreach (var m in allModels)
        {
            var testBucket = new UsageBucket(ProviderKind.Antigravity, DateOnly.FromDateTime(DateTime.Today), null, m.Model, 1000, 500, 500, 1, DataQuality.Exact, "fixture");
            var rule = UsageTray.Pricing.PricingMatcher.Find(pricingService.Rules, ProviderKind.Antigravity, m.Model);
            _output.WriteLine($"Model: '{m.Model}' (DisplayName: '{m.DisplayName}') -> Matched Pattern: '{rule?.ModelPattern}', RuleFound={rule != null}");
        }
        _output.WriteLine($"Token Totals: UncachedInput={totalUncachedInput:N0}, CacheRead={totalCacheRead:N0}, CacheWrite={totalCacheWrite:N0}, Thinking={totalThinking:N0}, Response={totalResponse:N0}, AggregateOutput={totalAggregateOutput:N0}");
        _output.WriteLine($"Total Estimated Cost across real data: ${totalEstimatedCostUsd:N2}, Unpriced Generations: {unpricedGenerations}");
        _output.WriteLine($"Invariant Check: aggregate == thinking + response. Errors: {totalInvariantErrors}");

        Assert.Equal(0, totalInvariantErrors);
        // Unverified model aliases may remain unpriced; token invariants must still hold.
    }

    [Fact]
    public async Task LiveLanguageServerQuotaDiscoveryReturnsValidRealSnapshots()
    {
        var procDiscovery = new AntigravityProcessDiscovery();
        var procs = procDiscovery.Discover();
        _output.WriteLine($"Discovered {procs.Count} processes.");
        foreach (var p in procs)
        {
            _output.WriteLine($"Proc: {p.Name} (PID={p.ProcessId}), HasCSRF={!string.IsNullOrWhiteSpace(p.CsrfToken)}");
        }

        var portDiscovery = new AntigravityPortDiscovery();
        var ports = portDiscovery.DiscoverCandidatePorts(procs);
        _output.WriteLine($"Candidate Ports ({ports.Count}): {string.Join(", ", ports.Take(10))}");

        var csrfToken = procs.Select(p => p.CsrfToken).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        using var api = new AntigravityLocalApi();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var quota = await api.TryGetQuotaAsync(ports, csrfToken, cts.Token);

        if (procs.Any(p => p.Name.Contains("language", StringComparison.OrdinalIgnoreCase)))
        {
            Assert.NotNull(quota);
            Assert.NotEmpty(quota.Snapshots);
            _output.WriteLine($"Successfully fetched live quota from {quota.Endpoint} with {quota.Snapshots.Count} snapshots:");
            foreach (var snap in quota.Snapshots)
            {
                _output.WriteLine($"  - [{snap.WindowKind}] {snap.DisplayLabel} (ID={snap.ModelOrPoolId}): Remaining={snap.RemainingFraction:P1}, Reset={snap.ResetAt:yyyy-MM-dd HH:mm:ss}");
            }
        }
    }
}



