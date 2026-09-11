using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using UsageTray.App;
using UsageTray.Core;
using UsageTray.Data;
using UsageTray.Pricing;
using UsageTray.Providers.Antigravity;
using UsageTray.Providers.Codex;
using UsageTray.Services;
using UsageTray.UI;
using UsageTray.UI.Controls;
using Xunit;

namespace UsageTray.Tests;

public class ScreenshotGeneratorTests
{
    [Fact]
    public void GenerateScreenshots()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                RunGeneration();
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (threadEx != null)
        {
            throw new Exception("Screenshot generation failed", threadEx);
        }
    }

    private static void RunGeneration()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var zhDir = Path.Combine(repoRoot, "docs", "images", "zh-CN");
        var enDir = Path.Combine(repoRoot, "docs", "images", "en-US");
        Directory.CreateDirectory(zhDir);
        Directory.CreateDirectory(enDir);

        var dbPath = AppPaths.DatabasePath;
        var settingsPath = AppPaths.SettingsPath;
        var settingsStore = new AppSettingsStore(File.Exists(settingsPath) ? settingsPath : Path.GetTempFileName());
        var settings = settingsStore.Load();
        settings.EnableCodex = true;
        settings.EnableAntigravity = true;

        var pricing = PricingService.LoadOrCreate(AppPaths.PricingPath, Path.Combine(AppContext.BaseDirectory, "Pricing", "default-pricing.json"));
        UsageDatabase database;
        UsageRepository repository;
        if (File.Exists(dbPath))
        {
            database = new UsageDatabase(dbPath);
            repository = new UsageRepository(database);
        }
        else
        {
            database = new UsageDatabase(Path.GetTempFileName());
            repository = new UsageRepository(database);
        }

        var aggregator = new UsageAggregator(repository, pricing);
        var providers = new Providers.IUsageProvider[] { new CodexProvider(), new AntigravityProvider() };
        using var coordinator = new RefreshCoordinator(providers, settingsStore, repository, pricing, aggregator, settings);

        // Build a high-quality weekly snapshot
        coordinator.RefreshAsync(false).GetAwaiter().GetResult();
        var snapshot = coordinator.BuildSnapshot(DateRange.LastDays(7), null, true);
        if ((snapshot.SpeedEstimate is null || !snapshot.SpeedEstimate.HasData) && coordinator.CurrentSnapshot?.SpeedEstimate?.HasData == true)
        {
            snapshot = snapshot with { SpeedEstimate = coordinator.CurrentSnapshot.SpeedEstimate };
        }
        if (snapshot.SpeedEstimate is null || !snapshot.SpeedEstimate.HasData)
        {
            snapshot = snapshot with { SpeedEstimate = new TokenSpeedEstimate(9400, 15200, 36.8, 100, 100, 100, 50) };
        }

        // Generate for both languages
        try
        {
            RenderLanguageScreenshots("zh-CN", zhDir, coordinator, settingsStore, snapshot);
            RenderLanguageScreenshots("en-US", enDir, coordinator, settingsStore, snapshot);
        }
        finally
        {
            I18n.SetLanguage("zh-CN");
        }
    }

    private static void RenderLanguageScreenshots(
        string language,
        string outputDir,
        RefreshCoordinator coordinator,
        AppSettingsStore settingsStore,
        DashboardSnapshot snapshot)
    {
        I18n.SetLanguage(language);

        // 1. Render MainForm (Main Dashboard) at 1180x620 ratio to display all columns and rows clearly
        using (var form = new MainForm(coordinator, settingsStore))
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-2000, -2000);
            form.Size = new Size(1180, 635);
            form.Show();
            for (int i = 0; i < 15; i++)
            {
                Application.DoEvents();
                Thread.Sleep(30);
            }
            form.RestoreColumnWidths(null, null);
            form.ApplySnapshot(snapshot);
            Application.DoEvents();

            using var bmp = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
            SaveJpeg(bmp, Path.Combine(outputDir, "main-dashboard.jpg"), 85);
            form.Hide();
        }

        // 2. Render Tray Mini Popup (Single-click card)
        using (var popup = new QuotaPopupForm())
        {
            popup.StartPosition = FormStartPosition.Manual;
            popup.Location = new Point(-2000, -2000);
            popup.Size = new Size(450, 480);
            popup.UpdateProviderSettings(true, true);
            popup.SetSnapshot(snapshot);
            popup.Show();
            Application.DoEvents();

            using var bmp = new Bitmap(popup.Width, popup.Height);
            popup.DrawToBitmap(bmp, new Rectangle(0, 0, popup.Width, popup.Height));
            SaveJpeg(bmp, Path.Combine(outputDir, "tray-popup.jpg"), 85);
            popup.Hide();
        }

        // 3. Render Tray Hover Tooltip (Mouse Hover)
        RenderTrayTooltip(language, outputDir, snapshot);
    }

    private static void RenderTrayTooltip(
        string language,
        string outputDir,
        DashboardSnapshot snapshot)
    {
        var width = 520;
        var height = 140;
        using var bmp = new Bitmap(width, height);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // Desktop background
        using (var bgBrush = new LinearGradientBrush(new Rectangle(0, 0, width, height), Color.FromArgb(240, 244, 248), Color.FromArgb(226, 232, 240), 90f))
        {
            g.FillRectangle(bgBrush, 0, 0, width, height);
        }

        // Windows Taskbar at bottom
        var taskbarHeight = 46;
        var taskbarY = height - taskbarHeight;
        using (var taskbarBrush = new SolidBrush(Color.FromArgb(248, 249, 250)))
        {
            g.FillRectangle(taskbarBrush, 0, taskbarY, width, taskbarHeight);
        }
        using (var taskbarBorder = new Pen(Color.FromArgb(222, 226, 230), 1))
        {
            g.DrawLine(taskbarBorder, 0, taskbarY, width, taskbarY);
        }

        // Clock / Date on taskbar right
        using var clockFont = new Font("Segoe UI", 8.5f, FontStyle.Regular);
        using var textBrush = new SolidBrush(Color.FromArgb(33, 37, 41));
        var timeStr = DateTime.Now.ToString("HH:mm");
        var dateStr = DateTime.Now.ToString("yyyy/M/d");
        g.DrawString(timeStr, clockFont, textBrush, width - 64, taskbarY + 6);
        g.DrawString(dateStr, clockFont, textBrush, width - 64, taskbarY + 22);

        // System tray icons (chevron, wifi, speaker)
        using var iconPen = new Pen(Color.FromArgb(73, 80, 87), 1.5f);
        // Chevron ^
        g.DrawLine(iconPen, width - 150, taskbarY + 24, width - 146, taskbarY + 20);
        g.DrawLine(iconPen, width - 146, taskbarY + 20, width - 142, taskbarY + 24);

        // WiFi arcs
        g.DrawArc(iconPen, width - 126, taskbarY + 16, 14, 14, 210, 120);
        g.DrawArc(iconPen, width - 123, taskbarY + 20, 8, 8, 210, 120);
        g.FillEllipse(textBrush, width - 120, taskbarY + 26, 2, 2);

        // Speaker
        var spkX = width - 96;
        var spkY = taskbarY + 17;
        Point[] speakerPts = [new(spkX, spkY + 4), new(spkX + 3, spkY + 4), new(spkX + 7, spkY), new(spkX + 7, spkY + 12), new(spkX + 3, spkY + 8), new(spkX, spkY + 8)];
        g.FillPolygon(textBrush, speakerPts);
        g.DrawArc(iconPen, spkX + 6, spkY + 1, 6, 10, -60, 120);

        // App Icon in tray
        var appIconX = width - 200;
        var appIconY = taskbarY + 7;
        using (var highlightBrush = new SolidBrush(Color.FromArgb(233, 236, 239)))
        {
            var r = new Rectangle(appIconX - 6, appIconY - 2, 36, 36);
            using var path = GetRoundedRectPath(r, 4);
            g.FillPath(highlightBrush, path);
        }

        using (var icon = AppIcon.Create())
        {
            using var iconBmp = icon.ToBitmap();
            g.DrawImage(iconBmp, new Rectangle(appIconX, appIconY + 4, 24, 24));
        }

        // Tooltip text
        var tooltipText = QuotaDisplayFormatter.BuildCompactText(snapshot, true, true);
        using var tooltipFont = new Font("Segoe UI", 9.25f, FontStyle.Regular);
        var textSize = TextRenderer.MeasureText(g, tooltipText, tooltipFont);

        var tipW = textSize.Width + 24;
        var tipH = textSize.Height + 14;
        var tipX = Math.Max(12, appIconX + 12 - tipW / 2);
        if (tipX + tipW > width - 12) tipX = width - 12 - tipW;
        var tipY = taskbarY - tipH - 8;

        // Tooltip Drop Shadow
        using (var shadowBrush = new SolidBrush(Color.FromArgb(25, 0, 0, 0)))
        {
            var shadowRect = new Rectangle(tipX + 2, tipY + 2, tipW, tipH);
            using var sPath = GetRoundedRectPath(shadowRect, 6);
            g.FillPath(shadowBrush, sPath);
        }

        // Tooltip Balloon Body
        var tipRect = new Rectangle(tipX, tipY, tipW, tipH);
        using (var tipPath = GetRoundedRectPath(tipRect, 6))
        {
            using var tipBg = new SolidBrush(Color.FromArgb(255, 255, 255));
            g.FillPath(tipBg, tipPath);
            using var tipBorder = new Pen(Color.FromArgb(209, 213, 219), 1f);
            g.DrawPath(tipBorder, tipPath);
        }

        // Tooltip Text
        using var tipTextBrush = new SolidBrush(Color.FromArgb(17, 24, 39));
        g.DrawString(tooltipText, tooltipFont, tipTextBrush, tipX + 12, tipY + 7);

        // Mouse Cursor Arrow pointing at app icon
        DrawCursor(g, appIconX + 14, appIconY + 12);

        // Save image
        SaveJpeg(bmp, Path.Combine(outputDir, "tray-tooltip.jpg"), 85);
    }

    private static void DrawCursor(Graphics g, int x, int y)
    {
        Point[] cursorOuter = [
            new(x, y),
            new(x, y + 15),
            new(x + 4, y + 12),
            new(x + 7, y + 18),
            new(x + 9, y + 17),
            new(x + 6, y + 11),
            new(x + 11, y + 11)
        ];
        using var whiteBrush = new SolidBrush(Color.White);
        using var blackPen = new Pen(Color.FromArgb(30, 30, 30), 1.5f) { LineJoin = LineJoin.Round };
        g.FillPolygon(whiteBrush, cursorOuter);
        g.DrawPolygon(blackPen, cursorOuter);
    }

    private static GraphicsPath GetRoundedRectPath(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void SaveJpeg(Bitmap bmp, string path, long quality = 85)
    {
        var encoder = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
        if (encoder is null)
        {
            bmp.Save(path, ImageFormat.Jpeg);
            return;
        }
        using var encoderParams = new EncoderParameters(1);
        encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        bmp.Save(path, encoder, encoderParams);
    }
}
