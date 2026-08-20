using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using UsageTray.Core;

namespace UsageTray.Pricing;

public sealed record PricingUpdateResult(
    PricingDocument Document,
    int UpdatedCount,
    DateTimeOffset FetchedAt,
    IReadOnlyList<string> Warnings)
{
    public bool HasChanges => UpdatedCount > 0;
}

public sealed class PricingUpdateService
{
    private static readonly Uri GeminiPricingUri = new("https://ai.google.dev/gemini-api/docs/pricing");
    private static readonly IReadOnlyDictionary<string, Uri> OpenAiModelPages = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-5.6"] = new("https://developers.openai.com/api/docs/models/gpt-5.6-sol"),
        ["gpt-5.6-sol*"] = new("https://developers.openai.com/api/docs/models/gpt-5.6-sol"),
        ["gpt-5.6-terra*"] = new("https://developers.openai.com/api/docs/models/gpt-5.6-terra"),
        ["gpt-5.6-luna*"] = new("https://developers.openai.com/api/docs/models/gpt-5.6-luna"),
        ["gpt-5.5*"] = new("https://developers.openai.com/api/docs/models/gpt-5.5"),
        ["gpt-5.4-mini*"] = new("https://developers.openai.com/api/docs/models/gpt-5.4-mini"),
        ["gpt-5.4-nano*"] = new("https://developers.openai.com/api/docs/models/gpt-5.4-nano"),
        ["gpt-5.4*"] = new("https://developers.openai.com/api/docs/models/gpt-5.4")
    };

    private static readonly string[] GeminiModels =
    [
        "gemini-3.7-flash", "gemini-3.6-flash", "gemini-3.6-flash-tiered", "gemini-3.5-flash-lite", "gemini-3.5-flash",
        "gemini-3-flash-a", "gemini-default", "gemini-3.1-flash-lite", "gemini-3.1-pro", "gemini-pro-default", "gemini-pro",
        "gemini-2.5-pro", "gemini-2.5-flash"
    ];

    private static string CanonicalGeminiModel(string model) => model.ToLowerInvariant() switch
    {
        "gemini-3-flash-a" or "gemini-default" => "gemini-3.5-flash",
        "gemini-pro-default" or "gemini-pro" => "gemini-3.1-pro",
        "gemini-3.6-flash-tiered" => "gemini-3.6-flash",
        _ => model
    };

    private readonly HttpClient _httpClient;

    public PricingUpdateService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? CreateHttpClient();
    }

    public async Task<PricingUpdateResult> FetchLatestAsync(PricingDocument current, CancellationToken cancellationToken = default)
    {
        var fetchedAt = DateTimeOffset.UtcNow;
        var verifiedDate = DateOnly.FromDateTime(fetchedAt.UtcDateTime.Date);
        var warnings = new List<string>();
        var rules = current.Rules.ToList();
        var pageCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var updatedCount = 0;
        var supportedRuleCount = 0;

        foreach (var page in OpenAiModelPages)
        {
            var indexes = rules
                .Select((rule, index) => (rule, index))
                .Where(pair => string.Equals(pair.rule.Provider, ProviderKind.Codex.ToStorageString(), StringComparison.OrdinalIgnoreCase) &&
                               string.Equals(pair.rule.ModelPattern, page.Key, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.index)
                .ToList();
            if (indexes.Count == 0) continue;
            supportedRuleCount += indexes.Count;

            var content = await GetPageAsync(page.Value, pageCache, warnings, cancellationToken);
            if (content is null || !TryParseOpenAi(content, out var prices)) continue;
            foreach (var index in indexes)
            {
                var old = rules[index];
                decimal? cacheWrite = old.CacheWritePerMillionUsd.HasValue ? prices.InputPerMillionUsd * 1.25m : null;
                rules[index] = old with
                {
                    InputPerMillionUsd = prices.InputPerMillionUsd,
                    CacheReadPerMillionUsd = prices.CacheReadPerMillionUsd,
                    CacheWritePerMillionUsd = cacheWrite,
                    OutputPerMillionUsd = prices.OutputPerMillionUsd,
                    LastVerifiedAt = verifiedDate
                };
                updatedCount++;
            }
        }

        var geminiIndexes = rules
            .Select((rule, index) => (rule, index))
            .Where(pair => string.Equals(pair.rule.Provider, ProviderKind.Antigravity.ToStorageString(), StringComparison.OrdinalIgnoreCase))
            .Where(pair => GeminiModels.Any(model => string.Equals(pair.rule.ModelPattern.TrimEnd('*'), model, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        supportedRuleCount += geminiIndexes.Count;
        if (geminiIndexes.Count > 0)
        {
            var content = await GetPageAsync(GeminiPricingUri, pageCache, warnings, cancellationToken);
            if (content is not null)
            {
                foreach (var pair in geminiIndexes)
                {
                    var model = CanonicalGeminiModel(pair.rule.ModelPattern.TrimEnd('*'));
                    if (!TryParseGemini(content, model, out var prices)) continue;
                    var old = rules[pair.index];
                    rules[pair.index] = old with
                    {
                        InputPerMillionUsd = prices.InputPerMillionUsd,
                        CacheReadPerMillionUsd = prices.CacheReadPerMillionUsd ?? old.CacheReadPerMillionUsd,
                        OutputPerMillionUsd = prices.OutputPerMillionUsd,
                        LastVerifiedAt = verifiedDate
                    };
                    updatedCount++;
                }
            }
        }

        if (updatedCount == 0 && warnings.Count == 0)
            warnings.Add("官方定价页未返回可识别的模型价格，已保留本地规则。");
        if (updatedCount > 0 && updatedCount < supportedRuleCount)
            warnings.Add("未能自动读取的模型规则已保留原价格；可在设置页手动编辑价格文件。");

        return new PricingUpdateResult(new PricingDocument(current.SchemaVersion, verifiedDate, rules), updatedCount, fetchedAt,
            warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private async Task<string?> GetPageAsync(Uri uri, IDictionary<string, string> pageCache, ICollection<string> warnings, CancellationToken cancellationToken)
    {
        if (pageCache.TryGetValue(uri.AbsoluteUri, out var cached)) return cached;
        if (!IsAllowedPricingHost(uri))
        {
            warnings.Add($"价格来源不是允许的官方域名：{uri.Host}");
            return null;
        }

        try
        {
            using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseContentRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                warnings.Add($"获取价格页失败：{uri.Host} HTTP {(int)response.StatusCode}");
                return null;
            }
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            pageCache[uri.AbsoluteUri] = content;
            return content;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            warnings.Add($"获取价格页超时：{uri.Host}");
            return null;
        }
        catch (HttpRequestException exception)
        {
            warnings.Add($"获取价格页失败：{uri.Host}（{exception.Message}）");
            return null;
        }
    }

    internal static bool TryParseOpenAi(string html, out PricePoint prices)
    {
        var text = NormalizeHtml(html);
        var textTokenIndex = text.IndexOf("Text tokens", StringComparison.OrdinalIgnoreCase);
        var segment = textTokenIndex >= 0 ? text[textTokenIndex..Math.Min(text.Length, textTokenIndex + 1800)] : text;
        var match = Regex.Match(segment,
            @"Per 1M tokens.*?\bInput\s+\$(?<input>[0-9]+(?:\.[0-9]+)?).*?\bCached input\s+\$(?<cached>[0-9]+(?:\.[0-9]+)?).*?\bOutput\s+\$(?<output>[0-9]+(?:\.[0-9]+)?)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success || !TryDecimal(match, "input", out var input) || !TryDecimal(match, "cached", out var cached) || !TryDecimal(match, "output", out var output))
        {
            prices = default;
            return false;
        }
        prices = new PricePoint(input, cached, output);
        return true;
    }

    internal static bool TryParseGemini(string html, string modelId, out PricePoint prices)
    {
        var text = NormalizeHtml(html);
        var start = text.IndexOf(modelId, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            prices = default;
            return false;
        }

        var end = text.Length;
        foreach (var otherModel in GeminiModels.Where(model => !string.Equals(model, modelId, StringComparison.OrdinalIgnoreCase)))
        {
            var otherStart = text.IndexOf(otherModel, start + modelId.Length, StringComparison.OrdinalIgnoreCase);
            if (otherStart > start && otherStart < end) end = otherStart;
        }
        var segment = text[start..Math.Min(end, start + 6000)];
        var input = ExtractPriceAfter(segment, "Input price");
        var output = ExtractPriceAfter(segment, "Output price");
        var cached = ExtractPriceAfter(segment, "Context caching price");
        if (!input.HasValue || !output.HasValue)
        {
            prices = default;
            return false;
        }
        prices = new PricePoint(input.Value, cached, output.Value);
        return true;
    }

    private static decimal? ExtractPriceAfter(string text, string label)
    {
        var start = text.IndexOf(label, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        var segment = text[start..Math.Min(text.Length, start + 300)];
        var match = Regex.Match(segment, @"\$(?<value>[0-9]+(?:\.[0-9]+)?)");
        return match.Success && TryDecimal(match, "value", out var value) ? value : null;
    }

    private static bool TryDecimal(Match match, string groupName, out decimal value) =>
        decimal.TryParse(match.Groups[groupName].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    private static string NormalizeHtml(string html)
    {
        var withoutScripts = Regex.Replace(html, @"<(script|style)\b[^>]*>.*?</\1>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var withoutTags = Regex.Replace(withoutScripts, "<[^>]+>", " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }

    private static bool IsAllowedPricingHost(Uri uri) => uri.Scheme == Uri.UriSchemeHttps &&
        (string.Equals(uri.Host, "developers.openai.com", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Host, "ai.google.dev", StringComparison.OrdinalIgnoreCase));

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("UsageTray", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        return client;
    }

    internal readonly record struct PricePoint(decimal InputPerMillionUsd, decimal? CacheReadPerMillionUsd, decimal OutputPerMillionUsd);
}
