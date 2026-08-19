using System.Runtime.InteropServices;
using UsageTray.App;
using UsageTray.Core;
using UsageTray.Data;
using UsageTray.Providers.Antigravity;
using UsageTray.Providers.Codex;

namespace UsageTray.Services;

public sealed class DiagnosticsService
{
    private readonly UsageRepository _repository;
    private readonly CodexSessionLocator _codexLocator;
    private readonly AntigravityHistoryLocator _antigravityLocator;

    public DiagnosticsService(UsageRepository repository, CodexSessionLocator? codexLocator = null,
        AntigravityHistoryLocator? antigravityLocator = null)
    {
        _repository = repository;
        _codexLocator = codexLocator ?? new CodexSessionLocator();
        _antigravityLocator = antigravityLocator ?? new AntigravityHistoryLocator();
    }

    public string BuildReport(AppSettings settings)
    {
        var roots = _codexLocator.GetCandidateRoots(settings.ExtraCodexRoots);
        var codexFiles = _codexLocator.DiscoverJsonlFiles(roots);
        var agJson = _antigravityLocator.DiscoverJsonFiles();
        var agDb = _antigravityLocator.DiscoverDatabaseFiles();
        return $"UsageTray\n版本：{typeof(DiagnosticsService).Assembly.GetName().Version}\nWindows：{RuntimeInformation.OSDescription}\n.NET：{Environment.Version}\n\n" +
               $"Codex\n路径：{string.Join("; ", roots)}\nJSONL 文件：{codexFiles.Count}\n\n" +
               $"Antigravity\n历史 JSON：{agJson.Count}\nDB/PB 文件：{agDb.Count}\n历史 token 标记：{_repository.GetFlag("antigravity_history_tokens") ?? "未检测"}\n" +
               "说明：此报告不包含 prompt、response、CSRF、OAuth 或邮箱。";
    }
}
