using System.Drawing.Drawing2D;
using UsageTray.Core;
using UsageTray.Providers.Antigravity;
using UsageTray.Services;

namespace UsageTray.UI.Controls;

public sealed class QuotaSummaryControl : UserControl
{
    private DashboardSnapshot? _snapshot;
    private readonly Font _titleFont;
    private readonly Font _sectionFont;
    private readonly Font _boldFont;
    private readonly Font _regularFont;
    private readonly Font _smallFont;

    public bool ShowDismissHint { get; set; }
    public bool ShowHeader { get; set; } = true;
    public bool ShowFooterNote { get; set; } = true;
    public bool IsLocked { get; set; }

    public QuotaSummaryControl()
    {
        _titleFont = new Font("Segoe UI", 10F, FontStyle.Bold, GraphicsUnit.Point);
        _sectionFont = new Font("Segoe UI", 9F, FontStyle.Bold, GraphicsUnit.Point);
        _boldFont = new Font("Segoe UI", 9F, FontStyle.Bold, GraphicsUnit.Point);
        _regularFont = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        _smallFont = new Font("Segoe UI", 8F, FontStyle.Regular, GraphicsUnit.Point);

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        BackColor = Color.White;
        DoubleBuffered = true;
        AutoScroll = true;
    }

    public void SetSnapshot(DashboardSnapshot? snapshot)
    {
        _snapshot = snapshot;
        RecalculateContentSize();
        Invalidate();
    }

    public int MeasureHeight(int width)
    {
        using var g = CreateGraphics();
        return MeasureContentHeight(g, width);
    }

    private void RecalculateContentSize()
    {
        if (_titleFont is null || IsDisposed) return;
        using var g = CreateGraphics();
        var width = Math.Max(ClientSize.Width, (int)Math.Round(560f * DeviceDpi / 96f));
        var height = MeasureContentHeight(g, width);
        AutoScrollMinSize = new Size(width - 20, height);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_titleFont is null || IsDisposed) return;
        RecalculateContentSize();
        Invalidate();
    }

    private int MeasureContentHeight(Graphics g, int width)
    {
        if (_titleFont is null || _regularFont is null) return 60;
        var y = 14;
        if (ShowHeader)
        {
            y += _titleFont.Height + 10;
            y += 1; // Divider
            y += 8;
        }

        if (_snapshot is null)
        {
            y += _regularFont.Height + 20;
            return y;
        }

        // Section 1: Antigravity
        y += _sectionFont.Height + 6;
        var rawAgSnapshots = _snapshot.Quotas
            .Where(q => q.Snapshot.Provider == ProviderKind.Antigravity)
            .Select(q => q.Snapshot).ToList();
        var agSnapshots = DeduplicateAntigravityQuotas(rawAgSnapshots);
        if (agSnapshots.Count == 0)
        {
            y += _regularFont.Height + 4;
        }
        else
        {
            var fiveHour = agSnapshots.Where(IsFiveHour).ToList();
            if (fiveHour.Count > 0) y += MeasureQuotaWindowHeight(fiveHour);
            var weekly = agSnapshots.Where(IsWeekly).ToList();
            if (weekly.Count > 0) y += MeasureQuotaWindowHeight(weekly);
            var others = agSnapshots.Where(s => !IsFiveHour(s) && !IsWeekly(s)).ToList();
            if (others.Count > 0) y += MeasureQuotaWindowHeight(others);
        }

        // Antigravity Weekly Cycle Estimates
        var agWeeklyEstimates = (_snapshot.AntigravityEstimates?
            .Where(e => e.WindowKind == "weekly")
            .ToList() ?? []).ToList();

        if (agWeeklyEstimates.Count == 0)
        {
            var weeklySnaps = agSnapshots.Where(IsWeekly).ToList();
            if (weeklySnaps.Count > 0)
            {
                agWeeklyEstimates = weeklySnaps.Select(w =>
                {
                    var isGemini = (w.DisplayLabel?.Contains("Gemini", StringComparison.OrdinalIgnoreCase) == true) || (w.ModelOrPoolId?.Contains("Gemini", StringComparison.OrdinalIgnoreCase) == true);
                    var name = isGemini ? "Gemini Models" : "Claude and GPT models";
                    var rem = w.EffectiveRemainingFraction();
                    var used = rem.HasValue ? Math.Clamp(1.0 - rem.Value, 0.0, 1.0) : (double?)null;
                    return new AntigravityQuotaEstimate(w.ModelOrPoolId ?? string.Empty, "weekly", name, rem, w.ResetAt, null, used, null, QuotaEstimateConfidence.Low, 0, null, null, null, null);
                }).ToList();
            }
        }

        if (agWeeklyEstimates.Count > 0)
        {
            y += 2;
            y += _boldFont.Height + 3; // "周订阅统计与预估 (本轮 7 天窗口)"
            foreach (var _ in agWeeklyEstimates)
            {
                y += _regularFont.Height + 3; // 本轮参考金额
                y += _regularFont.Height + 4; // 满额样本外推
            }
        }

        y += 6;
        y += 1; // Divider
        y += 8;

        // Section 2: Codex
        y += _sectionFont.Height + 6;
        var codexSnapshots = _snapshot.Quotas
            .Where(q => q.Snapshot.Provider == ProviderKind.Codex)
            .Select(q => q.Snapshot).ToList();
        var codexPlan = codexSnapshots
            .Where(s => !UsageAggregator.IsReserveSnapshot(s) && !string.IsNullOrWhiteSpace(s.PlanTier))
            .OrderByDescending(s => s.CapturedAt)
            .Select(s => s.PlanTier)
            .FirstOrDefault()
            ?? codexSnapshots
            .Where(s => !string.IsNullOrWhiteSpace(s.PlanTier))
            .OrderByDescending(s => s.CapturedAt)
            .Select(s => s.PlanTier)
            .FirstOrDefault();

        if (codexSnapshots.Count > 0)
        {
            var fiveHour = codexSnapshots.Where(IsFiveHour).ToList();
            if (fiveHour.Count > 0) y += MeasureQuotaWindowHeight(fiveHour);
            var weekly = codexSnapshots.Where(IsWeekly).ToList();
            if (weekly.Count > 0) y += MeasureQuotaWindowHeight(weekly);
            var others = codexSnapshots.Where(s => !IsFiveHour(s) && !IsWeekly(s)).ToList();
            if (others.Count > 0) y += MeasureQuotaWindowHeight(others);
            var activeSnapshots = codexSnapshots;
            if (activeSnapshots.Any(s => s.IsResetPassed())) y += _smallFont.Height + 4;
        }
        else
        {
            y += _regularFont.Height + 4;
        }


        // Codex Weekly Cycle & Projection
        var codexCycles = _snapshot.CodexWeeklyCycles.Count > 0
            ? _snapshot.CodexWeeklyCycles
            : _snapshot.CodexWeeklyCycle != null
                ? [_snapshot.CodexWeeklyCycle]
                : [];

        if (codexCycles.Count > 0)
        {
            y += 2;
            y += _boldFont.Height + 3; // "周订阅统计与预估 (本轮 7 天窗口)"
            foreach (var _ in codexCycles)
            {
                y += _regularFont.Height + 3; // 本轮参考金额
                y += _regularFont.Height + 4; // 满额样本外推
            }
        }

        y += 6;
        y += 1; // Divider
        y += 8;

        // Footer / Timestamp
        y += _smallFont.Height + 6;

        if (ShowFooterNote)
        {
            y += (_smallFont.Height + 3) * 2 + 6;
        }

        y += 10;
        return y;
    }

    private int MeasureQuotaWindowHeight(IReadOnlyList<QuotaSnapshot> snapshots)
    {
        if (snapshots.Count == 0) return 0;
        return _boldFont.Height + 2 + (_regularFont.Height + _smallFont.Height + 7) * snapshots.Count + 1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_titleFont is null || _regularFont is null || _smallFont is null) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // Apply scroll offset
        g.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);

        var bounds = ClientRectangle;
        var width = Math.Max(bounds.Width, AutoScrollMinSize.Width);
        var padX = (int)Math.Round(16f * DeviceDpi / 96f);
        var y = (int)Math.Round(12f * DeviceDpi / 96f);

        using var titleBrush = new SolidBrush(Color.FromArgb(15, 23, 42));
        using var hintBrush = new SolidBrush(Color.FromArgb(148, 163, 184));
        using var dividerPen = new Pen(Color.FromArgb(226, 232, 240));

        // Header
        if (ShowHeader)
        {
            g.DrawString("额度与用量摘要", _titleFont, titleBrush, padX, y);
            if (ShowDismissHint)
            {
                var closeText = IsLocked ? "已拖动锁定 (点击面板关闭)" : "点击任意位置关闭 (支持拖动)";
                var closeSize = TextRenderer.MeasureText(g, closeText, _smallFont);
                g.DrawString(closeText, _smallFont, hintBrush, width - padX - closeSize.Width, y + (_titleFont.Height - _smallFont.Height) / 2);
            }

            y += _titleFont.Height + 8;
            g.DrawLine(dividerPen, padX, y, width - padX, y);
            y += 8;
        }

        if (_snapshot is null)
        {
            using var muted = new SolidBrush(Color.FromArgb(100, 116, 139));
            g.DrawString("暂无额度快照，请先刷新。", _regularFont, muted, padX, y);
            return;
        }

        // Section 1: Antigravity
        var agViews = _snapshot.Quotas.Where(q => q.Snapshot.Provider == ProviderKind.Antigravity).ToList();
        var rawAgSnapshots = agViews.Select(q => q.Snapshot).ToList();
        var agSnapshots = DeduplicateAntigravityQuotas(rawAgSnapshots);
        var agPlan = agSnapshots
            .Where(s => !string.IsNullOrWhiteSpace(s.PlanTier))
            .OrderByDescending(s => s.CapturedAt)
            .Select(s => s.PlanTier)
            .FirstOrDefault();
        var agOffline = agViews.Count > 0 && agViews.All(v => v.IsOffline);

        DrawSectionHeader(g, padX, ref y, "Antigravity 额度与周订阅", agPlan, agOffline ? "离线" : null, Color.FromArgb(59, 130, 246));

        if (agSnapshots.Count == 0)
        {
            using var muted = new SolidBrush(Color.FromArgb(148, 163, 184));
            g.DrawString("暂无可用快照，请确保 Antigravity 正在运行并刷新。", _regularFont, muted, padX + 8, y);
            y += _regularFont.Height + 4;
        }
        else
        {
            var fiveHour = agSnapshots.Where(IsFiveHour)
                .OrderBy(s => (s.DisplayLabel?.Contains("gemini", StringComparison.OrdinalIgnoreCase) == true) || (s.ModelOrPoolId?.Contains("gemini", StringComparison.OrdinalIgnoreCase) == true) ? 0 : 1)
                .ToList();
            if (fiveHour.Count > 0) DrawQuotaWindowLine(g, padX + 8, ref y, "5 小时窗口", fiveHour);

            var weekly = agSnapshots.Where(IsWeekly)
                .OrderBy(s => (s.DisplayLabel?.Contains("gemini", StringComparison.OrdinalIgnoreCase) == true) || (s.ModelOrPoolId?.Contains("gemini", StringComparison.OrdinalIgnoreCase) == true) ? 0 : 1)
                .ToList();
            if (weekly.Count > 0) DrawQuotaWindowLine(g, padX + 8, ref y, "周窗口", weekly);

            var others = agSnapshots.Where(s => !IsFiveHour(s) && !IsWeekly(s))
                .OrderBy(s => (s.DisplayLabel?.Contains("gemini", StringComparison.OrdinalIgnoreCase) == true) || (s.ModelOrPoolId?.Contains("gemini", StringComparison.OrdinalIgnoreCase) == true) ? 0 : 1)
                .ToList();
            if (others.Count > 0) DrawQuotaWindowLine(g, padX + 8, ref y, "其他窗口", others);
        }

        // Antigravity Weekly Cycle Estimates (Gemini first, then Claude/GPT)
        var agWeeklyEstimates = (_snapshot.AntigravityEstimates?
            .Where(e => e.WindowKind == "weekly")
            .ToList() ?? []).ToList();

        if (agWeeklyEstimates.Count == 0)
        {
            var weeklySnaps = agSnapshots.Where(IsWeekly).ToList();
            if (weeklySnaps.Count > 0)
            {
                agWeeklyEstimates = weeklySnaps.Select(w =>
                {
                    var isGemini = (w.DisplayLabel?.Contains("Gemini", StringComparison.OrdinalIgnoreCase) == true) || (w.ModelOrPoolId?.Contains("Gemini", StringComparison.OrdinalIgnoreCase) == true);
                    var name = isGemini ? "Gemini Models" : "Claude and GPT models";
                    var rem = w.EffectiveRemainingFraction();
                    var used = rem.HasValue ? Math.Clamp(1.0 - rem.Value, 0.0, 1.0) : (double?)null;
                    return new AntigravityQuotaEstimate(w.ModelOrPoolId ?? string.Empty, "weekly", name, rem, w.ResetAt, null, used, null, QuotaEstimateConfidence.Low, 0, null, null, null, null);
                }).ToList();
            }
        }

        agWeeklyEstimates = agWeeklyEstimates
            .OrderBy(e => (e.DisplayName?.Contains("Gemini", StringComparison.OrdinalIgnoreCase) == true) ? 0 : 1)
            .ToList();

        if (agWeeklyEstimates.Count > 0)
        {
            y += 2;
            using var estHeaderBrush = new SolidBrush(Color.FromArgb(71, 85, 105));
            g.DrawString("周订阅统计与预估 (本轮 7 天窗口)", _boldFont, estHeaderBrush, padX + 8, y);
            y += _boldFont.Height + 3;

            foreach (var est in agWeeklyEstimates)
            {
                var usedText = est.ConsumedFraction.HasValue ? $"{est.ConsumedFraction.Value:P0}" : (est.RemainingFraction.HasValue ? $"{1.0 - est.RemainingFraction.Value:P0}" : "未知");
                var cycleCostText = est.ObservedCostUsd.HasValue ? "$" + est.ObservedCostUsd.Value.ToString("0.00") : "—";
                var estCostText = est.EstimatedFullQuotaUsd.HasValue ? $"约 ${est.EstimatedFullQuotaUsd.Value:0.00}" : est.CalculationDetails ?? "样本不足";
                var resetNote = est.ResetAt.HasValue ? $"（重置 {TimeFormatter.FormatResetWithRelative(est.ResetAt.Value)}）" : string.Empty;

                DrawKeyValueHighlight(g, padX + 16, ref y, $"{est.DisplayName} 本轮参考金额", cycleCostText, $"（已消耗 {usedText}）", Color.FromArgb(37, 99, 235));
                DrawKeyValueHighlight(g, padX + 16, ref y, $"{est.DisplayName} 满额样本外推", estCostText, est.EstimatedFullQuotaUsd.HasValue ? "（仅本机样本）" : string.Empty, Color.FromArgb(5, 150, 105));
            }
        }

        y += 6;
        g.DrawLine(dividerPen, padX, y, width - padX, y);
        y += 8;

        // Section 2: Codex
        var codexViews = _snapshot.Quotas.Where(q => q.Snapshot.Provider == ProviderKind.Codex).ToList();
        var codexSnapshots = codexViews.Select(q => q.Snapshot).ToList();
        var codexPlan = codexSnapshots
            .Where(s => !UsageAggregator.IsReserveSnapshot(s) && !string.IsNullOrWhiteSpace(s.PlanTier))
            .OrderByDescending(s => s.CapturedAt)
            .Select(s => s.PlanTier)
            .FirstOrDefault()
            ?? codexSnapshots
            .Where(s => !string.IsNullOrWhiteSpace(s.PlanTier))
            .OrderByDescending(s => s.CapturedAt)
            .Select(s => s.PlanTier)
            .FirstOrDefault();

        DrawSectionHeader(g, padX, ref y, "Codex 额度与周订阅", codexPlan, null, Color.FromArgb(16, 185, 129));

        if (codexSnapshots.Count > 0)
        {
            var fiveHour = codexSnapshots.Where(IsFiveHour).ToList();
            if (fiveHour.Count > 0) DrawQuotaWindowLine(g, padX + 8, ref y, "5 小时窗口", fiveHour);
            var weekly = codexSnapshots.Where(IsWeekly).ToList();
            if (weekly.Count > 0) DrawQuotaWindowLine(g, padX + 8, ref y, "周窗口", weekly);
            var others = codexSnapshots.Where(s => !IsFiveHour(s) && !IsWeekly(s)).ToList();
            if (others.Count > 0) DrawQuotaWindowLine(g, padX + 8, ref y, "其他窗口", others);

            var activeSnapshots = codexSnapshots;
            if (activeSnapshots.Any(s => s.IsResetPassed()))
            {
                using var tipBrush = new SolidBrush(Color.FromArgb(100, 116, 139));
                g.DrawString("部分窗口已过期；使用对应模型后等待新快照。", _smallFont, tipBrush, padX + 8, y);
                y += _smallFont.Height + 4;
            }
        }
        else
        {
            using var muted = new SolidBrush(Color.FromArgb(100, 116, 139));
            g.DrawString("暂无可用快照（当前 session 未写入 rate_limits）", _regularFont, muted, padX + 8, y);
            y += _regularFont.Height + 4;
        }

        // Codex Weekly Cycle & Projection
        var codexCycles = _snapshot.CodexWeeklyCycles.Count > 0
            ? _snapshot.CodexWeeklyCycles
            : _snapshot.CodexWeeklyCycle != null
                ? [_snapshot.CodexWeeklyCycle]
                : [];

        if (codexCycles.Count > 0)
        {
            y += 2;
            using var subHeaderBrush = new SolidBrush(Color.FromArgb(71, 85, 105));
            g.DrawString("周订阅统计与预估 (本轮 7 天窗口)", _boldFont, subHeaderBrush, padX + 8, y);
            y += _boldFont.Height + 3;

            foreach (var cycle in codexCycles)
            {
                var usedText = cycle.UsedFraction.HasValue ? $"{cycle.UsedFraction.Value:P0}" : "未知";
                var cycleCostText = cycle.CycleCostUsd.HasValue ? "$" + cycle.CycleCostUsd.Value.ToString("0.00") : "—";
                var estCostText = cycle.EstimatedWeeklyCostUsd.HasValue ? $"约 ${cycle.EstimatedWeeklyCostUsd.Value:0.00}" : cycle.EstimateNote ?? "样本不足";
                var codexResetNote = cycle.ResetAt.HasValue ? $"（重置 {TimeFormatter.FormatResetWithRelative(cycle.ResetAt.Value)}）" : string.Empty;
                var poolPrefix = codexCycles.Count > 1 ? $"{cycle.PoolName} " : string.Empty;

                DrawKeyValueHighlight(g, padX + 16, ref y, $"{poolPrefix}本轮参考金额", cycleCostText, $"（已消耗 {usedText}）", Color.FromArgb(37, 99, 235));
                DrawKeyValueHighlight(g, padX + 16, ref y, $"{poolPrefix}满额样本外推", estCostText, cycle.EstimatedWeeklyCostUsd.HasValue ? "（仅本机样本）" : string.Empty, Color.FromArgb(5, 150, 105));
            }
        }

        y += 6;
        g.DrawLine(dividerPen, padX, y, width - padX, y);
        y += 8;

        // Footer / Timestamp
        using var footerBrush = new SolidBrush(Color.FromArgb(148, 163, 184));
        g.DrawString($"界面刷新：{_snapshot.RefreshedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}", _smallFont, footerBrush, padX, y);
        y += _smallFont.Height + 6;

        if (ShowFooterNote)
        {
            using var noteBrush = new SolidBrush(Color.FromArgb(160, 174, 192));
            g.DrawString("• 订阅参考金额按固定基准计量，并非账单或可用余额。", _smallFont, noteBrush, padX, y);
            y += _smallFont.Height + 3;
            g.DrawString("• 满额金额仅按同周期本机样本外推；过期额度等待新快照。", _smallFont, noteBrush, padX, y);
        }


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
        using var labelBrush = new SolidBrush(Color.FromArgb(71, 85, 105));
        using var detailBrush = new SolidBrush(Color.FromArgb(100, 116, 139));
        g.DrawString($"{windowLabel}：", _boldFont, labelBrush, x, y);
        y += _boldFont.Height + 2;
        foreach (var snapshot in snapshots)
        {
            var label = $"{ShortLabel(snapshot)}  ";
            g.DrawString(label, _regularFont, labelBrush, x + 12, y);
            var (text, color) = GetRemainingTextAndColor(snapshot);
            using var brush = new SolidBrush(color);
            g.DrawString(text, _boldFont, brush, x + 12 + TextRenderer.MeasureText(g, label, _regularFont).Width, y);
            y += _regularFont.Height + 3;
            var detail = $"采样 {snapshot.CapturedAt.ToLocalTime():MM-dd HH:mm} · 重置 {FormatReset(snapshot)}";
            g.DrawString(detail, _smallFont, detailBrush, x + 12, y);
            y += _smallFont.Height + 4;
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
        if (snapshot.IsResetPassed())
        {
            return ($"待同步（上次 {frac:P0}）", Color.FromArgb(100, 116, 139));
        }
        var text = $"{frac:P0} 剩余{(snapshot.IsStale() ? "（旧快照）" : "")}";
        var color = frac switch
        {
            > 0.30 => Color.FromArgb(22, 163, 74),  // Green
            > 0.10 => Color.FromArgb(217, 119, 6),  // Amber
            _ => Color.FromArgb(220, 38, 38)        // Red
        };
        return (text, color);
    }



    private static string FormatReset(QuotaSnapshot snapshot) => TimeFormatter.FormatResetWithRelative(snapshot.ResetAt);

    private static string ShortLabel(QuotaSnapshot snapshot)
    {
        var label = string.IsNullOrWhiteSpace(snapshot.DisplayLabel) ||
            string.Equals(snapshot.DisplayLabel, snapshot.ModelOrPoolId, StringComparison.OrdinalIgnoreCase)
            ? snapshot.ModelOrPoolId
            : snapshot.DisplayLabel;
        return label
            .Replace("(5小时额度)", "")
            .Replace("(周额度)", "")
            .Replace("(5h)", "")
            .Replace("(weekly)", "")
            .Trim();
    }

    private static bool IsFiveHour(QuotaSnapshot snapshot)
    {
        var value = $"{snapshot.WindowKind} {snapshot.ModelOrPoolId} {snapshot.DisplayLabel}".ToLowerInvariant();
        return value.Contains("5h", StringComparison.Ordinal) || value.Contains("5 h", StringComparison.Ordinal) ||
            value.Contains("5-hour", StringComparison.Ordinal) || value.Contains("5 hour", StringComparison.Ordinal) ||
            value.Contains("five_hour", StringComparison.Ordinal) || value.Contains("five hour", StringComparison.Ordinal) ||
            value.Contains("5小时", StringComparison.Ordinal);
    }

    private static IReadOnlyList<QuotaSnapshot> DeduplicateAntigravityQuotas(IEnumerable<QuotaSnapshot> snapshots)
    {
        var list = snapshots.ToList();
        var hasSpecific = list.Any(s => (s.DisplayLabel?.Contains("Gemini", StringComparison.OrdinalIgnoreCase) == true) ||
                                        (s.DisplayLabel?.Contains("Claude", StringComparison.OrdinalIgnoreCase) == true) ||
                                        (s.DisplayLabel?.Contains("GPT", StringComparison.OrdinalIgnoreCase) == true));
        if (hasSpecific)
        {
            list = list.Where(s => !string.Equals(s.ModelOrPoolId, "Five Hour Limit Remaining", StringComparison.OrdinalIgnoreCase) &&
                                   !string.Equals(s.ModelOrPoolId, "Weekly Limit Remaining", StringComparison.OrdinalIgnoreCase) &&
                                   !string.Equals(s.DisplayLabel, "Five Hour Limit Remaining", StringComparison.OrdinalIgnoreCase) &&
                                   !string.Equals(s.DisplayLabel, "Weekly Limit Remaining", StringComparison.OrdinalIgnoreCase)).ToList();
        }
        return list;
    }

    private static bool IsWeekly(QuotaSnapshot snapshot)
    {
        var value = $"{snapshot.WindowKind} {snapshot.ModelOrPoolId} {snapshot.DisplayLabel}".ToLowerInvariant();
        return value.Contains("week", StringComparison.Ordinal) || value.Contains("weekly", StringComparison.Ordinal) ||
            value.Contains("7-day", StringComparison.Ordinal) || value.Contains("7 day", StringComparison.Ordinal) ||
            value.Contains("7d", StringComparison.Ordinal) || value.Contains("周", StringComparison.Ordinal);
    }

    public static bool IsProOrAbovePlan(string? planTier) => UsageAggregator.IsProOrAbovePlan(planTier);

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
