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
}
