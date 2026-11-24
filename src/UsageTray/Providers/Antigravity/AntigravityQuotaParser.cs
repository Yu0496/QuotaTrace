using System.Globalization;
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
            if (IsObsoleteArgonQuota(model, label)) continue;
            var window = JsonValueReader.FindString(item, "window_kind", "windowKind", "window", "kind") ?? "unknown";
            var resetText = JsonValueReader.FindString(item, "reset_time", "resetTime", "reset_at", "resetAt");
            DateTimeOffset? reset = DateTimeOffset.TryParse(resetText, out var parsedReset) ? parsedReset : null;
            snapshots.Add(new QuotaSnapshot(ProviderKind.Antigravity, capturedAt, model, label, remaining, reset, window, source, plan));
        }

        if (snapshots.Count == 0 && JsonValueReader.FindString(root, "status", "message") is { } message)
            warnings.Add($"Antigravity quota 没有可用项目：{message}");
        return new AntigravityQuotaResult(snapshots, source, plan, endpoint, warnings);
    }

    public AntigravityQuotaResult ParseStatusLineQuota(
        JsonElement root,
        DateTimeOffset capturedAt,
        string source = QuotaSources.AntigravityStatusLine)
    {
        var snapshots = new List<QuotaSnapshot>();
        var warnings = new List<string>();
        var plan = JsonValueReader.FindString(root, "plan_tier", "planTier", "plan", "tier");

        if (!JsonValueReader.TryGetProperty(root, out var quotaElement, "quota", "quotas"))
        {
            return new AntigravityQuotaResult(snapshots, source, plan, null, warnings);
        }

        if (quotaElement.ValueKind == JsonValueKind.Object)
        {
            if (TryExtractFromGroups(quotaElement, capturedAt, source, plan, snapshots, warnings) && snapshots.Count > 0)
            {
                return new AntigravityQuotaResult(snapshots, source, plan, null, warnings);
            }

            foreach (var prop in quotaElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    TryAddStatusLineBucket(prop.Name, prop.Value, capturedAt, source, plan, snapshots, warnings);
                }
                else if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var elem in prop.Value.EnumerateArray())
                    {
                        if (elem.ValueKind == JsonValueKind.Object)
                        {
                            TryAddStatusLineBucket(null, elem, capturedAt, source, plan, snapshots, warnings);
                        }
                    }
                }
            }
        }
        else if (quotaElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var elem in quotaElement.EnumerateArray())
            {
                if (elem.ValueKind == JsonValueKind.Object)
                {
                    TryAddStatusLineBucket(null, elem, capturedAt, source, plan, snapshots, warnings);
                }
            }
        }

        return new AntigravityQuotaResult(snapshots, source, plan, null, warnings);
    }

    private static void TryAddStatusLineBucket(
        string? bucketKey,
        JsonElement bucket,
        DateTimeOffset capturedAt,
        string source,
        string? plan,
        List<QuotaSnapshot> snapshots,
        List<string> warnings)
    {
        if (IsDisabled(bucket)) return;

        var rawId = JsonValueReader.FindString(bucket, "bucket_id", "bucketId", "id", "model_id", "modelId", "quota_id", "quotaId")
            ?? bucketKey
            ?? "unknown";
        var rawLabel = JsonValueReader.FindString(bucket, "display_name", "displayName", "label", "name") ?? rawId;
        if (IsObsoleteArgonQuota(bucketKey, rawId, rawLabel)) return;

        var remaining = JsonValueReader.GetDouble(bucket, "remaining_fraction", "remainingFraction", "remaining_fraction_value", "remaining_amount", "remainingAmount");
        if (remaining.HasValue)
        {
            if (remaining is < 0 or > 1)
            {
                if (warnings.Count < 20) warnings.Add("statusLine quota 返回了范围外的 remaining_fraction，已忽略该条。");
                return;
            }
        }
        else
        {
            var pct = JsonValueReader.GetDouble(bucket, "remaining_percentage", "remainingPercentage");
            if (!pct.HasValue) return;
            if (pct is < 0 or > 100)
            {
                if (warnings.Count < 20) warnings.Add("statusLine quota 返回了范围外的 remaining_percentage，已忽略该条。");
                return;
            }
            remaining = Math.Round(pct.Value / 100.0, 6);
        }

        DateTimeOffset? reset = null;
        var resetText = JsonValueReader.FindString(bucket, "reset_time", "resetTime", "reset_at", "resetAt");
        if (!string.IsNullOrWhiteSpace(resetText) &&
            DateTimeOffset.TryParse(resetText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedReset))
        {
            reset = parsedReset;
        }
        else
        {
            var resetInSeconds = JsonValueReader.GetDouble(bucket, "reset_in_seconds", "resetInSeconds");
            if (resetInSeconds.HasValue && resetInSeconds.Value >= 0)
            {
                reset = capturedAt.AddSeconds(resetInSeconds.Value);
            }
        }

        var rawWindow = JsonValueReader.FindString(bucket, "window", "window_kind", "windowKind", "kind");
        var (normalizedId, normalizedWindow, normalizedLabel) = NormalizeStatusLinePoolAndWindow(rawId, rawLabel, rawWindow, capturedAt, reset);
        if (IsObsoleteArgonQuota(normalizedId, normalizedLabel)) return;

        snapshots.Add(new QuotaSnapshot(
            ProviderKind.Antigravity,
            capturedAt,
            normalizedId,
            normalizedLabel,
            remaining,
            reset,
            normalizedWindow,
            source,
            plan));
    }

    private static bool IsDisabled(JsonElement element)
    {
        if (!JsonValueReader.TryGetProperty(element, out var prop, "disabled")) return false;
        return prop.ValueKind == JsonValueKind.True ||
               (prop.ValueKind == JsonValueKind.String && string.Equals(prop.GetString(), "true", StringComparison.OrdinalIgnoreCase));
    }

    private static (string ModelOrPoolId, string WindowKind, string DisplayLabel) NormalizeStatusLinePoolAndWindow(
        string rawId,
        string rawLabel,
        string? rawWindow,
        DateTimeOffset capturedAt,
        DateTimeOffset? reset)
    {
        var hint = $"{rawId} {rawLabel} {rawWindow}".ToLowerInvariant();
        var windowHint = $"{rawWindow} {rawId} {rawLabel}".ToLowerInvariant();

        string windowKind;
        if (windowHint.Contains("week", StringComparison.Ordinal) ||
            windowHint.Contains("7-day", StringComparison.Ordinal) ||
            windowHint.Contains("7 day", StringComparison.Ordinal) ||
            windowHint.Contains("7d", StringComparison.Ordinal) ||
            windowHint.Contains("周", StringComparison.Ordinal))
        {
            windowKind = "weekly";
        }
        else if (windowHint.Contains("5h", StringComparison.Ordinal) ||
                 windowHint.Contains("5 h", StringComparison.Ordinal) ||
                 windowHint.Contains("5-hour", StringComparison.Ordinal) ||
                 windowHint.Contains("5 hour", StringComparison.Ordinal) ||
                 windowHint.Contains("five_hour", StringComparison.Ordinal) ||
                 windowHint.Contains("five hour", StringComparison.Ordinal) ||
                 windowHint.Contains("5小时", StringComparison.Ordinal))
        {
            windowKind = "5h";
        }
        else if (reset.HasValue && (reset.Value - capturedAt) > TimeSpan.FromHours(8))
        {
            windowKind = "weekly";
        }
        else
        {
            windowKind = string.IsNullOrWhiteSpace(rawWindow) ? "unknown" : rawWindow.Trim();
        }

        var is3p = (hint.Contains("3p", StringComparison.Ordinal) ||
                    hint.Contains("claude", StringComparison.Ordinal) ||
                    hint.Contains("gpt", StringComparison.Ordinal) ||
                    hint.Contains("other", StringComparison.Ordinal)) &&
                   !hint.Contains("gemini", StringComparison.Ordinal);

        var isGemini = hint.Contains("gemini", StringComparison.Ordinal);

        if (isGemini && windowKind is "weekly" or "5h")
        {
            return windowKind == "weekly"
                ? ("gemini-weekly", "weekly", "Gemini (周额度)")
                : ("gemini-5h", "5h", "Gemini (5小时额度)");
        }

        if (is3p && windowKind is "weekly" or "5h")
        {
            return windowKind == "weekly"
                ? ("3p-weekly", "weekly", "Claude / GPT (周额度)")
                : ("3p-5h", "5h", "Claude / GPT (5小时额度)");
        }

        return (rawId, windowKind, rawLabel);
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
                    if (IsObsoleteArgonQuota(groupName)) continue;
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
                            if (IsObsoleteArgonQuota(bucketId, bucketDisplayName)) continue;
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

    private static bool IsObsoleteArgonQuota(params string?[] values) =>
        values.Any(v => !string.IsNullOrWhiteSpace(v) && v.Contains("argon", StringComparison.OrdinalIgnoreCase));
}
