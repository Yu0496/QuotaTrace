using System.Text.Json;
using UsageTray.Data;
using UsageTray.Pricing;
using UsageTray.Providers.Codex;
using UsageTray.Services;

namespace UsageTray.App;

internal static class CodexMaintenanceCli
{
    public static int Rebuild()
    {
        AppPaths.EnsureDirectories();
        var backupPath = BackupDatabase();
        using var database = new UsageDatabase(AppPaths.DatabasePath);
        var repository = new UsageRepository(database);
        var settings = new AppSettingsStore(AppPaths.SettingsPath).Load();
        var bundledPricing = Path.Combine(AppContext.BaseDirectory, "Pricing", "default-pricing.json");
        var pricing = PricingService.LoadOrCreate(AppPaths.PricingPath, bundledPricing);
        var result = new CodexProvider().RefreshAsync(new UsageTray.Providers.RefreshContext(settings, repository, pricing, true), CancellationToken.None).GetAwaiter().GetResult();
        var auditPath = Path.Combine(AppPaths.DiagnosticsPath, "codex-audit.csv");
        var diagnostics = new DiagnosticsService(repository);
        diagnostics.ExportCodexAuditCsv(auditPath);
        var reportPath = Path.Combine(AppPaths.DiagnosticsPath, "codex-rebuild-result.json");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            completedAtUtc = DateTimeOffset.UtcNow,
            database = AppPaths.DatabasePath,
            backup = backupPath,
            audit = auditPath,
            bucketCount = result.Usage.Count,
            inputTokens = result.Usage.Sum(item => item.InputTokens),
            cachedInputTokens = result.Usage.Sum(item => item.CachedInputTokens),
            outputTokens = result.Usage.Sum(item => item.OutputTokens),
            warnings = result.Warnings
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static string? BackupDatabase()
    {
        if (!File.Exists(AppPaths.DatabasePath)) return null;
        var path = AppPaths.DatabasePath + $".pre-codex-rebuild-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.bak";
        File.Copy(AppPaths.DatabasePath, path, false);
        return path;
    }
}
