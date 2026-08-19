using System.Drawing.Drawing2D;
using UsageTray.Core;
using UsageTray.Services;

namespace UsageTray.UI;

internal sealed class QuotaPopupForm : Form
{
    private DashboardSnapshot? _snapshot;
    private readonly Font _titleFont;
    private readonly Font _sectionFont;
    private readonly Font _boldFont;
    private readonly Font _regularFont;
    private readonly Font _smallFont;

    public event EventHandler? DismissRequested;

    public QuotaPopupForm()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        Icon = AppIcon.Create();
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        ShowIcon = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.White;
        DoubleBuffered = true;

        _titleFont = new Font("Segoe UI", 10F, FontStyle.Bold, GraphicsUnit.Point);
        _sectionFont = new Font("Segoe UI", 9F, FontStyle.Bold, GraphicsUnit.Point);
        _boldFont = new Font("Segoe UI", 9F, FontStyle.Bold, GraphicsUnit.Point);
        _regularFont = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        _smallFont = new Font("Segoe UI", 8F, FontStyle.Regular, GraphicsUnit.Point);

        Deactivate += (_, _) => DismissRequested?.Invoke(this, EventArgs.Empty);

        SetSnapshot(null);
    }

    private bool _isMouseDown;
    private bool _isDragging;
    private Point _dragStartScreenPoint;
    private Point _formStartLocation;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _isMouseDown = true;
            _isDragging = false;
            _dragStartScreenPoint = PointToScreen(e.Location);
            _formStartLocation = Location;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_isMouseDown)
        {
            var currentScreen = PointToScreen(e.Location);
            var dx = currentScreen.X - _dragStartScreenPoint.X;
            var dy = currentScreen.Y - _dragStartScreenPoint.Y;
            if (!_isDragging && (Math.Abs(dx) > 3 || Math.Abs(dy) > 3))
            {
                _isDragging = true;
            }
            if (_isDragging)
            {
                Location = new Point(_formStartLocation.X + dx, _formStartLocation.Y + dy);
            }
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_isMouseDown)
        {
            _isMouseDown = false;
            if (!_isDragging)
            {
                DismissRequested?.Invoke(this, EventArgs.Empty);
            }
            _isDragging = false;
        }
    }

    public void SetSnapshot(DashboardSnapshot? snapshot)
    {
        _snapshot = snapshot;
        RecalculateSize();
        Invalidate();
    }

    private void RecalculateSize()
    {
        using var graphics = CreateGraphics();
        var width = (int)Math.Round(520f * DeviceDpi / 96f);
        var height = MeasureContentHeight(graphics, width);
        ClientSize = new Size(width, height);
    }

    private int MeasureContentHeight(Graphics g, int width)
    {
        var y = 14;
        // Header
        y += _titleFont.Height + 10;
        y += 1; // Divider
        y += 8;

        if (_snapshot is null)
        {
            y += _regularFont.Height + 20;
            return y;
        }

        // Section 1: Antigravity
        y += _sectionFont.Height + 6;
        var agSnapshots = _snapshot.Quotas
            .Where(q => q.Snapshot.Provider == ProviderKind.Antigravity)
            .Select(q => q.Snapshot).ToList();
        if (agSnapshots.Count == 0)
        {
            y += _regularFont.Height + 4;
        }
        else
        {
            var fiveHour = agSnapshots.Where(IsFiveHour).ToList();
            y += MeasureQuotaWindowHeight(fiveHour);
            var weekly = agSnapshots.Where(IsWeekly).ToList();
            y += MeasureQuotaWindowHeight(weekly);
            var others = agSnapshots.Where(s => !IsFiveHour(s) && !IsWeekly(s)).ToList();
            y += MeasureQuotaWindowHeight(others);
        }
        y += 6;
        y += 1; // Divider
        y += 8;

        // Section 2: Codex
        y += _sectionFont.Height + 6;
        var codexSnapshots = _snapshot.Quotas
            .Where(q => q.Snapshot.Provider == ProviderKind.Codex)
            .Select(q => q.Snapshot).ToList();
        if (codexSnapshots.Count > 0)
        {
            var fiveHour = codexSnapshots.Where(IsFiveHour).ToList();
            y += MeasureQuotaWindowHeight(fiveHour);
            var weekly = codexSnapshots.Where(IsWeekly).ToList();
            y += MeasureQuotaWindowHeight(weekly);
        }
        else
        {
            y += _regularFont.Height + 4;
            y += _regularFont.Height + 4;
        }

        // Codex Weekly Cycle & Projection
        if (_snapshot.CodexWeeklyCycle is { } cycle)
        {
            y += 2;
            y += _boldFont.Height + 3; // "周订阅本轮统计与预估"
            y += _regularFont.Height + 3; // 本轮周消耗
            y += _regularFont.Height + 4; // 周满额预估
        }

        // Codex Local Logs
        var codexModels = _snapshot.Models.Where(m => m.Provider == ProviderKind.Codex).ToList();
        y += 2;
        y += _boldFont.Height + 3; // "本地日志用量"
        if (codexModels.Count > 0)
        {
            y += _regularFont.Height + 3; // Input | Cache Read
            y += _regularFont.Height + 3; // Cache Creation | Output
            y += _regularFont.Height + 4; // API 等值
        }
        else
        {
            y += _regularFont.Height + 4;
        }

        y += 6;
        y += 1; // Divider
        y += 8;

        // Footer / Timestamp
        y += _smallFont.Height + 10;
        return y;
    }

    private int MeasureQuotaWindowHeight(IReadOnlyList<QuotaSnapshot> snapshots)
    {
        if (snapshots.Count == 0) return 0;
        if (snapshots.Count == 1) return _regularFont.Height + 4;
        return _boldFont.Height + 2 + (_regularFont.Height + 3) * snapshots.Count + 1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var bounds = ClientRectangle;
        using var borderPen = new Pen(Color.FromArgb(203, 213, 225));
        g.DrawRectangle(borderPen, 0, 0, bounds.Width - 1, bounds.Height - 1);

        var padX = (int)Math.Round(16f * DeviceDpi / 96f);
        var y = (int)Math.Round(12f * DeviceDpi / 96f);

        // Header
        using var titleBrush = new SolidBrush(Color.FromArgb(15, 23, 42));
        using var hintBrush = new SolidBrush(Color.FromArgb(148, 163, 184));
        using var dividerPen = new Pen(Color.FromArgb(226, 232, 240));

        g.DrawString("额度与用量摘要", _titleFont, titleBrush, padX, y);
        var closeText = "点击任意位置关闭";
        var closeSize = TextRenderer.MeasureText(g, closeText, _smallFont);
        g.DrawString(closeText, _smallFont, hintBrush, bounds.Width - padX - closeSize.Width, y + (_titleFont.Height - _smallFont.Height) / 2);

        y += _titleFont.Height + 8;
        g.DrawLine(dividerPen, padX, y, bounds.Width - padX, y);
        y += 8;

        if (_snapshot is null)
        {
            using var muted = new SolidBrush(Color.FromArgb(100, 116, 139));
            g.DrawString("暂无额度快照，请先刷新。", _regularFont, muted, padX, y);
            return;
        }

        // Section 1: Antigravity
        var agViews = _snapshot.Quotas.Where(q => q.Snapshot.Provider == ProviderKind.Antigravity).ToList();
        var agSnapshots = agViews.Select(q => q.Snapshot).ToList();
        var agPlan = agSnapshots.Select(s => s.PlanTier).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
        var agOffline = agViews.Count > 0 && agViews.All(v => v.IsOffline);

        DrawSectionHeader(g, padX, ref y, "Antigravity 额度", agPlan, agOffline ? "离线" : null, Color.FromArgb(59, 130, 246));

        if (agSnapshots.Count == 0)
        {
            using var muted = new SolidBrush(Color.FromArgb(148, 163, 184));
            g.DrawString("暂无可用快照，请确保 Antigravity 正在运行并刷新。", _regularFont, muted, padX + 8, y);
            y += _regularFont.Height + 4;
        }
        else
        {
            var fiveHour = agSnapshots.Where(IsFiveHour).ToList();
            if (fiveHour.Count > 0) DrawQuotaWindowLine(g, padX + 8, ref y, "5 小时窗口", fiveHour);
            var weekly = agSnapshots.Where(IsWeekly).ToList();
            if (weekly.Count > 0) DrawQuotaWindowLine(g, padX + 8, ref y, "周窗口", weekly);
            var others = agSnapshots.Where(s => !IsFiveHour(s) && !IsWeekly(s)).ToList();
            if (others.Count > 0) DrawQuotaWindowLine(g, padX + 8, ref y, "其他窗口", others);
        }

        y += 6;
        g.DrawLine(dividerPen, padX, y, bounds.Width - padX, y);
        y += 8;

        // Section 2: Codex
        var codexViews = _snapshot.Quotas.Where(q => q.Snapshot.Provider == ProviderKind.Codex).ToList();
        var codexSnapshots = codexViews.Select(q => q.Snapshot).ToList();
        var codexPlan = codexSnapshots.Select(s => s.PlanTier).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));

        DrawSectionHeader(g, padX, ref y, "Codex 额度与周订阅", codexPlan, null, Color.FromArgb(16, 185, 129));

        if (codexSnapshots.Count > 0)
        {
            var fiveHour = codexSnapshots.Where(IsFiveHour).ToList();
            if (fiveHour.Count > 0) DrawQuotaWindowLine(g, padX + 8, ref y, "5 小时窗口", fiveHour);
            var weekly = codexSnapshots.Where(IsWeekly).ToList();
            if (weekly.Count > 0) DrawQuotaWindowLine(g, padX + 8, ref y, "周窗口", weekly);
        }
        else
        {
            using var muted = new SolidBrush(Color.FromArgb(100, 116, 139));
            g.DrawString("5 小时窗口：剩余未知（当前 session 未写入 rate_limits）", _regularFont, muted, padX + 8, y);
            y += _regularFont.Height + 4;
            g.DrawString("周窗口：剩余未知（当前 session 未写入 rate_limits）", _regularFont, muted, padX + 8, y);
            y += _regularFont.Height + 4;
        }

        // Codex Weekly Cycle & Projection
        if (_snapshot.CodexWeeklyCycle is { } cycle)
        {
            y += 2;
            using var subHeaderBrush = new SolidBrush(Color.FromArgb(71, 85, 105));
            g.DrawString("周订阅统计与预估 (本轮 7 天窗口)", _boldFont, subHeaderBrush, padX + 8, y);
            y += _boldFont.Height + 3;

            var usedText = cycle.UsedFraction.HasValue ? $"{cycle.UsedFraction.Value:P0}" : "未知";
            var cycleCostText = cycle.CycleCostUsd.HasValue ? "$" + cycle.CycleCostUsd.Value.ToString("0.00") : "—";
            var estCostText = cycle.EstimatedWeeklyCostUsd.HasValue ? $"约 ${cycle.EstimatedWeeklyCostUsd.Value:0.00}" : "待产生消耗后推算";

            DrawKeyValueHighlight(g, padX + 16, ref y, "本轮周消耗", cycleCostText, $"（已消耗 {usedText}）", Color.FromArgb(37, 99, 235));
            DrawKeyValueHighlight(g, padX + 16, ref y, "周满额预估", estCostText, "（按当前用量推算满额价值）", Color.FromArgb(5, 150, 105));
        }

        // Codex Local Logs
        var models = _snapshot.Models.Where(m => m.Provider == ProviderKind.Codex).ToList();
        y += 2;
        using var logHeaderBrush = new SolidBrush(Color.FromArgb(71, 85, 105));
        g.DrawString("本地会话日志用量", _boldFont, logHeaderBrush, padX + 8, y);
        y += _boldFont.Height + 3;

        if (models.Count > 0)
        {
            var input = models.Sum(item => item.NonCachedInputTokens);
            var cacheRead = models.Sum(item => item.CachedTokens);
            var cacheCreation = models.Sum(item => item.CacheCreationTokens);
            var output = models.Sum(item => item.OutputTokens);
            var cost = models.Count > 0 && models.All(item => item.ApiEquivalentUsd.HasValue) ? models.Sum(item => item.ApiEquivalentUsd!.Value) : (decimal?)null;
            var costText = cost.HasValue ? "$" + cost.Value.ToString("0.00") : "—";

            using var textBrush = new SolidBrush(Color.FromArgb(71, 85, 105));
            g.DrawString($"Input（未命中）: {FormatTokens(input)}    |    Cache Read: {FormatTokens(cacheRead)}", _regularFont, textBrush, padX + 16, y);
            y += _regularFont.Height + 3;
            g.DrawString($"Cache Creation: {FormatTokens(cacheCreation)}    |    Output: {FormatTokens(output)}", _regularFont, textBrush, padX + 16, y);
            y += _regularFont.Height + 3;

            DrawKeyValueHighlight(g, padX + 16, ref y, "等效 API 金额", costText, $"（范围 {FormatRange(_snapshot.Range)}）", Color.FromArgb(37, 99, 235));
        }
        else
        {
            using var muted = new SolidBrush(Color.FromArgb(148, 163, 184));
            g.DrawString("本次统计范围没有可显示的 Codex token。", _regularFont, muted, padX + 16, y);
            y += _regularFont.Height + 4;
        }

        y += 6;
        g.DrawLine(dividerPen, padX, y, bounds.Width - padX, y);
        y += 8;

        // Footer / Timestamp
        var captured = _snapshot.Quotas.Select(item => item.Snapshot.CapturedAt)
            .Append(_snapshot.RefreshedAt)
            .Max()
            .ToLocalTime();
        using var footerBrush = new SolidBrush(Color.FromArgb(148, 163, 184));
        g.DrawString($"采样时间：{captured:yyyy-MM-dd HH:mm:ss}", _smallFont, footerBrush, padX, y);
    }

    private void DrawSectionHeader(Graphics g, int x, ref int y, string title, string? badge, string? statusBadge, Color dotColor)
    {
        using var dotBrush = new SolidBrush(dotColor);
        g.FillEllipse(dotBrush, x, y + 4, 8, 8);

        using var titleBrush = new SolidBrush(Color.FromArgb(30, 41, 59));
        g.DrawString(title, _sectionFont, titleBrush, x + 14, y);
        var titleWidth = TextRenderer.MeasureText(g, title, _sectionFont).Width;

        var currentX = x + 18 + titleWidth;

        if (!string.IsNullOrWhiteSpace(badge))
        {
            using var badgeBg = new SolidBrush(Color.FromArgb(240, 249, 255));
            using var badgePen = new Pen(Color.FromArgb(186, 230, 253));
            using var badgeText = new SolidBrush(Color.FromArgb(2, 132, 199));
            var text = badge.Trim();
            var size = TextRenderer.MeasureText(g, text, _smallFont);
            var rect = new Rectangle(currentX, y + 1, size.Width + 8, size.Height + 2);
            g.FillRectangle(badgeBg, rect);
            g.DrawRectangle(badgePen, rect);
            g.DrawString(text, _smallFont, badgeText, currentX + 4, y + 2);
            currentX += rect.Width + 6;
        }

        if (!string.IsNullOrWhiteSpace(statusBadge))
        {
            using var badgeBg = new SolidBrush(Color.FromArgb(254, 242, 242));
            using var badgePen = new Pen(Color.FromArgb(254, 202, 202));
            using var badgeText = new SolidBrush(Color.FromArgb(220, 38, 38));
            var text = statusBadge.Trim();
            var size = TextRenderer.MeasureText(g, text, _smallFont);
            var rect = new Rectangle(currentX, y + 1, size.Width + 8, size.Height + 2);
            g.FillRectangle(badgeBg, rect);
            g.DrawRectangle(badgePen, rect);
            g.DrawString(text, _smallFont, badgeText, currentX + 4, y + 2);
        }

        y += _sectionFont.Height + 5;
    }

    private void DrawQuotaWindowLine(Graphics g, int x, ref int y, string windowLabel, IReadOnlyList<QuotaSnapshot> snapshots)
    {
        if (snapshots.Count == 0) return;
        if (snapshots.Count == 1)
        {
            var snapshot = snapshots[0];
            using var labelBrush = new SolidBrush(Color.FromArgb(71, 85, 105));
            var labelText = $"{windowLabel}：";
            g.DrawString(labelText, _boldFont, labelBrush, x, y);
            var labelWidth = TextRenderer.MeasureText(g, labelText, _boldFont).Width;
            var currentX = x + labelWidth;

            var modelLabel = ShortLabel(snapshot);
            if (!string.IsNullOrWhiteSpace(modelLabel) &&
                !modelLabel.StartsWith("Codex", StringComparison.OrdinalIgnoreCase))
            {
                using var modelBrush = new SolidBrush(Color.FromArgb(51, 65, 85));
                g.DrawString($"{modelLabel} ", _regularFont, modelBrush, currentX, y);
                currentX += TextRenderer.MeasureText(g, $"{modelLabel} ", _regularFont).Width;
            }

            var (remText, remColor) = GetRemainingTextAndColor(snapshot);
            using var remBrush = new SolidBrush(remColor);
            g.DrawString(remText, _boldFont, remBrush, currentX, y);
            currentX += TextRenderer.MeasureText(g, remText, _boldFont).Width;

            var resetText = FormatReset(snapshot);
            using var resetBrush = new SolidBrush(Color.FromArgb(100, 116, 139));
            g.DrawString($"  （重置 {resetText}）", _regularFont, resetBrush, currentX, y);

            y += _regularFont.Height + 4;
            return;
        }

        // Multiple snapshots for this window (e.g. Gemini Models & Claude and GPT models)
        using var windowLabelBrush = new SolidBrush(Color.FromArgb(71, 85, 105));
        g.DrawString($"{windowLabel}：", _boldFont, windowLabelBrush, x, y);
        y += _boldFont.Height + 2;

        foreach (var snapshot in snapshots)
        {
            var currentX = x + 12;
            using var bulletBrush = new SolidBrush(Color.FromArgb(148, 163, 184));
            g.DrawString("• ", _regularFont, bulletBrush, currentX, y);
            currentX += TextRenderer.MeasureText(g, "• ", _regularFont).Width;

            var modelLabel = ShortLabel(snapshot);
            if (!string.IsNullOrWhiteSpace(modelLabel))
            {
                using var modelBrush = new SolidBrush(Color.FromArgb(51, 65, 85));
                g.DrawString($"{modelLabel}：", _regularFont, modelBrush, currentX, y);
                currentX += TextRenderer.MeasureText(g, $"{modelLabel}：", _regularFont).Width;
            }

            var (remText, remColor) = GetRemainingTextAndColor(snapshot);
            using var remBrush = new SolidBrush(remColor);
            g.DrawString(remText, _boldFont, remBrush, currentX, y);
            currentX += TextRenderer.MeasureText(g, remText, _boldFont).Width;

            var resetText = FormatReset(snapshot);
            using var resetBrush = new SolidBrush(Color.FromArgb(100, 116, 139));
            g.DrawString($"  （重置 {resetText}）", _regularFont, resetBrush, currentX, y);

            y += _regularFont.Height + 3;
        }
        y += 1;
    }

    private void DrawKeyValueHighlight(Graphics g, int x, ref int y, string key, string highlightValue, string note, Color highlightColor)
    {
        using var keyBrush = new SolidBrush(Color.FromArgb(71, 85, 105));
        var keyText = $"{key}：";
        g.DrawString(keyText, _regularFont, keyBrush, x, y);
        var keyWidth = TextRenderer.MeasureText(g, keyText, _regularFont).Width;

        var currentX = x + keyWidth;

        using var valBrush = new SolidBrush(highlightColor);
        g.DrawString(highlightValue, _boldFont, valBrush, currentX, y);
        var valWidth = TextRenderer.MeasureText(g, highlightValue, _boldFont).Width;

        currentX += valWidth;

        if (!string.IsNullOrWhiteSpace(note))
        {
            using var noteBrush = new SolidBrush(Color.FromArgb(100, 116, 139));
            g.DrawString($"  {note}", _regularFont, noteBrush, currentX, y);
        }

        y += _regularFont.Height + 4;
    }

    private static (string Text, Color Color) GetRemainingTextAndColor(QuotaSnapshot snapshot)
    {
        if (!snapshot.RemainingFraction.HasValue) return ("剩余未知", Color.FromArgb(100, 116, 139));
        var frac = snapshot.RemainingFraction.Value;
        var text = $"{frac:P0} 剩余";
        var color = frac switch
        {
            > 0.30 => Color.FromArgb(22, 163, 74),  // Green
            > 0.10 => Color.FromArgb(217, 119, 6),  // Amber
            _ => Color.FromArgb(220, 38, 38)        // Red
        };
        return (text, color);
    }

    public void ShowAt(Point cursor)
    {
        var screen = Screen.FromPoint(cursor).WorkingArea;
        var x = Math.Clamp(cursor.X - Width + 12, screen.Left + 4, screen.Right - Width - 4);
        var y = Math.Clamp(cursor.Y - Height - 12, screen.Top + 4, screen.Bottom - Height - 4);
        Location = new Point(x, y);
        if (!Visible) Show();
        else BringToFront();
        TopMost = true;
    }

    protected override bool ShowWithoutActivation => true;

    private static string FormatReset(QuotaSnapshot snapshot) => snapshot.ResetAt.HasValue
        ? snapshot.ResetAt.Value.ToLocalTime().ToString("MM-dd HH:mm")
        : "未知";

    private static string ShortLabel(QuotaSnapshot snapshot) => string.IsNullOrWhiteSpace(snapshot.DisplayLabel) ||
        string.Equals(snapshot.DisplayLabel, snapshot.ModelOrPoolId, StringComparison.OrdinalIgnoreCase)
        ? snapshot.ModelOrPoolId
        : snapshot.DisplayLabel;

    private static string FormatTokens(long value) => value switch
    {
        >= 1_000_000 => $"{value / 1_000_000d:0.##}M",
        >= 1_000 => $"{value / 1_000d:0.##}K",
        _ => value.ToString("N0")
    };

    private static string FormatRange(DateRange range) => range.From == range.To
        ? range.From.ToString("yyyy-MM-dd")
        : $"{range.From:yyyy-MM-dd} 至 {range.To:yyyy-MM-dd}";

    private static bool IsFiveHour(QuotaSnapshot snapshot)
    {
        var value = $"{snapshot.WindowKind} {snapshot.ModelOrPoolId} {snapshot.DisplayLabel}".ToLowerInvariant();
        return value.Contains("5h", StringComparison.Ordinal) || value.Contains("5 h", StringComparison.Ordinal) ||
            value.Contains("5-hour", StringComparison.Ordinal) || value.Contains("5 hour", StringComparison.Ordinal) ||
            value.Contains("five_hour", StringComparison.Ordinal) || value.Contains("five hour", StringComparison.Ordinal) ||
            value.Contains("5小时", StringComparison.Ordinal);
    }

    private static bool IsWeekly(QuotaSnapshot snapshot)
    {
        var value = $"{snapshot.WindowKind} {snapshot.ModelOrPoolId} {snapshot.DisplayLabel}".ToLowerInvariant();
        return value.Contains("week", StringComparison.Ordinal) || value.Contains("weekly", StringComparison.Ordinal) ||
            value.Contains("7-day", StringComparison.Ordinal) || value.Contains("7 day", StringComparison.Ordinal) ||
            value.Contains("7d", StringComparison.Ordinal) || value.Contains("周", StringComparison.Ordinal);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _titleFont.Dispose();
            _sectionFont.Dispose();
            _boldFont.Dispose();
            _regularFont.Dispose();
            _smallFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
