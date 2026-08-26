using UsageTray.Core;
using UsageTray.Providers.Codex;

namespace UsageTray.Tests;

public sealed class CodexJsonlParserTests
{
    [Fact]
    public void CumulativeTokenEventsAreConvertedToDeltasAndBadLinesAreSkipped()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("session.jsonl");
        File.WriteAllText(path, string.Join(Environment.NewLine, [
            "{\"timestamp\":\"2026-08-18T15:30:00Z\",\"type\":\"event_msg\",\"model\":\"gpt-5-test\",\"cwd\":\"C:\\\\Work\\\\Demo\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":1000,\"cached_input_tokens\":400,\"output_tokens\":100}}}}",
            "{\"timestamp\":\"2026-08-18T15:31:00Z\",\"type\":\"event_msg\",\"model\":\"gpt-5-test\",\"cwd\":\"C:\\\\Work\\\\Demo\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":1600,\"cached_input_tokens\":700,\"output_tokens\":250}}}}",
            "not-json"
        ]));

        var result = new CodexJsonlParser().ParseFile(path);

        var bucket = Assert.Single(result.Buckets);
        Assert.Equal(1600, bucket.InputTokens);
        Assert.Equal(700, bucket.CachedInputTokens);
        Assert.Equal(250, bucket.OutputTokens);
        Assert.Equal("Demo", Path.GetFileName(bucket.ProjectKey));
        Assert.Equal(1, result.WarningCount);
        Assert.True(result.HasTokenData);
    }

    [Fact]
    public void SubTaskSessionMetaExtractsOwnIdRatherThanParentSessionId()
    {
        using var workspace = new TempWorkspace();
        var mainSessionId = "01a02db9-f26f-7bf3-a35b-32d0e7fec487";
        var subTaskId = "01a02dbf-9809-7f50-a977-f53276f58acf";

        var path = workspace.File($"rollout-{subTaskId}.jsonl");
        File.WriteAllText(path, string.Join(Environment.NewLine, [
            $"{{\"timestamp\":\"2026-08-23T08:32:06.456Z\",\"type\":\"session_meta\",\"payload\":{{\"session_id\":\"{mainSessionId}\",\"id\":\"{subTaskId}\",\"parent_thread_id\":\"{mainSessionId}\"}}}}",
            "{\"timestamp\":\"2026-08-23T08:32:06.456Z\",\"type\":\"event_msg\",\"model\":\"codex-auto-review\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":50000,\"cached_input_tokens\":40000,\"output_tokens\":200}}}}"
        ]));

        var result = new CodexJsonlParser().ParseFile(path);
        Assert.Equal(subTaskId, result.SessionId);
        Assert.NotEqual(mainSessionId, result.SessionId);
    }

    [Fact]
    public void InterleavedMainAndSubTaskSessionsDoNotInflateTokens()
    {
        using var workspace = new TempWorkspace();
        var mainSessionId = "01a02db9-f26f-7bf3-a35b-32d0e7fec487";
        var subTaskId = "01a02dbf-9809-7f50-a977-f53276f58acf";

        var parser = new CodexJsonlParser();
        var mainPath = workspace.File($"rollout-{mainSessionId}.jsonl");
        var subPath = workspace.File($"rollout-{subTaskId}.jsonl");

        File.WriteAllText(mainPath, string.Join(Environment.NewLine, [
            $"{{\"timestamp\":\"2026-08-23T08:27:36.794Z\",\"type\":\"session_meta\",\"payload\":{{\"session_id\":\"{mainSessionId}\",\"id\":\"{mainSessionId}\"}}}}",
            "{\"timestamp\":\"2026-08-23T15:20:00Z\",\"type\":\"event_msg\",\"model\":\"gpt-5.6-luna\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":100000000,\"cached_input_tokens\":97000000,\"output_tokens\":400000}}}}",
            "{\"timestamp\":\"2026-08-23T15:22:00Z\",\"type\":\"event_msg\",\"model\":\"gpt-5.6-luna\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":100200000,\"cached_input_tokens\":97150000,\"output_tokens\":401000}}}}"
        ]));

        File.WriteAllText(subPath, string.Join(Environment.NewLine, [
            $"{{\"timestamp\":\"2026-08-23T08:32:06.456Z\",\"type\":\"session_meta\",\"payload\":{{\"session_id\":\"{mainSessionId}\",\"id\":\"{subTaskId}\",\"parent_thread_id\":\"{mainSessionId}\"}}}}",
            "{\"timestamp\":\"2026-08-23T15:21:00Z\",\"type\":\"event_msg\",\"model\":\"codex-auto-review\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":2000000,\"cached_input_tokens\":1800000,\"output_tokens\":10000}}}}"
        ]));

        var mainResult = parser.ParseFile(mainPath);
        var subResult = parser.ParseFile(subPath);

        var allSnapshots = (mainResult.Snapshots ?? []).Concat(subResult.Snapshots ?? []).ToList();
        var normalizer = new CodexUsageNormalizer();
        var normalized = normalizer.Normalize(allSnapshots);

        var lunaBucket = Assert.Single(normalized.Buckets, b => b.ModelId == "gpt-5.6-luna");
        var reviewBucket = Assert.Single(normalized.Buckets, b => b.ModelId == "codex-auto-review");

        // Luna total input should be exactly 100,200,000, not 100,200,000 + 98,200,000!
        Assert.Equal(100_200_000, lunaBucket.InputTokens);
        Assert.Equal(97_150_000, lunaBucket.CachedInputTokens);
        Assert.Equal(401_000, lunaBucket.OutputTokens);

        Assert.Equal(2_000_000, reviewBucket.InputTokens);
        Assert.Equal(1_800_000, reviewBucket.CachedInputTokens);
        Assert.Equal(10_000, reviewBucket.OutputTokens);
    }
}
