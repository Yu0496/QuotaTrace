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
    private readonly Button _refreshButton;
    private readonly Button _settingsButton;
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
    private readonly Label _uncachedInputLabel;
    private readonly Label _cacheCreationLabel;
    private readonly Label _cacheHitRateLabel;
    private readonly Label _speedEstimateLabel;
    private readonly Label _chartTitle;
    private readonly Label _providerFieldLabel;
    private readonly Label _rangeFieldLabel;
    private readonly Label _status;
    private readonly ToolTip _toolTip;
    private readonly DataGridView _models;
    private readonly DataGridView _projects;
    private readonly DailyBarChartControl _chart;
    private readonly QuotaSummaryControl _quotaControl;
    private readonly CodexHistoryControl _codexHistory;
    private readonly TabControl _tabs;
    private readonly TabPage _modelPage;
    private readonly TabPage _projectPage;
    private readonly TabPage _quotaPage;
    private readonly TabPage _codexHistoryPage;
    private DateRange _customRange;
    private int _previousRangeIndex;
    private bool _ignoreRangeSelection;
    private bool _isInitializing = true;
    private int _loadSequence;

    public MainForm(RefreshCoordinator coordinator, AppSettingsStore settingsStore)
    {
        _coordinator = coordinator;
        _settingsStore = settingsStore;
        _customRange = DateRange.LastDays(7);
        _previousRangeIndex = 4;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font("Segoe UI", 9F);
        Icon = AppIcon.Create();
        Text = "QuotaTrace";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(780, 560);
        MinimumSize = new Size(740, 500);
        FormClosing += (_, e) => { e.Cancel = true; Hide(); };

        var textHeight = TextRenderer.MeasureText(I18n.T("刷新", "Refresh"), Font).Height;
        var buttonHeight = Math.Max(34, textHeight + 12);
        var topHeight = buttonHeight + 18;
        var controlVerticalOffset = Math.Max(0, (buttonHeight - textHeight) / 2);

        _providerCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = Math.Max(110, TextRenderer.MeasureText("Antigravity", Font).Width + 36),
            Height = buttonHeight,
            Margin = new Padding(0, 0, 8, 0)
        };

        _rangeCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = Math.Max(150, TextRenderer.MeasureText(I18n.T("本次周额度", "Current Weekly Cycle"), Font).Width + 36),
            Height = buttonHeight,
            Margin = new Padding(0, 0, 8, 0)
        };

        _refreshButton = new Button
        {
            Text = I18n.T("刷新", "Refresh"),
            Width = Math.Max(64, TextRenderer.MeasureText(I18n.T("刷新", "Refresh"), Font).Width + 24),
            Height = buttonHeight,
            Margin = new Padding(0, 0, 6, 0),
            AutoSize = false,
            Padding = new Padding(8, 2, 8, 2)
        };
        _refreshButton.Click += async (_, _) => await RefreshViewAsync(false);

        _settingsButton = new Button
        {
            Text = I18n.T("设置", "Settings"),
            Width = Math.Max(64, TextRenderer.MeasureText(I18n.T("设置", "Settings"), Font).Width + 24),
            Height = buttonHeight,
            Margin = new Padding(0),
            AutoSize = false,
            Padding = new Padding(8, 2, 8, 2)
        };
        _settingsButton.Click += (_, _) =>
        {
            using var form = new SettingsForm(_settingsStore, _coordinator);
            form.ShowDialog(this);
            RefreshProviderAndRangeOptions();
            ApplyCurrentSelection();
        };

        _toolTip = new ToolTip();

        _providerCombo.SelectedIndexChanged += (_, _) => ApplyCurrentSelection();
        _rangeCombo.SelectedIndexChanged += (_, _) => HandleRangeSelectionChanged();

        _providerFieldLabel = FieldLabel(I18n.T("提供商", "Provider"), controlVerticalOffset);
        _rangeFieldLabel = FieldLabel(I18n.T("日期", "Date"), controlVerticalOffset);

        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 9, 12, 9),
            WrapContents = false,
            AutoScroll = false,
            FlowDirection = FlowDirection.LeftToRight
        };
        top.Controls.AddRange([
            _providerFieldLabel, _providerCombo,
            _rangeFieldLabel, _rangeCombo,
            _refreshButton, _settingsButton
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
            Text = I18n.T("周期：—", "Cycle: —"),
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
            Text = I18n.T("周期：—", "Cycle: —"),
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
        _uncachedInputValue = InlineMetricLabel();
        _cacheCreationValue = InlineMetricLabel();
        _cacheHitRateValue = InlineMetricLabel();
        _speedEstimateValue = new Label
        {
            Text = "—",
            Dock = DockStyle.Fill,
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Font,
            ForeColor = Color.FromArgb(30, 41, 59),
            Padding = new Padding(2, 0, 0, 0)
        };

        var cardTitleFont = new Font(Font, FontStyle.Regular);
        var cardTitleHeight = TextRenderer.MeasureText(I18n.T("订阅参考金额", "Sub Ref Value"), cardTitleFont).Height + 6;
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
        cards.Controls.Add(Card(I18n.T("订阅参考金额 (分应用)", "Sub Ref Value (By App)"), apiValuePanel, null, cardTitleFont, cardTitleHeight, 0), 0, 0);
        cards.Controls.Add(Card(I18n.T("Input（未命中）", "Input (Uncached)"), _inputValue, null, cardTitleFont, cardTitleHeight, 0), 1, 0);
        cards.Controls.Add(Card("Cache Read", _cachedValue, null, cardTitleFont, cardTitleHeight, 0), 2, 0);
        cards.Controls.Add(Card("Output", _outputValue, null, cardTitleFont, cardTitleHeight, 0), 3, 0);

        var cacheMissHeight = Math.Max(38, Font.Height + 20);
        var cacheMissLine = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 8,
            RowCount = 1,
            Padding = new Padding(12, 3, 12, 3)
        };
        cacheMissLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        cacheMissLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        cacheMissLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        cacheMissLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        cacheMissLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        cacheMissLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        cacheMissLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        cacheMissLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        _uncachedInputLabel = new Label
        {
            Text = I18n.T("未命中：", "Uncached: "),
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray
        };
        cacheMissLine.Controls.Add(_uncachedInputLabel, 0, 0);
        cacheMissLine.Controls.Add(_uncachedInputValue, 1, 0);

        _cacheCreationLabel = new Label
        {
            Text = I18n.T("缓存创建：", "Cache Creation: "),
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray
        };
        cacheMissLine.Controls.Add(_cacheCreationLabel, 2, 0);
        cacheMissLine.Controls.Add(_cacheCreationValue, 3, 0);

        _cacheHitRateLabel = new Label
        {
            Text = I18n.T("命中率：", "Hit Rate: "),
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray
        };
        cacheMissLine.Controls.Add(_cacheHitRateLabel, 4, 0);
        cacheMissLine.Controls.Add(_cacheHitRateValue, 5, 0);

        _speedEstimateLabel = new Label
        {
            Text = I18n.T("预估速率：", "Est. Speed: "),
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray
        };
        cacheMissLine.Controls.Add(_speedEstimateLabel, 6, 0);
        _speedEstimateValue.Dock = DockStyle.Fill;
        cacheMissLine.Controls.Add(_speedEstimateValue, 7, 0);

        var chartTitleHeight = TextRenderer.MeasureText(I18n.T("每日 订阅参考金额", "Daily Sub Ref Value"), Font).Height + 10;
        var chartPlotHeight = Math.Max(65, TextRenderer.MeasureText("00-00", Font).Height + 48);
        _chart = new DailyBarChartControl { Dock = DockStyle.Fill, MinimumSize = new Size(0, chartPlotHeight), Margin = new Padding(0) };
        _chartTitle = new Label
        {
            Text = I18n.T("每日 订阅参考金额", "Daily Sub Ref Value"),
            Dock = DockStyle.Fill,
            Height = chartTitleHeight,
            Padding = new Padding(0, 4, 0, 2),
            Font = new Font(Font, FontStyle.Bold),
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft
        };
        var chartPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(12, 4, 12, 8),
            Margin = new Padding(0)
        };
        chartPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, chartTitleHeight + 6));
        chartPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        chartPanel.Controls.Add(_chartTitle, 0, 0);
        chartPanel.Controls.Add(_chart, 0, 1);

        _models = CreateGrid(ModelColumns, DefaultModelColumnWidths);
        _projects = CreateGrid(ProjectColumns, DefaultProjectColumnWidths);
        _models.ColumnWidthChanged += (_, _) => { if (!_isRestoringColumns) ColumnWidthsChanged?.Invoke(this, EventArgs.Empty); };
        _projects.ColumnWidthChanged += (_, _) => { if (!_isRestoringColumns) ColumnWidthsChanged?.Invoke(this, EventArgs.Empty); };
        _quotaControl = new QuotaSummaryControl { Dock = DockStyle.Fill, ShowHeader = false, ShowDismissHint = false, ShowFooterNote = true, BackColor = Color.White };
        _codexHistory = new CodexHistoryControl { Dock = DockStyle.Fill };
        _codexHistory.ColumnWidthsChanged += (_, _) => { if (!_isRestoringColumns) ColumnWidthsChanged?.Invoke(this, EventArgs.Empty); };

        _tabs = new TabControl { Dock = DockStyle.Fill, Margin = new Padding(12, 0, 12, 0) };
        _modelPage = new TabPage(I18n.T("按模型", "By Model")); _modelPage.Controls.Add(_models);
        _projectPage = new TabPage(I18n.T("按项目", "By Project")); _projectPage.Controls.Add(_projects);
        _quotaPage = new TabPage(I18n.T("额度", "Quotas")); _quotaPage.Controls.Add(_quotaControl);
        _codexHistoryPage = new TabPage(I18n.T("Codex 周历史", "Codex Weekly History")); _codexHistoryPage.Controls.Add(_codexHistory);
        _tabs.TabPages.AddRange([_modelPage, _projectPage, _quotaPage, _codexHistoryPage]);

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
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, chartTitleHeight + chartPlotHeight + 18));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, statusHeight));
        root.Controls.Add(top, 0, 0);
        root.Controls.Add(cards, 0, 1);
        root.Controls.Add(cacheMissLine, 0, 2);
        root.Controls.Add(chartPanel, 0, 3);
        root.Controls.Add(_tabs, 0, 4);
        root.Controls.Add(_status, 0, 5);
        Controls.Add(root);

        RefreshProviderAndRangeOptions();
        UpdateTooltips();
        I18n.LanguageChanged += HandleLanguageChanged;
        _isInitializing = false;
    }

    private void UpdateTooltips()
    {
        _toolTip.SetToolTip(_refreshButton, I18n.T("增量刷新：仅重新解析新增或发生变化的本地记录。程序启动时会自动全量读取一次；全量重读请到设置中执行。", "Incremental refresh: only re-parses new or modified local records. Full scans can be triggered in Settings."));
        _toolTip.SetToolTip(_rangeCombo, I18n.T("选择自定义…后填写开始日期和结束日期，按本地日历统计。", "Select Custom... to specify start and end dates based on local calendar."));
    }

    private void HandleLanguageChanged()
    {
        Text = "QuotaTrace";
        _refreshButton.Text = I18n.T("刷新", "Refresh");
        _settingsButton.Text = I18n.T("设置", "Settings");
        _providerFieldLabel.Text = I18n.T("提供商", "Provider");
        _rangeFieldLabel.Text = I18n.T("日期", "Date");
        _uncachedInputLabel.Text = I18n.T("未命中：", "Uncached: ");
        _cacheCreationLabel.Text = I18n.T("缓存创建：", "Cache Creation: ");
        _cacheHitRateLabel.Text = I18n.T("命中率：", "Hit Rate: ");
        _speedEstimateLabel.Text = I18n.T("预估速率：", "Est. Speed: ");
        _chartTitle.Text = I18n.T("每日 订阅参考金额", "Daily Sub Ref Value");

        _modelPage.Text = I18n.T("按模型", "By Model");
        _projectPage.Text = I18n.T("按项目", "By Project");
        _quotaPage.Text = I18n.T("额度", "Quotas");
        _codexHistoryPage.Text = I18n.T("Codex 周历史", "Codex Weekly History");

        UpdateGridHeaders(_models, ModelColumns);
        UpdateGridHeaders(_projects, ProjectColumns);
        RefreshProviderAndRangeOptions();
        UpdateTooltips();
    }

    private void RefreshProviderAndRangeOptions()
    {
        var enableCodex = _coordinator.Settings.EnableCodex;
        var enableAntigravity = _coordinator.Settings.EnableAntigravity;

        // Provider dropdown
        var prevProviderIdx = _providerCombo.SelectedIndex;
        _providerCombo.Items.Clear();
        if (enableCodex && enableAntigravity)
        {
            _providerCombo.Items.AddRange([I18n.T("全部", "All"), "Codex", "Antigravity"]);
            _providerCombo.Enabled = true;
            _providerCombo.SelectedIndex = Math.Clamp(prevProviderIdx, 0, 2);
        }
        else if (enableCodex)
        {
            _providerCombo.Items.Add("Codex");
            _providerCombo.SelectedIndex = 0;
            _providerCombo.Enabled = false;
        }
        else
        {
            _providerCombo.Items.Add("Antigravity");
            _providerCombo.SelectedIndex = 0;
            _providerCombo.Enabled = false;
        }

        // Range dropdown
        var prevRangeIdx = _rangeCombo.SelectedIndex;
        _rangeCombo.Items.Clear();
        _rangeCombo.Items.AddRange([
            I18n.T("今天", "Today"),
            I18n.T("7天", "7 Days"),
            I18n.T("30天", "30 Days"),
            I18n.T("本月", "This Month"),
            I18n.T("本次周额度", "Current Weekly Cycle"),
            I18n.T("全部", "All"),
            I18n.T("自定义…", "Custom...")
        ]);
        _rangeCombo.SelectedIndex = prevRangeIdx >= 0 && prevRangeIdx < _rangeCombo.Items.Count ? prevRangeIdx : 4;

        // TabPage visibility
        if (!enableCodex)
        {
            if (_tabs.TabPages.Contains(_codexHistoryPage)) _tabs.TabPages.Remove(_codexHistoryPage);
        }
        else
        {
            if (!_tabs.TabPages.Contains(_codexHistoryPage)) _tabs.TabPages.Add(_codexHistoryPage);
        }

        // Quota control provider toggles
        _quotaControl.EnableCodex = enableCodex;
        _quotaControl.EnableAntigravity = enableAntigravity;

        // Cards visibility
        _codexApiValue.Visible = enableCodex;
        _codexCycleNote.Visible = enableCodex;
        _antigravityApiValue.Visible = enableAntigravity;
        _antigravityCycleNote.Visible = enableAntigravity;
    }

    public void ApplySnapshot(DashboardSnapshot snapshot)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => ApplySnapshot(snapshot)); return; }

        var enableCodex = _coordinator.Settings.EnableCodex;
        var enableAntigravity = _coordinator.Settings.EnableAntigravity;

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

        _toolTip.SetToolTip(_codexApiValue, $"{I18n.T("Codex 主力: ", "Codex Primary: ")}{FormatCost(codexStdCost)}" +
            (hasSparkActivity ? $"\r\nGPT-5.3 Spark: {FormatCost(codexSparkCost)}" : "") +
            (hasReserveActivity ? $"\r\nCodex Reserve: {FormatCost(codexResCost)}" : ""));

        var geminiCost = snapshot.AntigravityGeminiApiEquivalentUsd;
        var claudeCost = snapshot.AntigravityClaudeApiEquivalentUsd;
        _antigravityApiValue.Text = $"Antigravity: {FormatCost(geminiCost)} | {FormatCost(claudeCost)}";
        _toolTip.SetToolTip(_antigravityApiValue, $"Gemini: {FormatCost(geminiCost)}\r\nClaude: {FormatCost(claudeCost)}");

        if (snapshot.IsWeeklyCycleWindow)
        {
            var stdCycle = snapshot.CodexWeeklyCycles.FirstOrDefault(c => c.PoolCategory == "standard") ?? snapshot.CodexWeeklyCycle;
            var sparkCycle = snapshot.CodexWeeklyCycles.FirstOrDefault(c => c.PoolCategory == "spark");
            var resCycle = snapshot.CodexWeeklyCycles.FirstOrDefault(c => c.PoolCategory == "reserve") ?? snapshot.CodexReserveWeeklyCycle;

            var cycleParts = new List<string>();
            if (stdCycle is { ResetAt: not null })
            {
                var rel = TimeFormatter.FormatRelativeFuture(stdCycle.ResetAt.Value);
                cycleParts.Add($"{I18n.T("主力: ", "Primary: ")}{stdCycle.ResetAt.Value.ToLocalTime():MM-dd HH:mm}（{rel}）");
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
                _codexCycleNote.Text = I18n.T("周期：", "Cycle: ") + string.Join(" | ", cycleParts);
            }
            else
            {
                var codexQuota = snapshot.Quotas.FirstOrDefault(q => q.Snapshot.Provider == ProviderKind.Codex && q.Snapshot.WindowKind.Contains("week", StringComparison.OrdinalIgnoreCase));
                if (codexQuota?.Snapshot.ResetAt.HasValue == true)
                {
                    var reset = codexQuota.Snapshot.ResetAt.Value;
                    var start = reset.AddDays(-7);
                    var rel = TimeFormatter.FormatRelativeFuture(reset);
                    _codexCycleNote.Text = I18n.Format("周期：{0:yyyy-MM-dd HH:mm} ~ {1:yyyy-MM-dd HH:mm}（{2}）", "Cycle: {0:yyyy-MM-dd HH:mm} ~ {1:yyyy-MM-dd HH:mm} ({2})", start.ToLocalTime(), reset.ToLocalTime(), rel);
                }
                else
                {
                    _codexCycleNote.Text = I18n.T("周期：待同步（未纳入本次周统计）", "Cycle: Syncing (Not in weekly window)");
                }
            }

            var geminiEst = snapshot.AntigravityEstimates?.FirstOrDefault(e => e.WindowKind == "weekly" && e.DisplayName.Contains("Gemini", StringComparison.OrdinalIgnoreCase));
            var claudeEst = snapshot.AntigravityEstimates?.FirstOrDefault(e => e.WindowKind == "weekly" && (e.DisplayName.Contains("Claude", StringComparison.OrdinalIgnoreCase) || e.DisplayName.Contains("3p", StringComparison.OrdinalIgnoreCase)));

            string geminiCycle = geminiEst?.ResetAt.HasValue == true
                ? I18n.Format("Gemini: {0:MM-dd HH:mm}~{1:MM-dd HH:mm}（{2}）", "Gemini: {0:MM-dd HH:mm}~{1:MM-dd HH:mm} ({2})", geminiEst.ResetAt.Value.AddDays(-7).ToLocalTime(), geminiEst.ResetAt.Value.ToLocalTime(), TimeFormatter.FormatRelativeFuture(geminiEst.ResetAt.Value))
                : I18n.T("Gemini: 暂无配额", "Gemini: No quota");

            string claudeCycle = claudeEst?.ResetAt.HasValue == true
                ? I18n.Format("Claude: {0:MM-dd HH:mm}~{1:MM-dd HH:mm}（{2}）", "Claude: {0:MM-dd HH:mm}~{1:MM-dd HH:mm} ({2})", claudeEst.ResetAt.Value.AddDays(-7).ToLocalTime(), claudeEst.ResetAt.Value.ToLocalTime(), TimeFormatter.FormatRelativeFuture(claudeEst.ResetAt.Value))
                : I18n.T("Claude: 暂无配额", "Claude: No quota");

            _antigravityCycleNote.Text = I18n.T("周期：", "Cycle: ") + $"{geminiCycle} | {claudeCycle}";
        }
        else
        {
            _codexCycleNote.Text = I18n.Format("范围：{0:yyyy-MM-dd} 至 {1:yyyy-MM-dd}", "Range: {0:yyyy-MM-dd} to {1:yyyy-MM-dd}", snapshot.Range.From, snapshot.Range.To);
            _antigravityCycleNote.Text = I18n.Format("范围：{0:yyyy-MM-dd} 至 {1:yyyy-MM-dd}", "Range: {0:yyyy-MM-dd} to {1:yyyy-MM-dd}", snapshot.Range.From, snapshot.Range.To);
        }

        _inputValue.Text = FormatTokens(snapshot.NonCachedInputTokens);
        _cachedValue.Text = FormatTokens(snapshot.CachedTokens);
        _outputValue.Text = FormatTokens(snapshot.OutputTokens);

        _uncachedInputValue.Text = FormatTokens(snapshot.NonCachedInputTokens);
        _cacheCreationValue.Text = FormatTokens(snapshot.CacheCreationTokens);
        _cacheHitRateValue.Text = snapshot.InputTokens > 0 ? $"{snapshot.CacheHitRate:F2}%" : "0.00%";

        var speed = snapshot.SpeedEstimate;
        if (speed is not null && speed.HasData)
        {
            _speedEstimateValue.Text = speed.ToShortDisplayString();
            _toolTip.SetToolTip(_speedEstimateValue, speed.ToDetailedTooltip());
        }
        else
        {
            _speedEstimateValue.Text = "-";
            _toolTip.SetToolTip(_speedEstimateValue, I18n.T("预估速率：暂无足够的时间戳样本进行反推。\r\n\r\n说明：仅当本地会话存在连续 Turn 时间戳记录时计算。详情见“设置”。", "Estimated speed: Insufficient timestamp samples.\r\n\r\nNote: Calculated only when consecutive turn timestamps exist. See Settings for details."));
        }

        _chart.SetData(snapshot.Daily);
        _quotaControl.SetSnapshot(snapshot);

        if (enableCodex)
        {
            _codexHistory.SetCycles(snapshot.CodexHistoricalCycles);
        }

        RenderModels(snapshot.Models);
        RenderProjects(snapshot.Projects);

        var rangeLabel = !string.IsNullOrWhiteSpace(snapshot.RangeDisplayOverride)
            ? snapshot.RangeDisplayOverride
            : FormatRange(snapshot.Range);

        var statusText = I18n.Format("统计范围：{0} | 更新于 {1:yyyy-MM-dd HH:mm:ss}", "Range: {0} | Updated at {1:yyyy-MM-dd HH:mm:ss}", rangeLabel, snapshot.RefreshedAt.ToLocalTime());
        if (snapshot.Warnings.Count > 0) statusText += $" | {snapshot.Warnings[0]}";
        _status.Text = statusText;
    }

    private void RenderModels(IReadOnlyList<ModelUsageView> models)
    {
        _models.SuspendLayout();
        try
        {
            _models.Rows.Clear();
            foreach (var row in models)
            {
                var hitRate = row.InputTokens > 0 ? $"{row.CacheHitRate:F2}%" : "0.00%";
                var speedText = row.SpeedEstimate?.HasData == true ? row.SpeedEstimate.ToShortDisplayString() : "-";
                var estWeeklyText = FormatCost(row.EstimatedWeeklyCostUsd);

                var rowIndex = _models.Rows.Add(
                    row.ModelId,
                    row.Provider.ToStorageString(),
                    FormatTokens(row.NonCachedInputTokens),
                    FormatTokens(row.CachedTokens),
                    hitRate,
                    speedText,
                    FormatTokens(row.OutputTokens),
                    FormatCost(row.ApiEquivalentUsd),
                    estWeeklyText
                );

                if (!string.IsNullOrEmpty(row.EstimateDetail))
                {
                    _models.Rows[rowIndex].Cells["推算周满额"].ToolTipText = row.EstimateDetail;
                }
                else if (row.Provider == ProviderKind.Codex && !row.EstimatedWeeklyCostUsd.HasValue)
                {
                    _models.Rows[rowIndex].Cells["推算周满额"].ToolTipText = I18n.T("该模型暂无足够独立消耗样本测算周总额。", "Insufficient independent usage samples to estimate full weekly quota for this model.");
                }
            }
        }
        finally
        {
            _models.ResumeLayout();
        }
    }

    private void RenderProjects(IReadOnlyList<ProjectUsageView> projects)
    {
        _projects.SuspendLayout();
        try
        {
            _projects.Rows.Clear();
            foreach (var row in projects)
            {
                var hitRate = row.InputTokens > 0 ? $"{row.CacheHitRate:F2}%" : "0.00%";
                var speedText = row.SpeedEstimate?.HasData == true ? row.SpeedEstimate.ToShortDisplayString() : "-";
                _projects.Rows.Add(
                    row.DisplayName,
                    row.Provider.ToStorageString(),
                    FormatTokens(row.Tokens),
                    FormatTokens(row.NonCachedInputTokens),
                    FormatTokens(row.CachedTokens),
                    hitRate,
                    speedText,
                    FormatTokens(row.OutputTokens),
                    FormatCost(row.ApiEquivalentUsd)
                );
            }
        }
        finally
        {
            _projects.ResumeLayout();
        }
    }

    public void RefreshCurrentSelection()
    {
        if (InvokeRequired) { BeginInvoke(RefreshCurrentSelection); return; }
        ApplyCurrentSelection();
    }

    private async Task RefreshViewAsync(bool force)
    {
        try
        {
            _status.Text = force ? I18n.T("正在全量读取用量数据…", "Performing full scan...") : I18n.T("正在增量读取用量数据…", "Reading incremental usage data...");
            _refreshButton.Enabled = false;
            await _coordinator.RefreshAsync(force);
            ApplyCurrentSelection();
        }
        catch (Exception exception)
        {
            _status.Text = I18n.Format("刷新失败：{0}", "Refresh failed: {0}", exception.Message);
        }
        finally
        {
            if (!IsDisposed) _refreshButton.Enabled = true;
        }
    }

    private void HandleRangeSelectionChanged()
    {
        if (_isInitializing || _ignoreRangeSelection) return;
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
        if (_isInitializing) return;
        _ = ApplyCurrentSelectionAsync();
    }

    private async Task ApplyCurrentSelectionAsync()
    {
        if (IsDisposed) return;
        var seq = Interlocked.Increment(ref _loadSequence);

        ProviderKind? provider = null;
        var enableCodex = _coordinator.Settings.EnableCodex;
        var enableAntigravity = _coordinator.Settings.EnableAntigravity;

        if (enableCodex && enableAntigravity)
        {
            provider = _providerCombo.SelectedIndex switch { 1 => ProviderKind.Codex, 2 => ProviderKind.Antigravity, _ => null };
        }
        else if (enableCodex)
        {
            provider = ProviderKind.Codex;
        }
        else if (enableAntigravity)
        {
            provider = ProviderKind.Antigravity;
        }

        var isWeeklyCycle = _rangeCombo.SelectedIndex == 4;
        var range = _rangeCombo.SelectedIndex switch
        {
            0 => DateRange.Today(),
            1 => DateRange.LastDays(7),
            2 => DateRange.LastDays(30),
            3 => DateRange.ThisMonth(),
            4 => DateRange.LastDays(7),
            5 => DateRange.AllTime(),
            6 => _customRange,
            _ => DateRange.LastDays(7)
        };

        _status.Text = I18n.T("正在计算用量数据…", "Calculating usage data...");

        try
        {
            var snapshot = await Task.Run(() => _coordinator.BuildSnapshot(range, provider, isWeeklyCycle));
            if (seq != _loadSequence || IsDisposed) return;
            ApplySnapshot(snapshot);
        }
        catch (Exception ex)
        {
            if (seq == _loadSequence && !IsDisposed)
            {
                _status.Text = I18n.Format("计算失败：{0}", "Calculation failed: {0}", ex.Message);
            }
        }
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
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font(Font.FontFamily, 10F, FontStyle.Bold),
        ForeColor = Color.FromArgb(30, 70, 110),
        Margin = new Padding(0, 0, 18, 0)
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

    private static readonly (string Key, string Zh, string En)[] ModelColumns =
    [
        ("模型", "模型", "Model"),
        ("Provider", "Provider", "Provider"),
        ("Input（未命中）", "Input（未命中）", "Input (Uncached)"),
        ("Cache Read", "Cache Read", "Cache Read"),
        ("缓存命中率", "缓存命中率", "Cache Hit Rate"),
        ("预估速率", "预估速率", "Est. Speed"),
        ("Output", "Output", "Output"),
        ("订阅参考金额", "订阅参考金额", "Sub Ref Value"),
        ("推算周满额", "推算周满额", "Est. Weekly Full")
    ];

    private static readonly (string Key, string Zh, string En)[] ProjectColumns =
    [
        ("项目", "项目", "Project"),
        ("Provider", "Provider", "Provider"),
        ("Tokens", "Tokens", "Tokens"),
        ("Input（未命中）", "Input（未命中）", "Input (Uncached)"),
        ("Cache Read", "Cache Read", "Cache Read"),
        ("缓存命中率", "缓存命中率", "Cache Hit Rate"),
        ("预估速率", "预估速率", "Est. Speed"),
        ("Output", "Output", "Output"),
        ("订阅参考金额", "订阅参考金额", "Sub Ref Value")
    ];

    private static readonly Dictionary<string, int> DefaultModelColumnWidths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["模型"] = 150,
        ["Provider"] = 90,
        ["Input（未命中）"] = 135,
        ["Cache Read"] = 105,
        ["缓存命中率"] = 110,
        ["预估速率"] = 210,
        ["Output"] = 80,
        ["订阅参考金额"] = 110,
        ["推算周满额"] = 120
    };

    private static readonly Dictionary<string, int> DefaultProjectColumnWidths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["项目"] = 180,
        ["Provider"] = 90,
        ["Tokens"] = 90,
        ["Input（未命中）"] = 110,
        ["Cache Read"] = 100,
        ["缓存命中率"] = 90,
        ["预估速率"] = 220,
        ["Output"] = 80,
        ["订阅参考金额"] = 100
    };

    public Dictionary<string, int> GetModelColumnWidths() => GetColumnWidths(_models);
    public Dictionary<string, int> GetProjectColumnWidths() => GetColumnWidths(_projects);
    public Dictionary<string, int> GetCodexHistoryColumnWidths() => _codexHistory.GetColumnWidths();

    public void RestoreColumnWidths(Dictionary<string, int>? modelWidths, Dictionary<string, int>? projectWidths, Dictionary<string, int>? codexHistoryWidths = null)
    {
        _isRestoringColumns = true;
        try
        {
            ApplyColumnWidths(_models, modelWidths ?? DefaultModelColumnWidths);
            ApplyColumnWidths(_projects, projectWidths ?? DefaultProjectColumnWidths);
            _codexHistory.ApplyColumnWidths(codexHistoryWidths);
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

    private static void UpdateGridHeaders(DataGridView grid, (string Key, string Zh, string En)[] columns)
    {
        foreach (var (key, zh, en) in columns)
        {
            if (grid.Columns.Contains(key))
            {
                grid.Columns[key].HeaderText = I18n.T(zh, en);
            }
        }
    }

    private DataGridView CreateGrid((string Key, string Zh, string En)[] columns, Dictionary<string, int>? defaultWidths = null)
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
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
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
        foreach (var (key, zh, en) in columns)
        {
            var colIndex = grid.Columns.Add(key, I18n.T(zh, en));
            var col = grid.Columns[colIndex];
            col.MinimumWidth = 40;
            col.Resizable = DataGridViewTriState.True;
            if (defaultWidths != null && defaultWidths.TryGetValue(key, out var defW) && defW >= 30)
            {
                col.Width = defW;
            }
            else
            {
                col.Width = 100;
            }
        }
        EnableDoubleBuffering(grid);
        return grid;
    }

    private static void EnableDoubleBuffering(Control control)
    {
        typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.SetValue(control, true, null);
    }

    private static string FormatRange(DateRange range) => range.From == range.To
        ? range.From.ToString("yyyy-MM-dd")
        : I18n.Format("{0:yyyy-MM-dd} 至 {1:yyyy-MM-dd}", "{0:yyyy-MM-dd} to {1:yyyy-MM-dd}", range.From, range.To);

    private static string FormatTokens(long value) => value switch { >= 1_000_000 => $"{value / 1_000_000d:0.##}M", >= 1_000 => $"{value / 1_000d:0.##}K", _ => value.ToString("N0") };
    private static string FormatCost(decimal? value) => value.HasValue ? "$" + value.Value.ToString("0.00") : "—";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            I18n.LanguageChanged -= HandleLanguageChanged;
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }
}
