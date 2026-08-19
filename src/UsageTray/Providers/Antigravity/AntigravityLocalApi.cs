using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace UsageTray.Providers.Antigravity;

public sealed class AntigravityLocalApi : IDisposable
{
    private readonly HttpClient _client;
    private readonly AntigravityQuotaParser _parser;

    public AntigravityLocalApi(AntigravityQuotaParser? parser = null)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = static (request, _, _, _) =>
                request?.RequestUri is { } uri && AntigravityPortDiscovery.IsLoopback(uri)
        };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
        _parser = parser ?? new AntigravityQuotaParser();
    }

    public Task<AntigravityQuotaResult?> TryGetQuotaAsync(IEnumerable<int> ports, CancellationToken cancellationToken) =>
        TryGetQuotaAsync(ports, null, cancellationToken);

    public async Task<AntigravityQuotaResult?> TryGetQuotaAsync(IEnumerable<int> ports, string? csrfToken, CancellationToken cancellationToken)
    {
        var endpoints = new[]
        {
            "exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary",
            "exa.language_server_pb.LanguageServerService/GetUserStatus",
            "api/retrieveUserQuotaSummary",
            "api/getUserStatus"
        };
        var metadata = new
        {
            metadata = new
            {
                ideName = "antigravity",
                extensionName = "antigravity",
                ideVersion = "unknown",
                locale = "en"
            }
        };
        foreach (var port in ports.Distinct())
        {
            foreach (var path in endpoints)
            {
                var uri = AntigravityPortDiscovery.BuildEndpoint(port, path);
                if (!AntigravityPortDiscovery.IsLoopback(uri)) continue;
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, uri)
                    {
                        Content = JsonContent.Create(metadata)
                    };
                    request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
                    if (!string.IsNullOrWhiteSpace(csrfToken))
                        request.Headers.TryAddWithoutValidation("X-Codeium-Csrf-Token", csrfToken);
                    using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Moved or HttpStatusCode.RedirectMethod) continue;
                    if (!response.IsSuccessStatusCode) continue;
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                    var parsed = _parser.Parse(document.RootElement, DateTimeOffset.UtcNow, "antigravity-local", uri.ToString());
                    if (parsed.Snapshots.Count > 0 || parsed.Warnings.Count > 0) return parsed;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }
                catch (JsonException) { }
            }
        }
        return null;
    }

    public void Dispose() => _client.Dispose();
}
