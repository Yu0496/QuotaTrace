using System.Net;
using System.Net.NetworkInformation;

namespace UsageTray.Providers.Antigravity;

public sealed class AntigravityPortDiscovery
{
    public IReadOnlyList<int> DiscoverCandidatePorts(IEnumerable<AntigravityProcessInfo> processes)
    {
        var fromFlags = processes
            .SelectMany(process => ParsePorts(process.CommandLine))
            .Where(IsValidPort);
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Where(endpoint => IPAddress.IsLoopback(endpoint.Address))
            .Select(endpoint => endpoint.Port)
            .Where(IsValidPort);
        return fromFlags.Concat(listeners).Distinct().OrderBy(port => port).Take(128).ToList();
    }

    public static bool IsValidPort(int port) => port is >= 1 and <= 65535;

    public static bool IsLoopback(Uri uri) =>
        uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("[::1]", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase);

    public static Uri BuildEndpoint(int port, string path) => new($"https://127.0.0.1:{port}/{path.TrimStart('/')}");

    private static IEnumerable<int> ParsePorts(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) yield break;
        foreach (var token in commandLine.Split([' ', '\t', '"', '\''], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("--port=", StringComparison.OrdinalIgnoreCase) && int.TryParse(token[7..], out var port)) yield return port;
            else if (token.StartsWith("--port", StringComparison.OrdinalIgnoreCase) && int.TryParse(token[6..], out port)) yield return port;
            else if (token.StartsWith("--extension_server_port=", StringComparison.OrdinalIgnoreCase) && int.TryParse(token[24..], out port)) yield return port;
        }
    }
}
