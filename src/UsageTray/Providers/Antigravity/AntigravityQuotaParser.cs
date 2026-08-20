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

        // 优先解析 RetrieveUserQuotaSummary 官方标准嵌套 groups 结构
        if (TryExtractFromGroups(root, capturedAt, source, plan, snapshots, warnings) && snapshots.Count > 0)
        {
            return new AntigravityQuotaResult(snapshots, source, plan, endpoint, warnings);
        }

        // 回退到通用扁平对象遍历
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

    private static bool TryExtractFromGroups(JsonElement root, DateTimeOffset capturedAt, string source, string? plan,
        List<QuotaSnapshot> snapshots, List<string> warnings)
    {
        var foundAny = false;
        foreach (var candidate in JsonValueReader.EnumerateObjects(root))
        {
            if (JsonValueReader.TryGetProperty(candidate, out var groupsElement, "groups") &&
                groupsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var group in groupsElement.EnumerateArray())
                {
                    var groupName = JsonValueReader.FindString(group, "display_name", "displayName", "name") ?? "Antigravity";
                    if (JsonValueReader.TryGetProperty(group, out var bucketsElement, "buckets") &&
                        bucketsElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var bucket in bucketsElement.EnumerateArray())
                        {
                            var remaining = JsonValueReader.GetDouble(bucket, "remaining_fraction", "remainingFraction", "remaining_fraction_value");
                            if (!remaining.HasValue) continue;
                            if (remaining is < 0 or > 1)
                            {
                                if (warnings.Count < 20) warnings.Add("Antigravity quota 返回了范围外的 remaining fraction，已忽略该条。");
                                continue;
                            }
                            var bucketId = JsonValueReader.FindString(bucket, "bucket_id", "bucketId", "id", "model_id", "modelId") ?? "unknown";
                            var window = JsonValueReader.FindString(bucket, "window", "window_kind", "windowKind", "kind") ?? "unknown";
                            var bucketDisplayName = JsonValueReader.FindString(bucket, "display_name", "displayName", "name") ?? window;
                            var resetText = JsonValueReader.FindString(bucket, "reset_time", "resetTime", "reset_at", "resetAt");
                            DateTimeOffset? reset = DateTimeOffset.TryParse(resetText, out var parsedReset) ? parsedReset : null;

                            // 组合成易读且具有区分度的 Label
                            var isWeekly = window.Equals("weekly", StringComparison.OrdinalIgnoreCase) || bucketId.Contains("weekly", StringComparison.OrdinalIgnoreCase);
                            var windowText = isWeekly ? "周额度" : "5小时额度";
                            var cleanGroupName = System.Text.RegularExpressions.Regex.Replace(groupName, @"\s*models\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Replace(" and ", " / ").Trim();
                            var label = $"{cleanGroupName} ({windowText})";

                            snapshots.Add(new QuotaSnapshot(ProviderKind.Antigravity, capturedAt, bucketId, label, remaining, reset, window, source, plan));
                            foundAny = true;
                        }
                    }
                }
            }
        }
        return foundAny;
    }

}
