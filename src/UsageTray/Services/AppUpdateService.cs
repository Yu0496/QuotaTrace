using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;

namespace UsageTray.Services;

public sealed record ReleaseAssetInfo(string Name, string DownloadUrl, long Size);

public sealed record ReleaseInfo(
    string TagName,
    string VersionString,
    Version Version,
    string Title,
    string Description,
    string HtmlUrl,
    DateTimeOffset PublishedAt,
    ReleaseAssetInfo? Asset
);

public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    Version CurrentVersion,
    ReleaseInfo? LatestRelease,
    string? ErrorMessage = null
);

public static class AppUpdateService
{
    public const string GitHubRepoUrl = "https://github.com/Yu0496/QuotaTrace";
    public const string GitHubReleasesUrl = "https://github.com/Yu0496/QuotaTrace/releases";
    public const string GitHubApiLatestReleaseUrl = "https://api.github.com/repos/Yu0496/QuotaTrace/releases/latest";

    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("QuotaTrace-Updater", GetCurrentVersion().ToString()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
        return client;
    }

    public static Version GetCurrentVersion()
    {
        var ver = typeof(AppUpdateService).Assembly.GetName().Version;
        return ver != null ? new Version(ver.Major, ver.Minor, Math.Max(0, ver.Build)) : new Version(1, 4, 0);
    }

    public static string GetCurrentVersionString() => $"v{GetCurrentVersion().ToString(3)}";

    public static async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        var currentVersion = GetCurrentVersion();
        try
        {
            var response = await HttpClient.GetAsync(GitHubApiLatestReleaseUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(false, currentVersion, null, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var release = ParseReleaseJson(json);
            if (release == null)
            {
                return new UpdateCheckResult(false, currentVersion, null, "未能解析 Release 数据或版本格式无效。");
            }

            var isAvailable = release.Version > currentVersion;
            return new UpdateCheckResult(isAvailable, currentVersion, release);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, currentVersion, null, ex.Message);
        }
    }

    public static ReleaseInfo? ParseReleaseJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagElem)) return null;
            var tagName = tagElem.GetString();
            if (string.IsNullOrWhiteSpace(tagName)) return null;

            var cleanVersion = tagName.Trim().TrimStart('v', 'V');
            if (!Version.TryParse(cleanVersion, out var parsedVersion))
            {
                // 若含有 prerelease 标志（如 1.5.0-beta），取前三个字段解析
                var dashIdx = cleanVersion.IndexOf('-');
                if (dashIdx > 0 && Version.TryParse(cleanVersion[..dashIdx], out var fallbackVer))
                {
                    parsedVersion = fallbackVer;
                }
                else
                {
                    return null;
                }
            }

            var title = root.TryGetProperty("name", out var nameElem) ? nameElem.GetString() ?? tagName : tagName;
            var body = root.TryGetProperty("body", out var bodyElem) ? bodyElem.GetString() ?? string.Empty : string.Empty;
            var htmlUrl = root.TryGetProperty("html_url", out var urlElem) ? urlElem.GetString() ?? GitHubReleasesUrl : GitHubReleasesUrl;
            var publishedAt = root.TryGetProperty("published_at", out var pubElem) && pubElem.TryGetDateTimeOffset(out var dt)
                ? dt
                : DateTimeOffset.UtcNow;

            ReleaseAssetInfo? bestAsset = null;
            if (root.TryGetProperty("assets", out var assetsElem) && assetsElem.ValueKind == JsonValueKind.Array)
            {
                var candidateAssets = new List<ReleaseAssetInfo>();
                foreach (var asset in assetsElem.EnumerateArray())
                {
                    var aName = asset.TryGetProperty("name", out var an) ? an.GetString() : null;
                    var aUrl = asset.TryGetProperty("browser_download_url", out var au) ? au.GetString() : null;
                    var aSize = asset.TryGetProperty("size", out var asz) ? asz.GetInt64() : 0L;
                    if (!string.IsNullOrWhiteSpace(aName) && !string.IsNullOrWhiteSpace(aUrl))
                    {
                        candidateAssets.Add(new ReleaseAssetInfo(aName, aUrl, aSize));
                    }
                }

                // 优先选取包含 win-x64 的 zip，其次任意 zip
                bestAsset = candidateAssets.FirstOrDefault(a => a.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                         ?? candidateAssets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            }

            return new ReleaseInfo(
                tagName,
                cleanVersion,
                parsedVersion,
                title,
                body,
                htmlUrl,
                publishedAt,
                bestAsset
            );
        }
        catch
        {
            return null;
        }
    }

    public static async Task<string> DownloadAndPrepareUpdateAsync(
        ReleaseInfo release,
        IProgress<(long received, long total, int percentage)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (release.Asset == null || string.IsNullOrWhiteSpace(release.Asset.DownloadUrl))
        {
            throw new InvalidOperationException("未找到可供直接下载的安装包资产文件。");
        }

        var updateRoot = Path.Combine(Path.GetTempPath(), "QuotaTrace_Update");
        if (Directory.Exists(updateRoot))
        {
            try { Directory.Delete(updateRoot, true); } catch { }
        }
        Directory.CreateDirectory(updateRoot);

        var zipPath = Path.Combine(updateRoot, release.Asset.Name);

        // 下载文件并汇报进度
        using (var response = await HttpClient.GetAsync(release.Asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength ?? release.Asset.Size;

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                totalRead += bytesRead;
                var pct = totalBytes > 0 ? (int)Math.Clamp((double)totalRead / totalBytes * 100.0, 0, 100) : 0;
                progress?.Report((totalRead, totalBytes, pct));
            }
        }

        // 解压到 extracted 目录
        var extractDir = Path.Combine(updateRoot, "extracted");
        Directory.CreateDirectory(extractDir);
        ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

        // 寻找包含 UsageTray.exe 的实际根目录
        var sourceDir = extractDir;
        if (!File.Exists(Path.Combine(sourceDir, "UsageTray.exe")))
        {
            var subDirs = Directory.GetDirectories(extractDir);
            foreach (var sub in subDirs)
            {
                if (File.Exists(Path.Combine(sub, "UsageTray.exe")))
                {
                    sourceDir = sub;
                    break;
                }
            }
        }

        if (!File.Exists(Path.Combine(sourceDir, "UsageTray.exe")))
        {
            throw new InvalidOperationException("更新包解压后未找到 UsageTray.exe 主程序文件。");
        }

        // 创建自更新脚本
        var targetDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var scriptPath = Path.Combine(updateRoot, "apply_update.cmd");
        var pid = Environment.ProcessId;

        var scriptContent = $@"@echo off
chcp 65001 >nul
set TARGET={targetDir}
set SOURCE={sourceDir}
set PID={pid}

:WAIT_LOOP
timeout /t 1 /nobreak >nul
tasklist /fi ""PID eq %PID%"" 2>nul | findstr /i ""%PID%"" >nul
if %ERRORLEVEL% equ 0 goto WAIT_LOOP

timeout /t 1 /nobreak >nul
xcopy ""%SOURCE%\*"" ""%TARGET%\"" /s /e /y /q >nul
start """" ""%TARGET%\UsageTray.exe""
exit
";
        File.WriteAllText(scriptPath, scriptContent, System.Text.Encoding.UTF8);
        return scriptPath;
    }

    public static void ExecuteUpdateScriptAndExit(string scriptPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{scriptPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(scriptPath)
        };
        Process.Start(startInfo);
        Environment.Exit(0);
    }

    public static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // 忽略打开浏览器异常
        }
    }
}
