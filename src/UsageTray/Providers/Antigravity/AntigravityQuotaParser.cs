using System.Text.Json;
using UsageTray.Core;
using UsageTray.Providers;

namespace UsageTray.Providers.Antigravity;

public sealed class AntigravityQuotaParser
{
    public AntigravityQuotaResult Parse(JsonElement root, DateTimeOffset capturedAt, string source, string? endpoint = null)
    {
        var snapshots = new List<QuotaSnapshot>();
        var warnings = new List<string>();
        var plan = JsonValueReader.FindString(root, "plan_tier", "planTier", "plan", "tier");
        foreach (var item in JsonValueReader.EnumerateObjects(root))
        {
            var remaining = JsonValueReader.GetDouble(item, "remaining_fraction", "remainingFraction", "remaining_fraction_value");
            if (!remaining.HasValue) continue;
            if (remaining is < 0 or > 1)
            {
                if (warnings.Count < 20) warnings.Add("Antigravity quota 返回了范围外的 remaining fraction，已忽略该条。");
                continue;
            }
            var model = JsonValueReader.FindString(item, "model_id", "modelId", "model", "model_name", "modelName", "quota_id", "quotaId") ?? "unknown";
            var label = JsonValueReader.FindString(item, "display_name", "displayName", "label", "name") ?? model;
            var window = JsonValueReader.FindString(item, "window_kind", "windowKind", "window", "kind") ?? "unknown";
            var resetText = JsonValueReader.FindString(item, "reset_time", "resetTime", "reset_at", "resetAt");
            DateTimeOffset? reset = DateTimeOffset.TryParse(resetText, out var parsedReset) ? parsedReset : null;
            snapshots.Add(new QuotaSnapshot(ProviderKind.Antigravity, capturedAt, model, label, remaining, reset, window, source, plan));
        }

        if (snapshots.Count == 0 && JsonValueReader.FindString(root, "status", "message") is { } message)
            warnings.Add($"Antigravity quota 没有可用项目：{message}");
        return new AntigravityQuotaResult(snapshots, source, plan, endpoint, warnings);
    }
}
