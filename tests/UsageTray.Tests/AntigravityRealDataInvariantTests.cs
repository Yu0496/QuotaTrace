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

        foreach (var file in files)
        {
            var parseResult = parser.ParseFile(file, resolver);
            foreach (var gen in parseResult.Generations)
            {
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

        _output.WriteLine($"Scanned {files.Count} databases, {totalGenerations} generations.");
        _output.WriteLine($"Token Totals: UncachedInput={totalUncachedInput:N0}, CacheRead={totalCacheRead:N0}, CacheWrite={totalCacheWrite:N0}, Thinking={totalThinking:N0}, Response={totalResponse:N0}, AggregateOutput={totalAggregateOutput:N0}");
        _output.WriteLine($"Invariant Check: aggregate == thinking + response. Errors: {totalInvariantErrors}");

        Assert.Equal(0, totalInvariantErrors);
    }
}
