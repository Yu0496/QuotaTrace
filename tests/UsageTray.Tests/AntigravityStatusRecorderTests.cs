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

    [Fact]
    public void StatusLineQuotaIsRecordedWithoutConversationIdFilteredAndThrottled()
    {
        using var workspace = new TempWorkspace();
        var (database, repository) = RepositoryFactory.Create(workspace);
        using (database)
        {
            repository.SetFlag("antigravity_history_tokens", "1");
            var settings = new AppSettings
            {
                StatusLineCacheSemanticsValidated = true
            };
            var recorder = new AntigravityStatusRecorder(repository, settings);
            var t0 = DateTimeOffset.Parse("2026-11-23T08:00:00Z");

            using var doc1 = JsonDocument.Parse(QuotaPayload(0.80, 65, 360000));
            var r1 = recorder.Record(doc1.RootElement, t0);
            Assert.True(r1.Recorded);
            Assert.Null(r1.Event);
            Assert.Equal(2, r1.QuotasRecorded);

            var latest1 = repository.GetLatestQuotas(ProviderKind.Antigravity);
            Assert.Equal(2, latest1.Count);
            Assert.All(latest1, q => Assert.Equal(QuotaSources.AntigravityStatusLine, q.Source));
            Assert.DoesNotContain(latest1, q => q.ModelOrPoolId.Contains("argon", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(latest1, q => q.ModelOrPoolId.Contains("5h", StringComparison.OrdinalIgnoreCase));

            // Identical snapshot within 5 minutes is throttled (reset_in_seconds drift 10s <= 120s).
            using var doc2 = JsonDocument.Parse(QuotaPayload(0.80, 65, 360000));
            var r2 = recorder.Record(doc2.RootElement, t0.AddSeconds(10));
            Assert.False(r2.Recorded);
            Assert.Equal(0, r2.QuotasRecorded);

            // Remaining fraction drop >= 0.1% triggers immediate write for the changed pool.
            using var doc3 = JsonDocument.Parse(QuotaPayload(0.78, 65, 360000));
            var r3 = recorder.Record(doc3.RootElement, t0.AddSeconds(20));
            Assert.Equal(1, r3.QuotasRecorded);

            // After 5 minutes, periodic refresh snapshot is written even if percentages are unchanged.
            using var doc4 = JsonDocument.Parse(QuotaPayload(0.78, 65, 360000));
            var r4 = recorder.Record(doc4.RootElement, t0.AddMinutes(6));
            Assert.Equal(2, r4.QuotasRecorded);
        }
    }

    [Fact]
    public void StatusLineNestedGroupsShapeIsParsedAndOverridesManualCalibrationImmediately()
    {
        using var workspace = new TempWorkspace();
        var (database, repository) = RepositoryFactory.Create(workspace);
        using (database)
        {
            var t0 = DateTimeOffset.Parse("2026-11-23T08:00:00Z");
            var resetAt = DateTimeOffset.Parse("2026-11-30T08:00:00Z");
            repository.AddQuotaSnapshots([
                AntigravityManualQuotaHelper.CreateManualWeeklySnapshot("gemini", 80, resetAt, t0)
            ]);

            var recorder = new AntigravityStatusRecorder(repository, new AppSettings { StatusLineCacheSemanticsValidated = true });
            const string json = """
            {
              "quota": {
                "groups": [
                  {
                    "id": "gemini",
                    "label": "Gemini Models",
                    "remaining_fraction": 0.80,
                    "reset_time": "2026-11-30T08:00:00Z",
                    "window": "weekly"
                  }
                ]
              }
            }
            """;
            using var doc = JsonDocument.Parse(json);
            var result = recorder.Record(doc.RootElement, t0.AddSeconds(15));
            Assert.Equal(1, result.QuotasRecorded);

            var latest = Assert.Single(repository.GetLatestQuotas(ProviderKind.Antigravity));
            Assert.Equal("gemini-weekly", latest.ModelOrPoolId);
            Assert.Equal(QuotaSources.AntigravityStatusLine, latest.Source);
            Assert.Equal(0.80, latest.RemainingFraction!.Value, 4);
        }
    }

    private static string QuotaPayload(double geminiRemainingFraction, double thirdPartyPercent, int resetInSeconds) => $$"""
    {
      "cwd": "C:\\Work\\AntigravityDemo",
      "model": { "id": "gemini-3-pro" },
      "quota": {
        "gemini-weekly": {
          "label": "Gemini (Weekly)",
          "remaining_fraction": {{geminiRemainingFraction.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
          "reset_time": "2026-11-30T08:00:00Z",
          "window": "weekly"
        },
        "3p-weekly": {
          "label": "Claude / GPT (Weekly)",
          "remaining_percentage": {{thirdPartyPercent.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
          "reset_in_seconds": {{resetInSeconds}}
        },
        "argon-weekly": {
          "label": "Argon",
          "remaining_fraction": 0.90,
          "reset_time": "2026-11-30T08:00:00Z"
        },
        "gemini-5h": {
          "label": "Gemini 5h",
          "remaining_fraction": 0.50,
          "reset_time": "2026-11-24T15:00:00Z",
          "disabled": true
        }
      }
    }
    """;

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
