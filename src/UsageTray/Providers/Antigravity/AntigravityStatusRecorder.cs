using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UsageTray.App;
using UsageTray.Core;
using UsageTray.Data;
using UsageTray.Providers;

namespace UsageTray.Providers.Antigravity;

public sealed record StatusRecorderResult(bool Recorded, UsageEvent? Event, string? Warning, bool CounterReset, int QuotasRecorded = 0);

public sealed class AntigravityStatusRecorder
{
    private readonly UsageRepository _repository;
    private readonly AppSettings _settings;

    public AntigravityStatusRecorder(UsageRepository repository, AppSettings settings)
    {
        _repository = repository;
        _settings = settings;
    }

    public StatusRecorderResult Record(JsonElement payload, DateTimeOffset? receivedAt = null)
    {
        var now = (receivedAt ?? DateTimeOffset.Now).ToUniversalTime();
        var quotasRecorded = RecordStatusLineQuotas(payload, now);

        if (string.Equals(_repository.GetFlag("antigravity_history_tokens"), "1", StringComparison.Ordinal))
            return quotasRecorded > 0
                ? new StatusRecorderResult(true, null, null, false, quotasRecorded)
                : new StatusRecorderResult(false, null, "已发现可回溯的 Antigravity 历史 token，按来源优先级跳过 status-line 写入。", false);

        var conversationId = JsonValueReader.FindConversationId(payload);
        if (string.IsNullOrWhiteSpace(conversationId))
            return quotasRecorded > 0
                ? new StatusRecorderResult(true, null, null, false, quotasRecorded)
                : new StatusRecorderResult(false, null, "status-line payload 缺少 conversation_id，未记录。", false);
        var model = JsonValueReader.FindModel(payload) ?? "Unknown";
        var projectKey = ProjectResolver.Normalize(JsonValueReader.FindProjectPath(payload));
        var totalInput = FindNestedLong(payload, ["total_input_tokens", "totalInputTokens"]);
        var totalOutput = FindNestedLong(payload, ["total_output_tokens", "totalOutputTokens"]);
        if (!totalInput.HasValue && !totalOutput.HasValue)
            return quotasRecorded > 0
                ? new StatusRecorderResult(true, null, null, false, quotasRecorded)
                : new StatusRecorderResult(false, null, "status-line payload 缺少 context_window.total_*_tokens，未记录。", false);
        totalInput ??= 0;
        totalOutput ??= 0;
        var cacheRead = FindNestedLong(payload, ["cache_read_input_tokens", "cacheReadInputTokens"]) ?? 0;
        var cacheWrite = FindNestedLong(payload, ["cache_creation_input_tokens", "cacheCreationInputTokens"]) ?? 0;
        var currentInput = FindNestedLong(payload, ["current_usage", "currentUsage", "input_tokens", "inputTokens"]) ?? 0;
        var currentOutput = FindNestedLong(payload, ["current_usage", "currentUsage", "output_tokens", "outputTokens"]) ?? 0;
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.GetRawText())));
        var previous = _repository.GetRecorderState(conversationId);
        var epoch = previous?.CounterEpoch ?? 0;
        var counterReset = false;
        long deltaInput;
        long deltaOutput;
        if (previous is null)
        {
            deltaInput = totalInput.Value;
            deltaOutput = totalOutput.Value;
        }
        else if (totalInput.Value < previous.LastTotalInput || totalOutput.Value < previous.LastTotalOutput)
        {
            epoch++;
            counterReset = true;
            deltaInput = 0;
            deltaOutput = 0;
        }
        else
        {
            deltaInput = totalInput.Value - previous.LastTotalInput;
            deltaOutput = totalOutput.Value - previous.LastTotalOutput;
        }

        _repository.SaveRecorderState(new RecorderState(conversationId, totalInput.Value, totalOutput.Value, cacheRead, cacheWrite,
            model, fingerprint, now, epoch));
        if (deltaInput <= 0 && deltaOutput <= 0)
            return quotasRecorded > 0
                ? new StatusRecorderResult(true, null, null, counterReset, quotasRecorded)
                : new StatusRecorderResult(false, null, counterReset ? "Antigravity totals 发生回退，已建立新计数 epoch。" : null, counterReset);

        var cached = Math.Min(Math.Max(0, deltaInput), Math.Max(0, cacheRead));
        var write = Math.Min(Math.Max(0, deltaInput - cached), Math.Max(0, cacheWrite));
        var costQuality = (cached > 0 || write > 0) && !_settings.StatusLineCacheSemanticsValidated
            ? CostQuality.PartialPrice
            : ((cached > 0 || write > 0) ? CostQuality.ExactTokenSplit : CostQuality.ExactTokensNoCache);
        if (!_settings.StatusLineCacheSemanticsValidated && (currentInput > 0 || currentOutput > 0))
            costQuality = CostQuality.PartialPrice;
        var bucket = new UsageBucket(ProviderKind.Antigravity, DateOnly.FromDateTime(now.ToLocalTime().DateTime), projectKey, model,
            deltaInput, cached, deltaOutput, 1, DataQuality.Derived,
            string.IsNullOrWhiteSpace(JsonValueReader.FindString(payload, "transcript_path", "transcriptPath"))
                ? "status-line" : JsonValueReader.FindString(payload, "transcript_path", "transcriptPath")!,
            conversationId, write, costQuality);
        var eventId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{conversationId}\0{epoch}\0{totalInput}\0{totalOutput}\0{model}")));
        var usageEvent = new UsageEvent(eventId, "statusline", now, bucket);
        _repository.AddUsageEvents([usageEvent]);
        _repository.SetFlag("coverage_start_Antigravity", GetCoverageStart(now));
        return new StatusRecorderResult(true, usageEvent, null, counterReset, quotasRecorded);
    }

    private int RecordStatusLineQuotas(JsonElement payload, DateTimeOffset now)
    {
        var parsed = new AntigravityQuotaParser().ParseStatusLineQuota(payload, now, QuotaSources.AntigravityStatusLine);
        if (parsed.Snapshots.Count == 0) return 0;

        var existing = _repository.GetLatestQuotas(ProviderKind.Antigravity);
        var toWrite = new List<QuotaSnapshot>();

        foreach (var snap in parsed.Snapshots)
        {
            if (!snap.HasValidFraction) continue;
            var prev = existing.FirstOrDefault(e =>
                string.Equals(e.ModelOrPoolId, snap.ModelOrPoolId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.WindowKind, snap.WindowKind, StringComparison.OrdinalIgnoreCase));
            if (prev is null || ShouldWriteStatusLineQuota(prev, snap, now))
            {
                toWrite.Add(snap);
            }
        }

        if (toWrite.Count > 0)
        {
            _repository.AddQuotaSnapshots(toWrite);
        }
        return toWrite.Count;
    }

    private static bool ShouldWriteStatusLineQuota(QuotaSnapshot prev, QuotaSnapshot curr, DateTimeOffset now)
    {
        if (!string.Equals(prev.Source, curr.Source, StringComparison.OrdinalIgnoreCase)) return true;
        if (prev.RemainingFraction.HasValue != curr.RemainingFraction.HasValue) return true;
        if (prev.RemainingFraction.HasValue && curr.RemainingFraction.HasValue &&
            Math.Abs(prev.RemainingFraction.Value - curr.RemainingFraction.Value) >= 0.001)
            return true;
        if (prev.ResetAt.HasValue != curr.ResetAt.HasValue) return true;
        if (prev.ResetAt.HasValue && curr.ResetAt.HasValue &&
            (prev.ResetAt.Value - curr.ResetAt.Value).Duration() > TimeSpan.FromSeconds(120))
            return true;
        return now - prev.CapturedAt >= TimeSpan.FromMinutes(5);
    }

    private static string GetCoverageStart(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

    private static long? FindNestedLong(JsonElement root, string[] names)
    {
        foreach (var item in JsonValueReader.EnumerateObjects(root))
        {
            var value = JsonValueReader.GetLong(item, names);
            if (value.HasValue) return value;
        }
        return null;
    }
}

public static class AntigravityStatusRecorderCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        AppPaths.EnsureDirectories();
        var settingsStore = new AppSettingsStore(AppPaths.SettingsPath);
        var settings = settingsStore.Load();
        using var database = new UsageDatabase(AppPaths.DatabasePath);
        var repository = new UsageRepository(database);
        var recorder = new AntigravityStatusRecorder(repository, settings);
        var input = await Console.In.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(input)) return 0;
        try
        {
            using var document = JsonDocument.Parse(input);
            var result = recorder.Record(document.RootElement);
            if (!string.IsNullOrWhiteSpace(result.Warning)) Console.Error.WriteLine(result.Warning);
            if (result.Event is not null)
            {
                var model = result.Event.Bucket.ModelId ?? "Unknown";
                Console.Write($"AG {model}");
            }
            else if (result.QuotasRecorded > 0)
            {
                Console.Write("QT ✓");
            }
            return 0;
        }
        catch (JsonException)
        {
            Console.Error.WriteLine("无效的 Antigravity status-line JSON。");
            return 2;
        }
    }
}
