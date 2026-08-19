namespace UsageTray.Core;

public sealed record UsageBucket(
    ProviderKind Provider,
    DateOnly LocalDate,
    string? ProjectKey,
    string? ModelId,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    int RequestCount,
    DataQuality Quality,
    string SourcePath,
    string? ConversationId = null,
    long CacheWriteInputTokens = 0,
    CostQuality CostQuality = CostQuality.ExactTokensNoCache)
{
    // 本地来源保留原始的总 input；以下三个属性转换成 Sub2API token 模式：
    // input = 未命中缓存输入，cache_read = 缓存读取，cache_creation = 缓存创建。
    public long CacheReadTokens => Math.Min(Math.Max(0, InputTokens), Math.Max(0, CachedInputTokens));

    public long CacheCreationTokens => Math.Min(
        Math.Max(0, Math.Max(0, InputTokens) - CacheReadTokens),
        Math.Max(0, CacheWriteInputTokens));

    public long NonCachedInputTokens => Math.Max(0, Math.Max(0, InputTokens) - CacheReadTokens - CacheCreationTokens);

    public long TotalInputTokens => NonCachedInputTokens + CacheReadTokens + CacheCreationTokens;

    public long DisplayedTotalTokens => TotalInputTokens + Math.Max(0, OutputTokens);

    public TokenUsage Usage => new(InputTokens, CachedInputTokens, OutputTokens, CacheWriteInputTokens);
}
