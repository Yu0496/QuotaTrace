using System.Net;
using System.Net.Http;
using UsageTray.Core;
using UsageTray.Pricing;

namespace UsageTray.Tests;

public sealed class PricingUpdateServiceTests
{
    [Fact]
    public void OfficialPageParsersReadOpenAiAndGeminiPriceBlocks()
    {
        var openAi = "<h2>Text tokens</h2> Per 1M tokens Input $5.00 Cached input $0.50 Output $30.00";
        var gemini = "<h2>Gemini 3.7 Flash</h2><code>gemini-3.7-flash</code> Input price $0.75 Output price $3.75 Context caching price $0.075 <h2>Gemini 3.6 Flash</h2><code>gemini-3.6-flash</code> Input price $0.75 Output price $3.75 Context caching price $0.075";

        Assert.True(PricingUpdateService.TryParseOpenAi(openAi, out var openAiPrice));
        Assert.Equal(5m, openAiPrice.InputPerMillionUsd);
        Assert.Equal(0.5m, openAiPrice.CacheReadPerMillionUsd);
        Assert.Equal(30m, openAiPrice.OutputPerMillionUsd);

        Assert.True(PricingUpdateService.TryParseGemini(gemini, "gemini-3.7-flash", out var geminiPrice));
        Assert.Equal(0.75m, geminiPrice.InputPerMillionUsd);
        Assert.Equal(0.075m, geminiPrice.CacheReadPerMillionUsd);
        Assert.Equal(3.75m, geminiPrice.OutputPerMillionUsd);
    }

    [Fact]
    public async Task FetchLatestUpdatesOnlyRecognizedOfficialRules()
    {
        var document = new PricingDocument(1, new DateOnly(2026, 8, 19),
        [
            new PricingRule("Codex", "gpt-5.6", MatchMode.Exact, 1m, 0.1m, 1.25m, 2m, "https://developers.openai.com/api/docs/models/gpt-5.6-sol", new DateOnly(2026, 8, 19)),
            new PricingRule("Antigravity", "gemini-3.7-flash*", MatchMode.Wildcard, 1m, 0.1m, null, 2m, "https://ai.google.dev/gemini-api/docs/pricing", new DateOnly(2026, 8, 19)),
            new PricingRule("Codex", "unknown-model", MatchMode.Exact, 7m, null, null, 8m, "https://platform.openai.com/pricing", new DateOnly(2026, 8, 19))
        ]);
        using var client = new HttpClient(new StubPricingHandler());
        var service = new PricingUpdateService(client);

        var result = await service.FetchLatestAsync(document);

        Assert.Equal(2, result.UpdatedCount);
        Assert.Equal(5m, result.Document.Rules[0].InputPerMillionUsd);
        Assert.Equal(6.25m, result.Document.Rules[0].CacheWritePerMillionUsd);
        Assert.Equal(0.75m, result.Document.Rules[1].InputPerMillionUsd);
        Assert.Equal(7m, result.Document.Rules[2].InputPerMillionUsd);
    }

    private sealed class StubPricingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = request.RequestUri?.Host.Equals("ai.google.dev", StringComparison.OrdinalIgnoreCase) == true
                ? "<h2>Gemini 3.7 Flash</h2><code>gemini-3.7-flash</code> Input price $0.75 Output price $3.75 Context caching price $0.075"
                : "<h2>Text tokens</h2> Per 1M tokens Input $5.00 Cached input $0.50 Output $30.00";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) });
        }
    }
}
