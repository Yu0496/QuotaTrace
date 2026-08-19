using System.Diagnostics;
using System.Text.RegularExpressions;

namespace UsageTray.Providers.Antigravity;

public sealed class AntigravityProcessDiscovery
{
    private static readonly string[] ProcessNames =
    [
        "antigravity",
        "agy",
        "language_server",
        "language-server",
        "language_server_windows_x64",
        "language_server_x64"
    ];

    public IReadOnlyList<AntigravityProcessInfo> Discover()
    {
        var result = new List<AntigravityProcessInfo>();
        foreach (var name in ProcessNames)
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(name); } catch { continue; }
            foreach (var process in processes)
            {
                try
                {
                    string? executable = null;
                    try { executable = process.MainModule?.FileName; } catch { }
                    var shouldReadCommandLine = process.ProcessName.Contains("language", StringComparison.OrdinalIgnoreCase)
                        || process.ProcessName.Contains("agy", StringComparison.OrdinalIgnoreCase);
                    var commandLine = shouldReadCommandLine ? TryReadCommandLine(process.Id) : null;
                    result.Add(new AntigravityProcessInfo(process.ProcessName, process.Id, executable, commandLine, ExtractCsrfToken(commandLine)));
                }
                catch { }
                finally { process.Dispose(); }
            }
        }
        return result.GroupBy(p => p.ProcessId).Select(g => g.First()).ToList();
    }

    private static string? TryReadCommandLine(int processId)
    {
        try
        {
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(powershell)) return null;
            var startInfo = new ProcessStartInfo
            {
                FileName = powershell,
                Arguments = $"-NoLogo -NoProfile -NonInteractive -Command \"(Get-CimInstance Win32_Process -Filter 'ProcessId = {processId}').CommandLine\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var query = Process.Start(startInfo);
            if (query is null) return null;
            var outputTask = query.StandardOutput.ReadToEndAsync();
            if (!outputTask.Wait(TimeSpan.FromSeconds(2)))
            {
                try { query.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            query.WaitForExit(500);
            var output = outputTask.GetAwaiter().GetResult().Trim();
            return string.IsNullOrWhiteSpace(output) ? null : output;
        }
        catch { return null; }
    }

    internal static string? ExtractCsrfToken(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var match = Regex.Match(commandLine, @"--csrf[_-]token(?:=|\s+)(\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var token = match.Groups[1].Value.Trim('"', '\'');
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }
}
