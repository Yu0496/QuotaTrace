namespace UsageTray.Core;

public sealed record TokenUsage(
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long CacheWriteInputTokens = 0)
{
    public long DisplayedTotalTokens => Math.Max(0, InputTokens) + Math.Max(0, OutputTokens);

    public bool IsEmpty => InputTokens <= 0 && OutputTokens <= 0 && CacheWriteInputTokens <= 0;

    public TokenUsage NonNegative() => new(
        Math.Max(0, InputTokens),
        Math.Max(0, CachedInputTokens),
        Math.Max(0, OutputTokens),
        Math.Max(0, CacheWriteInputTokens));
}
