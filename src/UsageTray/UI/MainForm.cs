using UsageTray.App;
using UsageTray.Core;
using UsageTray.Services;
using UsageTray.UI.Controls;

namespace UsageTray.UI;

public sealed class MainForm : Form
{
    private readonly RefreshCoordinator _coordinator;
    private readonly AppSettingsStore _settingsStore;
    private readonly ComboBox _providerCombo;
    private readonly ComboBox _rangeCombo;
    private readonly Label _codexApiValue;
    private readonly Label _codexCycleNote;
    private readonly Label _antigravityApiValue;
    private readonly Label _antigravityCycleNote;
    private readonly Label _inputValue;

    private readonly Label _cachedValue;
    private readonly Label _outputValue;
    private readonly Label _uncachedInputValue;
    private readonly Label _cacheCreationValue;
    private readonly Label _cacheHitRateValue;
    private readonly Label _speedEstimateValue;
    private readonly Label _status;
    private readonly ToolTip _toolTip;
    private readonly DataGridView _models;
    private readonly DataGridView _projects;
    private readonly DailyBarChartControl _chart;
    private readonly QuotaSummaryControl _quotaControl;
    private DateRange _customRange;
    private int _previousRangeIndex;
    private bool _ignoreRangeSelection;

    public MainForm(RefreshCoordinator coordinator, AppSettingsStore settingsStore)
    {
        _coordinator = coordinator;
        _settingsStore = settingsStore;
        _customRange = DateRange.LastDays(7);
        _previousRangeIndex = 4;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font("Segoe UI", 9F);
        Icon = AppIcon.Create();
        Text = "AI Usage Tray";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(780, 560);
        MinimumSize = new Size(740, 500);
        FormClosing += (_, e) => { e.Cancel = true; Hide(); };

        var textHeight = TextRenderer.MeasureText("刷新", Font).Height;
        var buttonHeight = Math.Max(34, textHeight + 12);
        var topHeight = buttonHeight + 18;
        var controlVerticalOffset = Math.Max(0, (buttonHeight - textHeight) / 2);

        _providerCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = Math.Max(95, TextRenderer.MeasureText("Antigravity", Font).Width + 36),
            Height = buttonHeight,
            Margin = new Padding(0, 0, 8, 0)
        };
        _providerCombo.Items.AddRange(["全部", "Codex", "Antigravity"]);
        _providerCombo.SelectedIndex = 0;
        _rangeCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = Math.Max(130, TextRenderer.MeasureText("本次周额度", Font).Width + 36),
            Height = buttonHeight,
            Margin = new Padding(0, 0, 8, 0)
        };
        _rangeCombo.Items.AddRange(["今天", "7天", "30天", "本月", "本次周额度", "全部", "自定义…"]);
        _rangeCombo.SelectedIndex = 4;

        var refresh = new Button
        {
            Text = "刷新",
            Width = Math.Max(64, TextRenderer.MeasureText("刷新", Font).Width + 24),
            Height = buttonHeight,
            Margin = new Padding(0, 0, 6, 0),
            AutoSize = false,
            Padding = new Padding(8, 2, 8, 2)
        };
        refresh.Click += async (_, _) => await RefreshViewAsync(false);
        var settingsButton = new Button
        {
            Text = "设置",
            Width = Math.Max(64, TextRenderer.MeasureText("设置", Font).Width + 24),
            Height = buttonHeight,
            Margin = new Padding(0),
            AutoSize = false,
            Padding = new Padding(8, 2, 8, 2)
        };
        settingsButton.Click += (_, _) =>
        {
            using var form = new SettingsForm(_settingsStore, _coordinator);
            form.ShowDialog(this);
        };
        _toolTip = new ToolTip();
        _toolTip.SetToolTip(refresh, "增量刷新：仅重新解析新增或发生变化的本地记录。程序启动时会自动全量读取一次；全量重读请到设置中执行。");
        _toolTip.SetToolTip(_rangeCombo, "选择自定义…后填写开始日期和结束日期，按本地日历统计。");
        _providerCombo.SelectedIndexChanged += (_, _) => ApplyCurrentSelection();
        _rangeCombo.SelectedIndexChanged += (_, _) => HandleRangeSelectionChanged();

        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 9, 12, 9),
            WrapContents = false,
            AutoScroll = false,
            FlowDirection = FlowDirection.LeftToRight
        };
        top.Controls.AddRange([
            FieldLabel("Provider", controlVerticalOffset), _providerCombo,
            FieldLabel("日期", controlVerticalOffset), _rangeCombo,
            refresh, settingsButton
        ]);

        var noteFont = new Font(Font.FontFamily, 8.5F, FontStyle.Regular);

        _codexApiValue = new Label
        {
            Text = "Codex: —",
            Dock = DockStyle.Fill,
            AutoSize = false,
            AutoEllipsis = true,
            Font = new Font(Font.FontFamily, 10.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(16, 120, 90),
            TextAlign = ContentAlignment.MiddleLeft
        };
        _codexCycleNote = new Label
        {
            Text = "周期：—",
            Dock = DockStyle.Fill,
            AutoSize = false,
            AutoEllipsis = true,
            Font = noteFont,
            ForeColor = Color.DimGray,
            TextAlign = ContentAlignment.MiddleLeft
        };
        _antigravityApiValue = new Label
        {
            Text = "Antigravity: —",
            Dock = DockStyle.Fill,
            AutoSize = false,
            AutoEllipsis = true,
            Font = new Font(Font.FontFamily, 10.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(30, 70, 160),
            TextAlign = ContentAlignment.MiddleLeft
        };
        _antigravityCycleNote = new Label
        {
            Text = "周期：—",
            Dock = DockStyle.Fill,
            AutoSize = false,
            AutoEllipsis = true,
            Font = noteFont,
            ForeColor = Color.DimGray,
            TextAlign = ContentAlignment.MiddleLeft
        };

        var apiValuePanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        apiValuePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 28));
        apiValuePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 22));
        apiValuePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 28));
        apiValuePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 22));
        apiValuePanel.Controls.Add(_codexApiValue, 0, 0);
        apiValuePanel.Controls.Add(_codexCycleNote, 0, 1);
        apiValuePanel.Controls.Add(_antigravityApiValue, 0, 2);
        apiValuePanel.Controls.Add(_antigravityCycleNote, 0, 3);

        _inputValue = MetricLabel();
        _cachedValue = MetricLabel();
        _outputValue = MetricLabel();
        _cacheCreationValue = InlineMetricLabel();
        _cacheHitRateValue = InlineMetricLabel();
        _speedEstimateValue = InlineMetricLabel();
        _speedEstimateValue.AutoEllipsis = true;

        var cardTitleFont = new Font(Font, FontStyle.Regular);
        var cardTitleHeight = TextRenderer.MeasureText("订阅参考金额", cardTitleFont).Height + 6;
        var cardValueHeight = Math.Max(74, _inputValue.Font.Height * 3 + 20);
        var cardContentHeight = cardTitleHeight + cardValueHeight;
        var cardsHeight = cardContentHeight + 16;
        var cards = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Padding = new Padding(12, 4, 12, 4)
        };
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20.66f));
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20.67f));
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20.67f));
        cards.Controls.Add(Card("订阅参考金额 (分应用)", apiValuePanel, null, cardTitleFont, cardTitleHeight, 0), 0, 0);
        cards.Controls.Add(Card("Input（未命中）", _inputValue, null, cardTitleFont, cardTitleHeight, 0), 1, 0);
        cards.Controls.Add(Card("Cache Read", _cachedValue, null, cardTitleFont, cardTitleHeight, 0), 2, 0);
        cards.Controls.Add(Card("Output", _outputValue, null, cardTitleFont, cardTitleHeight, 0), 3, 0);



        var cacheMissHeight = Math.Max(32, Font.Height + 14);
        var cacheMissLine = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 8,
            RowCount = 1,
            Padding = new Padding(12, 2, 12, 2)
        };
        cacheMissLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        cacheMissLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18f));
        cacheMissLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        cacheMissLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18f));
        cacheMissLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        cacheMissLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 14f));
        cacheMissLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        cacheMissLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));

        cacheMissLine.Controls.Add(new Label
        {
            Text = "未命中：",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray
        }, 0, 0);
        _uncachedInputValue = InlineMetricLabel();
        cacheMissLine.Controls.Add(_uncachedInputValue, 1, 0);

        cacheMissLine.Controls.Add(new Label
        {
            Text = "缓存创建：",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            Padding = new Padding(6, 0, 0, 0)
        }, 2, 0);
        _cacheCreationValue = InlineMetricLabel();
        cacheMissLine.Controls.Add(_cacheCreationValue, 3, 0);

        cacheMissLine.Controls.Add(new Label
        {
            Text = "命中率：",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            Padding = new Padding(6, 0, 0, 0)
        }, 4, 0);
        cacheMissLine.Controls.Add(_cacheHitRateValue, 5, 0);

        cacheMissLine.Controls.Add(new Label
        {
            Text = "预估速率：",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            Padding = new Padding(8, 0, 0, 0)
        }, 6, 0);
        cacheMissLine.Controls.Add(_speedEstimateValue, 7, 0);

        _toolTip.SetToolTip(cacheMissLine, "Sub2API token 口径：Input=总输入-Cache Read-Cache Creation；Cache Read=缓存读取；Cache Creation=缓存创建；命中率=Cache Read / 总 Input；预估速率基于会话时间戳反推。");


        var chartTitleHeight = TextRenderer.MeasureText("每日 订阅参考金额", Font).Height + 10;
        var chartPlotHeight = Math.Max(150, TextRenderer.MeasureText("00-00", Font).Height + 126);
        _chart = new DailyBarChartControl { Dock = DockStyle.Fill, MinimumSize = new Size(0, chartPlotHeight), Margin = new Padding(0) };
        var chartTitle = new Label
        {
            Text = "每日 订阅参考金额",
            Dock = DockStyle.Top,
            Height = chartTitleHeight,
            Padding = new Padding(0, 5, 0, 0),
            Font = new Font(Font, FontStyle.Bold),
            AutoEllipsis = true
        };
        var chartPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 0, 12, 8),
            MinimumSize = new Size(0, chartTitleHeight + chartPlotHeight + 8)
        };
        chartPanel.Controls.Add(_chart);
        chartPanel.Controls.Add(chartTitle);

        _models = CreateGrid(["模型", "Provider", "Input（未命中）", "Cache Read", "缓存命中率", "预估速率", "Output", "订阅参考金额"], DefaultModelColumnWidths);
        _projects = CreateGrid(["项目", "Provider", "Tokens", "Input（未命中）", "Cache Read", "缓存命中率", "预估速率", "Output", "订阅参考金额"], DefaultProjectColumnWidths);
        _models.ColumnWidthChanged += (_, _) => { if (!_isRestoringColumns) ColumnWidthsChanged?.Invoke(this, EventArgs.Empty); };
        _projects.ColumnWidthChanged += (_, _) => { if (!_isRestoringColumns) ColumnWidthsChanged?.Invoke(this, EventArgs.Empty); };
        _quotaControl = new QuotaSummaryControl { Dock = DockStyle.Fill, ShowHeader = false, ShowDismissHint = false, ShowFooterNote = true, BackColor = Color.White };
        var tabs = new TabControl { Dock = DockStyle.Fill, Margin = new Padding(12, 0, 12, 0) };
        var modelPage = new TabPage("按模型"); modelPage.Controls.Add(_models);
        var projectPage = new TabPage("按项目"); projectPage.Controls.Add(_projects);
        var quotaPage = new TabPage("额度"); quotaPage.Controls.Add(_quotaControl);
        tabs.TabPages.AddRange([modelPage, projectPage, quotaPage]);

        var statusHeight = Math.Max(34, Font.Height + 16);
        _status = new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 5, 12, 5),
            ForeColor = Color.DimGray
        };

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, topHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, cardsHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, cacheMissHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, chartTitleHeight + chartPlotHeight + 8));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, statusHeight));
        root.Controls.Add(top, 0, 0);
        root.Controls.Add(cards, 0, 1);
        root.Controls.Add(cacheMissLine, 0, 2);
        root.Controls.Add(chartPanel, 0, 3);
        root.Controls.Add(tabs, 0, 4);
        root.Controls.Add(_status, 0, 5);
        Controls.Add(root);
    }

    public void ApplySnapshot(DashboardSnapshot snapshot)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => ApplySnapshot(snapshot)); return; }
        
        var codexStdCost = snapshot.CodexStandardApiEquivalentUsd;
        var codexSparkCost = snapshot.CodexSparkApiEquivalentUsd;
        var codexResCost = snapshot.CodexReserveApiEquivalentUsd;

        var hasSparkActivity = codexSparkCost > 0 || snapshot.CodexWeeklyCycles.Any(c => c.PoolCategory == "spark") || snapshot.Quotas.Any(q => q.Snapshot.Provider == ProviderKind.Codex && UsageAggregator.IsSparkSnapshot(q.Snapshot));
        var hasReserveActivity = codexResCost > 0 || snapshot.CodexWeeklyCycles.Any(c => c.PoolCategory == "reserve") || snapshot.Quotas.Any(q => q.Snapshot.Provider == ProviderKind.Codex && UsageAggregator.IsReserveSnapshot(q.Snapshot));

        if (hasSparkActivity && hasReserveActivity)
            _codexApiValue.Text = $"Codex: {FormatCost(codexStdCost)} | {FormatCost(codexSparkCost)} | {FormatCost(codexResCost)}";
        else if (hasSparkActivity)
            _codexApiValue.Text = $"Codex: {FormatCost(codexStdCost)} | {FormatCost(codexSparkCost)}";
        else if (hasReserveActivity)
            _codexApiValue.Text = $"Codex: {FormatCost(codexStdCost)} | {FormatCost(codexResCost)}";
        else
            _codexApiValue.Text = $"Codex: {FormatCost(codexStdCost)}";

        _toolTip.SetToolTip(_codexApiValue, $"Codex 主力: {FormatCost(codexStdCost)}" +
            (hasSparkActivity ? $"\r\nGPT-5.3 Spark: {FormatCost(codexSparkCost)}" : "") +
            (hasReserveActivity ? $"\r\nCodex Reserve: {FormatCost(codexResCost)}" : ""));

        var geminiCost = snapshot.AntigravityGeminiApiEquivalentUsd;
        var claudeCost = snapshot.AntigravityClaudeApiEquivalentUsd;
        _antigravityApiValue.Text = $"Antigravity: {FormatCost(geminiCost)} | {FormatCost(claudeCost)}";

        if (snapshot.IsWeeklyCycleWindow)
        {
            // Codex 多通道周期 (Standard / Spark / Reserve)
            var stdCycle = snapshot.CodexWeeklyCycles.FirstOrDefault(c => c.PoolCategory == "standard") ?? snapshot.CodexWeeklyCycle;
            var sparkCycle = snapshot.CodexWeeklyCycles.FirstOrDefault(c => c.PoolCategory == "spark");
            var resCycle = snapshot.CodexWeeklyCycles.FirstOrDefault(c => c.PoolCategory == "reserve") ?? snapshot.CodexReserveWeeklyCycle;

            var cycleParts = new List<string>();
            if (stdCycle is { ResetAt: not null })
            {
                var rel = TimeFormatter.FormatRelativeFuture(stdCycle.ResetAt.Value);
                cycleParts.Add($"主力: {stdCycle.ResetAt.Value.ToLocalTime():MM-dd HH:mm}（{rel}）");
            }
            if (sparkCycle is { ResetAt: not null })
            {
                var rel = TimeFormatter.FormatRelativeFuture(sparkCycle.ResetAt.Value);
                cycleParts.Add($"Spark: {sparkCycle.ResetAt.Value.ToLocalTime():MM-dd HH:mm}（{rel}）");
            }
            if (resCycle is { ResetAt: not null })
            {
                var rel = TimeFormatter.FormatRelativeFuture(resCycle.ResetAt.Value);
                cycleParts.Add($"Reserve: {resCycle.ResetAt.Value.ToLocalTime():MM-dd HH:mm}（{rel}）");
            }

            if (cycleParts.Count > 0)
            {
                _codexCycleNote.Text = $"周期：" + string.Join(" | ", cycleParts);
            }
            else
            {
                var codexQuota = snapshot.Quotas.FirstOrDefault(q => q.Snapshot.Provider == ProviderKind.Codex && q.Snapshot.WindowKind.Contains("week", StringComparison.OrdinalIgnoreCase));
                if (codexQuota?.Snapshot.ResetAt.HasValue == true)
                {
                    var reset = codexQuota.Snapshot.ResetAt.Value;
                    var start = reset.AddDays(-7);
                    var rel = TimeFormatter.FormatRelativeFuture(reset);
                    _codexCycleNote.Text = $"周期：{start.ToLocalTime():yyyy-MM-dd HH:mm} ~ {reset.ToLocalTime():yyyy-MM-dd HH:mm}（{rel}）";
                }
                else
                {
                    _codexCycleNote.Text = "周期：待同步（未纳入本次周统计）";
                }
            }

            // Antigravity 双周期（Gemini 池 & Claude/GPT 池）
            var geminiEst = snapshot.AntigravityEstimates?.FirstOrDefault(e => e.WindowKind == "weekly" && e.DisplayName.Contains("Gemini", StringComparison.OrdinalIgnoreCase));
            var claudeEst = snapshot.AntigravityEstimates?.FirstOrDefault(e => e.WindowKind == "weekly" && (e.DisplayName.Contains("Claude", StringComparison.OrdinalIgnoreCase) || e.DisplayName.Contains("3p", StringComparison.OrdinalIgnoreCase)));

            string geminiCycle = geminiEst?.ResetAt.HasValue == true
                ? $"Gemini: {geminiEst.ResetAt.Value.AddDays(-7).ToLocalTime():MM-dd HH:mm}~{geminiEst.ResetAt.Value.ToLocalTime():MM-dd HH:mm}（{TimeFormatter.FormatRelativeFuture(geminiEst.ResetAt.Value)}）"
                : "Gemini: 暂无配额";

            string claudeCycle = claudeEst?.ResetAt.HasValue == true
                ? $"Claude: {claudeEst.ResetAt.Value.AddDays(-7).ToLocalTime():MM-dd HH:mm}~{claudeEst.ResetAt.Value.ToLocalTime():MM-dd HH:mm}（{TimeFormatter.FormatRelativeFuture(claudeEst.ResetAt.Value)}）"
                : "Claude: 暂无配额";

            _antigravityCycleNote.Text = $"周期：{geminiCycle} | {claudeCycle}";
        }
        else
        {
            _codexCycleNote.Text = $"范围：{snapshot.Range.From:yyyy-MM-dd} 至 {snapshot.Range.To:yyyy-MM-dd}";
            _antigravityCycleNote.Text = $"范围：{snapshot.Range.From:yyyy-MM-dd} 至 {snapshot.Range.To:yyyy-MM-dd}";
        }




        _inputValue.Text = FormatTokens(snapshot.NonCachedInputTokens);
        _cachedValue.Text = FormatTokens(snapshot.CachedTokens);
        _uncachedInputValue.Text = FormatTokens(snapshot.NonCachedInputTokens);
        _cacheCreationValue.Text = FormatTokens(snapshot.CacheCreationTokens);
        _cacheHitRateValue.Text = snapshot.InputTokens > 0 ? $"{snapshot.CacheHitRate:F2}%" : "0.00%";
        _outputValue.Text = FormatTokens(snapshot.OutputTokens);

        var speed = snapshot.SpeedEstimate;
        if (speed is not null && speed.HasData)
        {
            _speedEstimateValue.Text = speed.ToShortDisplayString();
            _toolTip.SetToolTip(_speedEstimateValue, speed.ToDetailedTooltip());
        }
        else
        {
            _speedEstimateValue.Text = "-";
            _toolTip.SetToolTip(_speedEstimateValue, "预估速率：暂无足够的时间戳样本进行反推。\r\n\r\n说明：仅当本地会话存在连续 Turn 时间戳记录时计算。详情见“设置”。");
        }

        _chart.SetData(snapshot.Daily);

        _models.Rows.Clear();
        foreach (var row in snapshot.Models)
        {
            var hitRate = row.InputTokens > 0 ? $"{row.CacheHitRate:F2}%" : "0.00%";
            var speedText = row.SpeedEstimate?.HasData == true ? row.SpeedEstimate.ToShortDisplayString() : "-";
            _models.Rows.Add(row.ModelId, row.Provider.ToStorageString(), FormatTokens(row.NonCachedInputTokens),
                FormatTokens(row.CachedTokens), hitRate, speedText, FormatTokens(row.OutputTokens), FormatCost(row.ApiEquivalentUsd));
        }

        _projects.Rows.Clear();
        foreach (var row in snapshot.Projects)
        {
            var hitRate = row.InputTokens > 0 ? $"{row.CacheHitRate:F2}%" : "0.00%";
            var speedText = row.SpeedEstimate?.HasData == true ? row.SpeedEstimate.ToShortDisplayString() : "-";
            _projects.Rows.Add(row.DisplayName, row.Provider.ToStorageString(), FormatTokens(row.Tokens),
                FormatTokens(row.NonCachedInputTokens), FormatTokens(row.CachedTokens), hitRate, speedText, FormatTokens(row.OutputTokens), FormatCost(row.ApiEquivalentUsd));
        }

        _quotaControl.SetSnapshot(snapshot);

        var warningText = snapshot.Warnings.Count == 0 ? string.Empty : string.Join("；", snapshot.Warnings.Take(3));
        var rangeText = !string.IsNullOrWhiteSpace(snapshot.RangeDisplayOverride)
            ? snapshot.RangeDisplayOverride
            : $"范围：{FormatRange(snapshot.Range)}";

        _status.Text = string.IsNullOrEmpty(warningText)
            ? $"{rangeText}；最后刷新：{snapshot.RefreshedAt.ToLocalTime():HH:mm:ss}"
            : $"{rangeText}；提示：{warningText}；最后刷新：{snapshot.RefreshedAt.ToLocalTime():HH:mm:ss}";
    }

    public void RefreshCurrentSelection()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(RefreshCurrentSelection); return; }
        ApplyCurrentSelection();
    }

    private async Task RefreshViewAsync(bool force)
    {
        try
        {
            _status.Text = force ? "正在全量读取用量数据…" : "正在增量读取用量数据…";
            await _coordinator.RefreshAsync(force);
            ApplyCurrentSelection();
        }
        catch (Exception exception) { _status.Text = $"刷新失败：{exception.Message}"; }
    }

    private void HandleRangeSelectionChanged()
    {
        if (_ignoreRangeSelection) return;
        if (_rangeCombo.SelectedIndex != 6)
        {
            _previousRangeIndex = _rangeCombo.SelectedIndex;
            ApplyCurrentSelection();
            return;
        }

        using var dialog = new DateRangeDialog(_customRange);
        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.SelectedRange is not null)
        {
            _customRange = dialog.SelectedRange;
            _previousRangeIndex = 6;
            ApplyCurrentSelection();
            return;
        }

        _ignoreRangeSelection = true;
        _rangeCombo.SelectedIndex = _previousRangeIndex == 6 ? 1 : _previousRangeIndex;
        _ignoreRangeSelection = false;
    }

    private void ApplyCurrentSelection()
    {
        var provider = _providerCombo.SelectedIndex switch { 1 => ProviderKind.Codex, 2 => ProviderKind.Antigravity, _ => (ProviderKind?)null };
        var isWeeklyCycle = _rangeCombo.SelectedIndex == 4;
        var range = _rangeCombo.SelectedIndex switch
        {
            0 => DateRange.Today(),
            1 => DateRange.LastDays(7),
            2 => DateRange.LastDays(30),
            3 => DateRange.ThisMonth(),
            4 => DateRange.LastDays(7), // 精确周周期窗口将在 BuildSnapshot 内部动态计算
            5 => DateRange.AllTime(),
            6 => _customRange,
            _ => DateRange.LastDays(7)
        };
        ApplySnapshot(_coordinator.BuildSnapshot(range, provider, isWeeklyCycle));
    }


    private Label FieldLabel(string text, int topOffset) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(0, topOffset, 5, 0),
        TextAlign = ContentAlignment.MiddleLeft
    };

    private Label MetricLabel() => new()
    {
        Text = "—",
        Dock = DockStyle.Fill,
        AutoSize = false,
        AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font(Font.FontFamily, 14F, FontStyle.Bold),
        ForeColor = Color.FromArgb(30, 70, 110)
    };

    private Label InlineMetricLabel() => new()
    {
        Text = "—",
        Dock = DockStyle.Fill,
        AutoSize = false,
        AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font(Font, FontStyle.Bold),
        ForeColor = Color.FromArgb(30, 70, 110),
        Padding = new Padding(4, 0, 0, 0)
    };

    private Label NoteLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        AutoSize = false,
        AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font(Font, FontStyle.Regular),
        ForeColor = Color.DimGray
    };

    private static Control Card(string title, Control value, Label? note, Font titleFont, int titleHeight, int noteHeight)
    {

        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(4),
            Padding = new Padding(8),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = SystemColors.Window
        };
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, titleHeight));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, noteHeight));
        content.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            AutoSize = false,
            Font = titleFont,
            ForeColor = Color.DimGray,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        content.Controls.Add(value, 0, 1);
        content.Controls.Add(note ?? new Label { Dock = DockStyle.Fill, Font = titleFont }, 0, 2);
        panel.Controls.Add(content);
        return panel;
    }

    private bool _isRestoringColumns;
    public event EventHandler? ColumnWidthsChanged;

    private static readonly Dictionary<string, int> DefaultModelColumnWidths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["模型"] = 402,
        ["Provider"] = 215,
        ["Input（未命中）"] = 293,
        ["Cache Read"] = 195,
        ["缓存命中率"] = 212,
        ["预估速率"] = 680,
        ["Output"] = 260,
        ["订阅参考金额"] = 155
    };

    private static readonly Dictionary<string, int> DefaultProjectColumnWidths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["项目"] = 307,
        ["Provider"] = 207,
        ["Tokens"] = 175,
        ["Input（未命中）"] = 287,
        ["Cache Read"] = 220,
        ["缓存命中率"] = 216,
        ["预估速率"] = 565,
        ["Output"] = 185,
        ["订阅参考金额"] = 178
    };

    public Dictionary<string, int> GetModelColumnWidths() => GetColumnWidths(_models);
    public Dictionary<string, int> GetProjectColumnWidths() => GetColumnWidths(_projects);

    public void RestoreColumnWidths(Dictionary<string, int>? modelWidths, Dictionary<string, int>? projectWidths)
    {
        _isRestoringColumns = true;
        try
        {
            ApplyColumnWidths(_models, modelWidths ?? DefaultModelColumnWidths);
            ApplyColumnWidths(_projects, projectWidths ?? DefaultProjectColumnWidths);
        }
        finally
        {
            _isRestoringColumns = false;
        }
    }

    private static Dictionary<string, int> GetColumnWidths(DataGridView grid)
    {
        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewColumn col in grid.Columns)
        {
            if (col.Width > 0) dict[col.Name] = col.Width;
        }
        return dict;
    }

    private static void ApplyColumnWidths(DataGridView grid, Dictionary<string, int>? widths)
    {
        if (widths == null || widths.Count == 0) return;
        foreach (DataGridViewColumn col in grid.Columns)
        {
            if (widths.TryGetValue(col.Name, out var w) && w >= 30)
            {
                col.Width = w;
            }
        }
    }

    private DataGridView CreateGrid(string[] columns, Dictionary<string, int>? defaultWidths = null)
    {
        var cellFont = new Font(Font, FontStyle.Regular);
        var headerFont = new Font(Font, FontStyle.Bold);
        var cellTextHeight = TextRenderer.MeasureText("模型", cellFont).Height;
        var headerTextHeight = TextRenderer.MeasureText("订阅参考金额", headerFont).Height;
        var cellPadding = new Padding(4, 4, 4, 4);
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToResizeRows = false,
            AllowUserToResizeColumns = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
            BackgroundColor = Color.White,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            ColumnHeadersHeight = Math.Max(36, headerTextHeight + 10),
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.EnableResizing,
            RowTemplate = { Height = Math.Max(32, cellTextHeight + 10) },
            BorderStyle = BorderStyle.FixedSingle,
            GridColor = Color.LightGray,
            Font = cellFont
        };
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(245, 247, 250),
            ForeColor = Color.FromArgb(35, 35, 35),
            Font = headerFont,
            Alignment = DataGridViewContentAlignment.MiddleLeft,
            Padding = cellPadding
        };
        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            Font = cellFont,
            Alignment = DataGridViewContentAlignment.MiddleLeft,
            Padding = cellPadding,
            WrapMode = DataGridViewTriState.False,
            SelectionBackColor = Color.FromArgb(218, 232, 247),
            SelectionForeColor = Color.FromArgb(25, 25, 25)
        };
        foreach (var column in columns)
        {
            var colIndex = grid.Columns.Add(column, column);
            var col = grid.Columns[colIndex];
            col.MinimumWidth = 40;
            col.Resizable = DataGridViewTriState.True;
            if (defaultWidths != null && defaultWidths.TryGetValue(column, out var defW) && defW >= 30)
            {
                col.Width = defW;
            }
            else
            {
                col.Width = 100;
            }
        }
        return grid;
    }

    private static string FormatRange(DateRange range) => range.From == range.To
        ? range.From.ToString("yyyy-MM-dd")
        : $"{range.From:yyyy-MM-dd} 至 {range.To:yyyy-MM-dd}";

    private static string FormatTokens(long value) => value switch { >= 1_000_000 => $"{value / 1_000_000d:0.##}M", >= 1_000 => $"{value / 1_000d:0.##}K", _ => value.ToString("N0") };
    private static string FormatCost(decimal? value) => value.HasValue ? "$" + value.Value.ToString("0.00") : "—";
}


