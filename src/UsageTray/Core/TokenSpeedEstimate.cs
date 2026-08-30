using System;
using System.Globalization;

namespace UsageTray.Core;

/// <summary>
/// 表示基于会话记录反推的 Token 处理速率预估模型。
/// </summary>
public sealed record TokenSpeedEstimate(
    double? UncachedPrefillTokensPerSecond,
    double? CacheReadTokensPerSecond,
    double? OutputTokensPerSecond,
    int SampleCount,
    int UncachedPrefillSampleCount,
    int CacheReadSampleCount,
    int OutputSampleCount
)
{
    public static readonly TokenSpeedEstimate Empty = new(null, null, null, 0, 0, 0, 0);

    public bool HasData => SampleCount > 0 && (UncachedPrefillTokensPerSecond.HasValue || OutputTokensPerSecond.HasValue || CacheReadTokensPerSecond.HasValue);

    public string ToShortDisplayString()
    {
        if (!HasData) return "-";

        var parts = new System.Collections.Generic.List<string>();

        if (UncachedPrefillTokensPerSecond.HasValue && UncachedPrefillTokensPerSecond.Value > 0)
        {
            parts.Add($"未命中 ~{FormatRate(UncachedPrefillTokensPerSecond.Value)}/s");
        }

        if (CacheReadTokensPerSecond.HasValue && CacheReadTokensPerSecond.Value > 0)
        {
            parts.Add($"命中 ~{FormatRate(CacheReadTokensPerSecond.Value)}/s");
        }

        if (OutputTokensPerSecond.HasValue && OutputTokensPerSecond.Value > 0)
        {
            parts.Add($"输出 ~{FormatRate(OutputTokensPerSecond.Value)}/s");
        }

        return parts.Count > 0 ? string.Join(" · ", parts) : "-";
    }

    public string ToDetailedTooltip()
    {
        if (!HasData)
        {
            return "预估速率：暂无足够的时间戳样本进行反推。\r\n\r\n说明：仅当本地会话存在连续 Turn 时间戳记录时计算。详情见“设置”。";
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("预估速率详情（基于会话各轮耗时反推）：");

        if (UncachedPrefillTokensPerSecond.HasValue && UncachedPrefillTokensPerSecond.Value > 0)
        {
            sb.AppendLine($"• 未命中 Prefill 速度：约 {FormatRate(UncachedPrefillTokensPerSecond.Value)} tokens/s（冷启动计算，样本: {UncachedPrefillSampleCount}次）");
        }
        else
        {
            sb.AppendLine("• 未命中 Prefill 速度：样本不足");
        }

        if (CacheReadTokensPerSecond.HasValue && CacheReadTokensPerSecond.Value > 0)
        {
            sb.AppendLine($"• 命中 Prefill 速度：约 {FormatRate(CacheReadTokensPerSecond.Value)} tokens/s（KV 命中检索，样本: {CacheReadSampleCount}次）");
        }
        else
        {
            sb.AppendLine("• 命中 Prefill 速度：样本不足");
        }

        if (OutputTokensPerSecond.HasValue && OutputTokensPerSecond.Value > 0)
        {
            sb.AppendLine($"• 输出生成速度：约 {FormatRate(OutputTokensPerSecond.Value)} tokens/s（逐字解码/Thinking，样本: {OutputSampleCount}次）");
        }
        else
        {
            sb.AppendLine("• 输出生成速度：样本不足");
        }

        sb.AppendLine($"• 参与统计总样本数：{SampleCount} 次请求");
        sb.AppendLine();
        sb.Append("提示：该数值包含网络 RTT 与云端排队延迟，通常低于官方纯硬件速度；详情见“设置”。");

        return sb.ToString();
    }

    public static string FormatRate(double rate)
    {
        if (rate >= 1_000_000)
            return (rate / 1_000_000.0).ToString("0.0M", CultureInfo.InvariantCulture);
        if (rate >= 1_000)
            return (rate / 1_000.0).ToString("0.0k", CultureInfo.InvariantCulture);
        return rate.ToString("0.0", CultureInfo.InvariantCulture);
    }
}
