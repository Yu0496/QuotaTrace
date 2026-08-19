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
    private readonly Button _pricingButton;
    private readonly Label _apiValue;
    private readonly Label _apiNote;
    private readonly Label _inputValue;
    private readonly Label _cachedValue;
    private readonly Label _outputValue;
    private readonly Label _uncachedInputValue;
    private readonly Label _cacheCreationValue;
    private readonly Label _status;
    private readonly DataGridView _models;
    private readonly DataGridView _projects;
    private readonly DailyBarChartControl _chart;
    private DateRange _customRange;
    private int _previousRangeIndex;
    private bool _ignoreRangeSelection;

    public MainForm(RefreshCoordinator coordinator, AppSettingsStore settingsStore)
    {
        _coordinator = coordinator;
        _settingsStore = settingsStore;
        _customRange = DateRange.LastDays(7);
        _previousRangeIndex = 1;
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
            Width = Math.Max(125, TextRenderer.MeasureText("Antigravity", Font).Width + 36),
            Height = buttonHeight,
            Margin = new Padding(0, 0, 8, 0)
        };
        _providerCombo.Items.AddRange(["全部", "Codex", "Antigravity"]);
        _providerCombo.SelectedIndex = 0;
        _rangeCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = Math.Max(120, TextRenderer.MeasureText("自定义…", Font).Width + 36),
            Height = buttonHeight,
            Margin = new Padding(0, 0, 8, 0)
        };
        _rangeCombo.Items.AddRange(["今天", "7天", "30天", "本月", "自定义…"]);
        _rangeCombo.SelectedIndex = 1;

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
        _pricingButton = new Button
        {
            Text = "获取最新价格",
            Width = Math.Max(128, TextRenderer.MeasureText("获取最新价格", Font).Width + 24),
            Height = buttonHeight,
            Margin = new Padding(0, 0, 6, 0),
            AutoSize = false,
            Padding = new Padding(8, 2, 8, 2)
        };
        _pricingButton.Click += async (_, _) => await UpdatePricingAsync();
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
        toolTip.SetToolTip(_pricingButton, "手动从 OpenAI/Gemini 官方 HTTPS 定价页获取价格；不会上传本地用量数据。启动时不会自动联网更新。");
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
            refresh, _pricingButton, settingsButton
        ]);

        _apiValue = MetricLabel();
        _apiNote = NoteLabel("按公开 API 价格估算（1x token）");
        _inputValue = MetricLabel();
        _cachedValue = MetricLabel();
        _outputValue = MetricLabel();
        _cacheCreationValue = InlineMetricLabel();

        var cardTitleFont = new Font(Font, FontStyle.Regular);
        var cardTitleHeight = TextRenderer.MeasureText("API 等值", cardTitleFont).Height + 8;
        var cardValueHeight = _apiValue.Font.Height + 12;
        var cardNoteHeight = _apiNote.Font.Height + 8;
        var cardContentHeight = cardTitleHeight + cardValueHeight + cardNoteHeight;
        var cardsHeight = cardContentHeight + 36;
        var cards = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Padding = new Padding(12, 4, 12, 4)
        };
        for (var index = 0; index < 4; index++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        cards.Controls.Add(Card("API 等值", _apiValue, _apiNote, cardTitleFont, cardTitleHeight, cardNoteHeight), 0, 0);
        cards.Controls.Add(Card("Input（未命中）", _inputValue, null, cardTitleFont, cardTitleHeight, cardNoteHeight), 1, 0);
        cards.Controls.Add(Card("Cache Read", _cachedValue, null, cardTitleFont, cardTitleHeight, cardNoteHeight), 2, 0);
        cards.Controls.Add(Card("Output", _outputValue, null, cardTitleFont, cardTitleHeight, cardNoteHeight), 3, 0);

        var cacheMissHeight = Math.Max(34, Font.Height + 16);
        var cacheMissLine = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Padding = new Padding(12, 2, 12, 2)
        };
        cacheMissLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        cacheMissLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        cacheMissLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        cacheMissLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        cacheMissLine.Controls.Add(new Label
        {
            Text = "未命中缓存输入：",
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
            Padding = new Padding(8, 0, 0, 0)
        }, 2, 0);
        _cacheCreationValue = InlineMetricLabel();
        cacheMissLine.Controls.Add(_cacheCreationValue, 3, 0);
        toolTip.SetToolTip(cacheMissLine, "Sub2API token 口径：Input=总输入-Cache Read-Cache Creation；Cache Read=缓存读取；Cache Creation=缓存创建。API 等值采用 1x 公开 token 价格，不含 Sub2API 用户/渠道倍率。");

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

        _models = CreateGrid(["模型", "Provider", "Input（未命中）", "Cache Read", "Output", "API 等值"]);
        _projects = CreateGrid(["项目", "Provider", "Tokens", "Input（未命中）", "Output", "API 等值"]);
        var tabs = new TabControl { Dock = DockStyle.Fill, Margin = new Padding(12, 0, 12, 0) };
        var modelPage = new TabPage("按模型"); modelPage.Controls.Add(_models);
        var projectPage = new TabPage("按项目"); projectPage.Controls.Add(_projects);
        var quotaPage = new TabPage("额度");
        quotaPage.Controls.Add(new Label
        {
            Text = "Antigravity quota 只表示官方本地接口返回的剩余/刷新状态，不参与 API 等值计算。\r\nCodex session 日志只提供用量，不提供订阅剩余百分比和重置时间。\r\n请打开 Antigravity 后点击刷新。",
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = Math.Max(92, Font.Height * 3 + 36),
            Padding = new Padding(12, 12, 12, 4)
        });
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
        _apiValue.Text = snapshot.ApiEquivalentUsd.HasValue ? "$" + snapshot.ApiEquivalentUsd.Value.ToString("0.00") : "—";
        _apiNote.Text = snapshot.UnpricedTokens > 0 ? $"另有 {FormatTokens(snapshot.UnpricedTokens)} tokens 未计价" :
            snapshot.CoverageStart.HasValue ? $"精确统计自 {snapshot.CoverageStart.Value.ToLocalTime():yyyy-MM-dd HH:mm}" : "按公开 API 价格估算（1x token）";
        _inputValue.Text = FormatTokens(snapshot.NonCachedInputTokens);
        _cachedValue.Text = FormatTokens(snapshot.CachedTokens);
        _uncachedInputValue.Text = FormatTokens(snapshot.NonCachedInputTokens);
        _cacheCreationValue.Text = FormatTokens(snapshot.CacheCreationTokens);
        _outputValue.Text = FormatTokens(snapshot.OutputTokens);
        _chart.SetData(snapshot.Daily);
        _models.Rows.Clear();
        foreach (var row in snapshot.Models)
            _models.Rows.Add(row.ModelId, row.Provider.ToStorageString(), FormatTokens(row.NonCachedInputTokens), FormatTokens(row.CachedTokens), FormatTokens(row.OutputTokens), FormatCost(row.ApiEquivalentUsd));
        _projects.Rows.Clear();
        foreach (var row in snapshot.Projects)
            _projects.Rows.Add(row.DisplayName, row.Provider.ToStorageString(), FormatTokens(row.Tokens), FormatTokens(row.NonCachedInputTokens), FormatTokens(row.OutputTokens), FormatCost(row.ApiEquivalentUsd));
        var warningText = snapshot.Warnings.Count == 0 ? string.Empty : string.Join("；", snapshot.Warnings.Take(3));
        _status.Text = string.IsNullOrEmpty(warningText)
            ? $"范围：{FormatRange(snapshot.Range)}；最后刷新：{snapshot.RefreshedAt.ToLocalTime():HH:mm:ss}"
            : warningText;
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

    private async Task UpdatePricingAsync()
    {
        if (!_pricingButton.Enabled) return;
        _pricingButton.Enabled = false;
        try
        {
            _status.Text = "正在从官方定价页获取最新价格…";
            var result = await _coordinator.UpdatePricingAsync();
            ApplyCurrentSelection();
            var message = result.UpdatedCount > 0
                ? $"已更新 {result.UpdatedCount} 条价格规则（{result.FetchedAt.ToLocalTime():HH:mm:ss}）"
                : "未更新价格，已保留本地规则";
            if (result.Warnings.Count > 0) message += $"；{result.Warnings[0]}";
            _status.Text = message;
        }
        catch (Exception exception) { _status.Text = $"获取价格失败：{exception.Message}"; }
        finally { _pricingButton.Enabled = true; }
    }

    private void HandleRangeSelectionChanged()
    {
        if (_ignoreRangeSelection) return;
        if (_rangeCombo.SelectedIndex != 4)
        {
            _previousRangeIndex = _rangeCombo.SelectedIndex;
            ApplyCurrentSelection();
            return;
        }

        using var dialog = new DateRangeDialog(_customRange);
        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.SelectedRange is not null)
        {
            _customRange = dialog.SelectedRange;
            _previousRangeIndex = 4;
            ApplyCurrentSelection();
            return;
        }

        _ignoreRangeSelection = true;
        _rangeCombo.SelectedIndex = _previousRangeIndex == 4 ? 1 : _previousRangeIndex;
        _ignoreRangeSelection = false;
    }

    private void ApplyCurrentSelection()
    {
        var provider = _providerCombo.SelectedIndex switch { 1 => ProviderKind.Codex, 2 => ProviderKind.Antigravity, _ => (ProviderKind?)null };
        var range = _rangeCombo.SelectedIndex switch
        {
            0 => DateRange.Today(),
            1 => DateRange.LastDays(7),
            2 => DateRange.LastDays(30),
            3 => DateRange.ThisMonth(),
            4 => _customRange,
            _ => DateRange.LastDays(7)
        };
        ApplySnapshot(_coordinator.BuildSnapshot(range, provider));
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

    private static Control Card(string title, Label value, Label? note, Font titleFont, int titleHeight, int noteHeight)
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


