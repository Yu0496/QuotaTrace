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
    private readonly Label _status;
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
        var toolTip = new ToolTip();
        toolTip.SetToolTip(refresh, "增量刷新：仅重新解析新增或发生变化的本地记录。程序启动时会自动全量读取一次；全量重读请到设置中执行。");
        toolTip.SetToolTip(_rangeCombo, "选择自定义…后填写开始日期和结束日期，按本地日历统计。");
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

        var cardTitleFont = new Font(Font, FontStyle.Regular);
        var cardTitleHeight = TextRenderer.MeasureText("API 等值", cardTitleFont).Height + 6;
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
        cards.Controls.Add(Card("API 等值 (分应用)", apiValuePanel, null, cardTitleFont, cardTitleHeight, 0), 0, 0);
        cards.Controls.Add(Card("Input（未命中）", _inputValue, null, cardTitleFont, cardTitleHeight, 0), 1, 0);
        cards.Controls.Add(Card("Cache Read", _cachedValue, null, cardTitleFont, cardTitleHeight, 0), 2, 0);
        cards.Controls.Add(Card("Output", _outputValue, null, cardTitleFont, cardTitleHeight, 0), 3, 0);



        var cacheMissHeight = Math.Max(32, Font.Height + 14);
        var cacheMissLine = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            RowCount = 1,
            Padding = new Padding(12, 2, 12, 2)
        };
        cacheMissLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        cacheMissLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        cacheMissLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        cacheMissLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        cacheMissLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        cacheMissLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));

        cacheMissLine.Controls.Add(new Label
        {
            Text = "未命中：",
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray
        }, 0, 0);
        _uncachedInputValue = InlineMetricLabel();
        cacheMissLine.Controls.Add(_uncachedInputValue, 1, 0);

        cacheMissLine.Controls.Add(new Label
        {
            Text = "缓存创建：",
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            Padding = new Padding(6, 0, 0, 0)
        }, 2, 0);
        _cacheCreationValue = InlineMetricLabel();
        cacheMissLine.Controls.Add(_cacheCreationValue, 3, 0);

        cacheMissLine.Controls.Add(new Label
        {
            Text = "命中率：",
            Dock = DockStyle.Fill,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            Padding = new Padding(6, 0, 0, 0)
        }, 4, 0);
        cacheMissLine.Controls.Add(_cacheHitRateValue, 5, 0);

        toolTip.SetToolTip(cacheMissLine, "Sub2API token 口径：Input=总输入-Cache Read-Cache Creation；Cache Read=缓存读取；Cache Creation=缓存创建；命中率=Cache Read / 总 Input。");


        var chartTitleHeight = TextRenderer.MeasureText("每日 API 等值", Font).Height + 10;
        var chartPlotHeight = Math.Max(150, TextRenderer.MeasureText("00-00", Font).Height + 126);
        _chart = new DailyBarChartControl { Dock = DockStyle.Fill, MinimumSize = new Size(0, chartPlotHeight), Margin = new Padding(0) };
        var chartTitle = new Label
        {
            Text = "每日 API 等值",
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

        _models = CreateGrid(["模型", "Provider", "Input（未命中）", "Cache Read", "缓存命中率", "Output", "API 等值"]);
        _projects = CreateGrid(["项目", "Provider", "Tokens", "Input（未命中）", "Cache Read", "缓存命中率", "Output", "API 等值"]);
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
        
        _codexApiValue.Text = snapshot.CodexApiEquivalentUsd.HasValue ? $"Codex: ${snapshot.CodexApiEquivalentUsd.Value:0.00}" : "Codex: $0.00";
        var geminiCost = snapshot.AntigravityGeminiApiEquivalentUsd ?? 0m;
        var claudeCost = snapshot.AntigravityClaudeApiEquivalentUsd ?? 0m;
        _antigravityApiValue.Text = $"Antigravity: ${geminiCost:0.00} | ${claudeCost:0.00}";

        if (snapshot.IsWeeklyCycleWindow)
        {
            // Codex 周期
            if (snapshot.CodexWeeklyCycle is { } cwc && cwc.CycleStart != default && cwc.ResetAt.HasValue)
            {
                var rel = TimeFormatter.FormatRelativeFuture(cwc.ResetAt.Value);
                _codexCycleNote.Text = $"周期：{cwc.CycleStart.ToLocalTime():yyyy-MM-dd HH:mm} ~ {cwc.ResetAt.Value.ToLocalTime():yyyy-MM-dd HH:mm}（{rel}）";
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
                    _codexCycleNote.Text = "周期：暂无周重置时间（按近 7 天）";
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
        _chart.SetData(snapshot.Daily);

        _models.Rows.Clear();
        foreach (var row in snapshot.Models)
        {
            var hitRate = row.InputTokens > 0 ? $"{row.CacheHitRate:F2}%" : "0.00%";
            _models.Rows.Add(row.ModelId, row.Provider.ToStorageString(), FormatTokens(row.NonCachedInputTokens),
                FormatTokens(row.CachedTokens), hitRate, FormatTokens(row.OutputTokens), FormatCost(row.ApiEquivalentUsd));
        }

        _projects.Rows.Clear();
        foreach (var row in snapshot.Projects)
        {
            var hitRate = row.InputTokens > 0 ? $"{row.CacheHitRate:F2}%" : "0.00%";
            _projects.Rows.Add(row.DisplayName, row.Provider.ToStorageString(), FormatTokens(row.Tokens),
                FormatTokens(row.NonCachedInputTokens), FormatTokens(row.CachedTokens), hitRate, FormatTokens(row.OutputTokens), FormatCost(row.ApiEquivalentUsd));
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

    private DataGridView CreateGrid(string[] columns)
    {
        var cellFont = new Font(Font, FontStyle.Regular);
        var headerFont = new Font(Font, FontStyle.Bold);
        var cellTextHeight = TextRenderer.MeasureText("模型", cellFont).Height;
        var headerTextHeight = TextRenderer.MeasureText("API 等值", headerFont).Height;
        var cellPadding = new Padding(4, 4, 4, 4);
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToResizeRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
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
        foreach (var column in columns) grid.Columns.Add(column, column);
        return grid;
    }

    private static string FormatRange(DateRange range) => range.From == range.To
        ? range.From.ToString("yyyy-MM-dd")
        : $"{range.From:yyyy-MM-dd} 至 {range.To:yyyy-MM-dd}";

    private static string FormatTokens(long value) => value switch { >= 1_000_000 => $"{value / 1_000_000d:0.##}M", >= 1_000 => $"{value / 1_000d:0.##}K", _ => value.ToString("N0") };
    private static string FormatCost(decimal? value) => value.HasValue ? "$" + value.Value.ToString("0.00") : "—";
}


