using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
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
               $"解析器版本：{CodexJsonlParser.ParserVersion}\n数据库模式：{_repository.GetFlag("codex_schema_version") ?? "未知"}\n待重建：{_repository.GetFlag("codex_rebuild_required") ?? "未知"}\n" +
               $"Codex 原始快照：{CountAudit("Events")}，重复 EventKey：{AuditNumber("DuplicateEventCount")}，计数回退：{AuditNumber("CounterRewindCount")}\n" +
               "官方 app-server 交叉校验：N/A（当前未配置本地 RPC）\n\n" +
               $"Antigravity\n历史 JSON：{agJson.Count}\nDB/PB 文件：{agDb.Count}\n历史 token 标记：{_repository.GetFlag("antigravity_history_tokens") ?? "未检测"}\n" +
               "说明：此报告不包含 prompt、response、CSRF、OAuth 或邮箱。";
    }

    public string BuildCodexAuditReport() => _repository.GetCodexAuditJson() ??
        "{\"status\":\"N/A\",\"message\":\"尚未完成 Codex 原始快照扫描\"}";

    public string ExportCodexAuditCsv(string filePath)
    {
        using var document = JsonDocument.Parse(BuildCodexAuditReport());
        var builder = new StringBuilder();
        builder.AppendLine("session_id,event_key,captured_at_utc,model_id,source_path,source_line,counter_epoch,input_tokens,cached_input_tokens,cache_write_input_tokens,output_tokens,request_usage_quality,is_long_context,is_duplicate,duplicate_sources");
        if (document.RootElement.TryGetProperty("Events", out var events) && events.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in events.EnumerateArray())
            {
                builder.AppendJoin(',', [
                    Csv(item, "SessionId"), Csv(item, "EventKey"), Csv(item, "CapturedAt"), Csv(item, "ModelId"),
                    Csv(item, "SourcePath"), Csv(item, "SourceLine"), Csv(item, "CounterEpoch"),
                    CsvNested(item, "Delta", "InputTokens"), CsvNested(item, "Delta", "CachedInputTokens"),
                    CsvNested(item, "Delta", "CacheWriteInputTokens"), CsvNested(item, "Delta", "OutputTokens"),
                    Csv(item, "RequestUsageQuality"), Csv(item, "IsLongContext"), Csv(item, "IsDuplicate"), Csv(item, "DuplicateSources")
                ]);
                builder.AppendLine();
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, builder.ToString(), Encoding.UTF8);
        return filePath;
    }

    private int CountAudit(string name)
    {
        try
        {
            using var document = JsonDocument.Parse(BuildCodexAuditReport());
            return document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : 0;
        }
        catch { return 0; }
    }

    private int AuditNumber(string name)
    {
        try
        {
            using var document = JsonDocument.Parse(BuildCodexAuditReport());
            return document.RootElement.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;
        }
        catch { return 0; }
    }

    private static string Csv(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value)) return string.Empty;
        return CsvValue(value);
    }

    private static string CsvNested(JsonElement item, string parent, string property) =>
        item.TryGetProperty(parent, out var nested) ? Csv(nested, property) : string.Empty;

    private static string CsvValue(JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.Array ? string.Join(';', value.EnumerateArray().Select(CsvValue)) : value.ToString();
        return $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
