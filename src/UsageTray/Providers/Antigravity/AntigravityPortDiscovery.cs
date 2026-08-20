using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace UsageTray.Providers.Antigravity;

public sealed class AntigravityPortDiscovery
{
    public IReadOnlyList<int> DiscoverCandidatePorts(IEnumerable<AntigravityProcessInfo> processes)
    {
        var processList = processes.ToList();
        var processPorts = new List<int>();

        foreach (var proc in processList)
        {
            processPorts.AddRange(DiscoverProcessPorts(proc.ProcessId));
            processPorts.AddRange(ParsePorts(proc.CommandLine));
        }

        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Where(endpoint => IPAddress.IsLoopback(endpoint.Address))
            .Select(endpoint => endpoint.Port)
            .Where(IsValidPort);

        return processPorts
            .Where(IsValidPort)
            .Concat(listeners)
            .Distinct()
            .Take(128)
            .ToList();
    }

    public static IReadOnlyList<int> DiscoverProcessPorts(int processId)
    {
        if (processId <= 0) return [];
        var ports = new List<int>();
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "netstat",
                Arguments = "-ano -p tcp",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var proc = Process.Start(startInfo);
            if (proc is null) return [];
            var text = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(1000);

            var pattern = $@"(?:TCP)\s+(?:(?:127\.0\.0\.1|0\.0\.0\.0|\[::\]|\[::1\]):(\d+))\s+.*?(?:LISTENING|LISTEN)\s+{processId}\b";
            foreach (Match match in Regex.Matches(text, pattern, RegexOptions.IgnoreCase))
            {
                if (int.TryParse(match.Groups[1].Value, out var port) && IsValidPort(port))
                {
                    ports.Add(port);
                }
            }
        }
        catch { }
        return ports.Distinct().ToList();
    }

    public static bool IsValidPort(int port) => port is >= 1 and <= 65535;

    public static bool IsLoopback(Uri uri) =>
        uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("[::1]", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase);

    public static Uri BuildEndpoint(int port, string path, string scheme = "https") =>
        new($"{scheme}://127.0.0.1:{port}/{path.TrimStart('/')}");

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

