using System.Text.Json;
using UsageTray.App;
using UsageTray.Core;
using UsageTray.Providers.Antigravity;

namespace UsageTray.Tests;

public sealed class AntigravityStatusRecorderTests
{
    [Fact]
    public void RepeatedStatusLineStateIsDeduplicatedAndTotalsAreDifferenced()
    {
        using var workspace = new TempWorkspace();
        var (database, repository) = RepositoryFactory.Create(workspace);
        using (database)
        {
            var settings = new AppSettings { StatusLineCacheSemanticsValidated = true };
            var recorder = new AntigravityStatusRecorder(repository, settings);
            using var first = JsonDocument.Parse(Payload(100, 20, 30, 5));
            using var duplicate = JsonDocument.Parse(Payload(100, 20, 30, 5));
            using var second = JsonDocument.Parse(Payload(150, 35, 40, 6));
            using var rewind = JsonDocument.Parse(Payload(10, 2, 1, 0));

            Assert.True(recorder.Record(first.RootElement, DateTimeOffset.Parse("2026-08-19T10:00:00Z")).Recorded);
            Assert.False(recorder.Record(duplicate.RootElement, DateTimeOffset.Parse("2026-08-19T10:00:01Z")).Recorded);
            var result = recorder.Record(second.RootElement, DateTimeOffset.Parse("2026-08-19T10:00:02Z"));
            Assert.True(result.Recorded);
            Assert.Equal(50, result.Event!.Bucket.InputTokens);
            Assert.Equal(15, result.Event.Bucket.OutputTokens);
            Assert.Equal(40, result.Event.Bucket.CachedInputTokens);
            Assert.False(recorder.Record(rewind.RootElement, DateTimeOffset.Parse("2026-08-19T10:00:03Z")).Recorded);
            var usage = Assert.Single(repository.GetUsage(new DateRange(new DateOnly(2026, 8, 19), new DateOnly(2026, 8, 19)), ProviderKind.Antigravity));
            Assert.Equal(150, usage.InputTokens);
            Assert.Equal(35, usage.OutputTokens);
        }
    }

    private static string Payload(long input, long output, long cacheRead, long cacheWrite) => $$"""
    {
      "conversation_id": "conversation-fixture",
      "cwd": "C:\\Work\\AntigravityDemo",
      "model": { "id": "gemini-2.5-flash" },
      "context_window": {
        "total_input_tokens": {{input}},
        "total_output_tokens": {{output}},
        "current_usage": {
          "input_tokens": {{input}},
          "output_tokens": {{output}},
          "cache_read_input_tokens": {{cacheRead}},
          "cache_creation_input_tokens": {{cacheWrite}}
        }
      }
    }
    """;
}
