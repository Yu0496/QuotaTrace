using System.Data;
using UsageTray.App;
using UsageTray.Core;
using UsageTray.Pricing;
using UsageTray.Services;

namespace UsageTray.UI;

public sealed class PricingViewerForm : Form
{
    private readonly Func<IReadOnlyList<PricingRule>> _rulesProvider;
    private readonly FlowLayoutPanel _topPanel;
    private readonly Label _filterLabel;
    private readonly ComboBox _providerFilter;
    private readonly Label _searchLabel;
    private readonly TextBox _searchBox;
    private readonly Button _refreshButton;
    private readonly DataGridView _grid;
    private readonly Panel _bottomPanel;
    private readonly Label _statusLabel;
    private readonly Button _closeButton;
    private Font _cellFont = null!;
    private Font _headerFont = null!;
    private int _rowMinHeight;
    private int _lastColumnMinWidth;
    private bool _initialSized;
    private IReadOnlyList<PricingRule> _rules;

    public PricingViewerForm(RefreshCoordinator coordinator)
        : this(() => coordinator.Pricing.Rules)
    {
    }

    internal PricingViewerForm(Func<IReadOnlyList<PricingRule>> rulesProvider)
    {
        _rulesProvider = rulesProvider;
        _rules = _rulesProvider();

        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font("Segoe UI", 9F);
        _cellFont ??= new Font(Font, FontStyle.Regular);
        _headerFont ??= new Font(Font, FontStyle.Bold);
        Icon = AppIcon.Create();
        Text = I18n.T("当前模型与定价规则", "Current Models & Pricing Rules");
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;

        _filterLabel = new Label
        {
            Text = I18n.T("筛选：", "Filter: "),
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft
        };

        _providerFilter = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _providerFilter.Items.AddRange([I18n.T("全部 Provider", "All Providers"), "Codex", "Antigravity"]);
        _providerFilter.SelectedIndex = 0;
        _providerFilter.SelectedIndexChanged += (_, _) => ApplyFilter();

        _searchLabel = new Label
        {
            Text = I18n.T("搜索：", "Search: "),
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft
        };

        _searchBox = new TextBox
        {
            PlaceholderText = I18n.T("按模型名称搜索…", "Search by model name...")
        };
        _searchBox.TextChanged += (_, _) => ApplyFilter();

        _refreshButton = new Button
        {
            Text = I18n.T("重新读取", "Reload"),
            AutoSize = false
        };
        _refreshButton.Click += (_, _) =>
        {
            _rules = _rulesProvider();
            ApplyFilter();
        };

        _topPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.FromArgb(248, 250, 252)
        };
        _topPanel.Controls.Add(_filterLabel);
        _topPanel.Controls.Add(_providerFilter);
        _topPanel.Controls.Add(_searchLabel);
        _topPanel.Controls.Add(_searchBox);
        _topPanel.Controls.Add(_refreshButton);

        _grid = CreateGrid();
        _grid.SizeChanged += (_, _) => AdjustLastColumnWidth();

        _statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(71, 85, 105)
        };

        _closeButton = new Button
        {
            Text = I18n.T("关闭", "Close"),
            DialogResult = DialogResult.OK,
            Dock = DockStyle.Right,
            AutoSize = false
        };

        _bottomPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            BackColor = Color.FromArgb(248, 250, 252)
        };
        _bottomPanel.Controls.Add(_statusLabel);
        _bottomPanel.Controls.Add(_closeButton);

        Controls.Add(_grid);
        Controls.Add(_topPanel);
        Controls.Add(_bottomPanel);

        AcceptButton = _closeButton;
        CancelButton = _closeButton;

        UpdateLayoutMetrics();
        ApplyFilter();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        UpdateLayoutMetrics();

        if (!_initialSized)
        {
            _initialSized = true;
            var scale = GetScale();
            var workArea = Screen.FromControl(this).WorkingArea;
            if (workArea.Width <= 0 || workArea.Height <= 0)
                workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);

            var margin = (int)Math.Round(48 * scale);
            var minTopWidth = _topPanel.PreferredSize.Width + (int)Math.Round(16 * scale);
            var targetWidth = Math.Min(Math.Max((int)Math.Round(1360 * scale), minTopWidth), Math.Max(640, workArea.Width - margin));
            var targetHeight = Math.Min((int)Math.Round(680 * scale), Math.Max(420, workArea.Height - margin));
            var minWidth = Math.Min(Math.Max((int)Math.Round(780 * scale), minTopWidth), targetWidth);
            var minHeight = Math.Min((int)Math.Round(440 * scale), targetHeight);

            MinimumSize = new Size(minWidth, minHeight);
            ClientSize = new Size(targetWidth, targetHeight);
            if (Owner is not null || Parent is not null)
                CenterToParent();
            else if (StartPosition != FormStartPosition.Manual)
                CenterToScreen();
        }

        AdjustLastColumnWidth();
        _grid.AutoResizeRows(DataGridViewAutoSizeRowsMode.AllCells);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        UpdateLayoutMetrics();
        AdjustLastColumnWidth();
        _grid.AutoResizeRows(DataGridViewAutoSizeRowsMode.AllCells);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        var oldCellFont = _cellFont;
        var oldHeaderFont = _headerFont;
        _cellFont = new Font(Font, FontStyle.Regular);
        _headerFont = new Font(Font, FontStyle.Bold);

        base.OnFontChanged(e);

        if (_grid is not null)
        {
            UpdateLayoutMetrics();
            AdjustLastColumnWidth();
            _grid.AutoResizeRows(DataGridViewAutoSizeRowsMode.AllCells);
        }

        if (!ReferenceEquals(oldCellFont, _cellFont))
            oldCellFont?.Dispose();
        if (!ReferenceEquals(oldHeaderFont, _headerFont))
            oldHeaderFont?.Dispose();
    }

    private float GetScale()
    {
        var dpi = DeviceDpi > 0 ? DeviceDpi : 96;
        return Math.Max(1.0f, dpi / 96.0f);
    }

    private Size MeasureText(string text, Font font)
    {
        if (IsHandleCreated)
        {
            using var g = CreateGraphics();
            return TextRenderer.MeasureText(g, text, font);
        }
        return TextRenderer.MeasureText(text, font);
    }

    internal void UpdateLayoutMetrics()
    {
        var scale = GetScale();

        var cellTextH = Math.Max(
            MeasureText(I18n.T("重新读取", "Reload"), _cellFont).Height,
            MeasureText("Agpy0123", _cellFont).Height);
        var headerTextH = MeasureText(I18n.T("模型模式 (Pattern)", "Model Pattern"), _headerFont).Height;
        var buttonHeight = Math.Max((int)Math.Round(32 * scale), cellTextH + (int)Math.Round(12 * scale));

        _topPanel.SuspendLayout();
        _bottomPanel.SuspendLayout();
        _grid.SuspendLayout();
        try
        {
            _filterLabel.Font = _cellFont;
            _providerFilter.Font = _cellFont;
            _searchLabel.Font = _cellFont;
            _searchBox.Font = _cellFont;
            _refreshButton.Font = _cellFont;
            _statusLabel.Font = _cellFont;
            _closeButton.Font = _cellFont;

            var padX = (int)Math.Round(12 * scale);
            var padY = (int)Math.Round(8 * scale);
            var gap = (int)Math.Round(10 * scale);
            var labelGap = (int)Math.Round(4 * scale);

            _providerFilter.Width = Math.Max(
                (int)Math.Round(150 * scale),
                MeasureText(I18n.T("全部 Provider", "All Providers"), _cellFont).Width +
                Math.Max((int)Math.Round(42 * scale), SystemInformation.VerticalScrollBarWidth + (int)Math.Round(24 * scale)));
            _providerFilter.Height = buttonHeight;

            _searchBox.Width = Math.Max(
                (int)Math.Round(220 * scale),
                MeasureText(I18n.T("按模型名称搜索…", "Search by model name..."), _cellFont).Width + (int)Math.Round(36 * scale));

            _refreshButton.Width = Math.Max(
                (int)Math.Round(96 * scale),
                MeasureText(_refreshButton.Text, _cellFont).Width + (int)Math.Round(28 * scale));
            _refreshButton.Height = buttonHeight;

            _closeButton.Width = Math.Max(
                (int)Math.Round(90 * scale),
                MeasureText(_closeButton.Text, _cellFont).Width + (int)Math.Round(28 * scale));
            _closeButton.Height = buttonHeight;

            var rowHeight = Math.Max(buttonHeight, Math.Max(_providerFilter.Height, _searchBox.PreferredHeight));
            var filterTop = Math.Max(0, (rowHeight - _filterLabel.PreferredHeight) / 2);
            var searchLabelTop = Math.Max(0, (rowHeight - _searchLabel.PreferredHeight) / 2);
            var comboTop = Math.Max(0, (rowHeight - _providerFilter.Height) / 2);
            var boxTop = Math.Max(0, (rowHeight - _searchBox.PreferredHeight) / 2);
            var buttonTop = Math.Max(0, (rowHeight - _refreshButton.Height) / 2);

            _filterLabel.Margin = new Padding(0, filterTop, labelGap, 0);
            _providerFilter.Margin = new Padding(0, comboTop, gap, 0);
            _searchLabel.Margin = new Padding(0, searchLabelTop, labelGap, 0);
            _searchBox.Margin = new Padding(0, boxTop, gap, 0);
            _refreshButton.Margin = new Padding(0, buttonTop, 0, 0);

            _topPanel.Padding = new Padding(padX, padY, padX, padY);
            _topPanel.Height = rowHeight + padY * 2 + (int)Math.Round(2 * scale);

            _bottomPanel.Padding = new Padding(padX, padY, padX, padY);
            _bottomPanel.Height = buttonHeight + padY * 2;
            _statusLabel.Padding = new Padding(0, 0, gap, 0);

            var cellPadX = (int)Math.Round(8 * scale);
            var headerPadY = (int)Math.Round(6 * scale);
            var cellPadY = (int)Math.Round(5 * scale);

            _grid.Font = _cellFont;
            _grid.ColumnHeadersDefaultCellStyle.Font = _headerFont;
            _grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(cellPadX, headerPadY, cellPadX, headerPadY);
            _grid.DefaultCellStyle.Font = _cellFont;
            _grid.DefaultCellStyle.Padding = new Padding(cellPadX, cellPadY, cellPadX, cellPadY);

            _grid.ColumnHeadersHeight = Math.Max(
                (int)Math.Round(38 * scale),
                headerTextH + headerPadY * 2 + (int)Math.Round(8 * scale));

            _rowMinHeight = Math.Max(
                (int)Math.Round(34 * scale),
                cellTextH + cellPadY * 2 + (int)Math.Round(8 * scale));
            _grid.RowTemplate.Height = _rowMinHeight;
            _grid.RowTemplate.MinimumHeight = _rowMinHeight;

            foreach (DataGridViewRow row in _grid.Rows)
            {
                row.MinimumHeight = _rowMinHeight;
            }

            var columnSpecs = new (int MinBasePx, string Sample)[]
            {
                (96, "Antigravity"),
                (200, "gemini-3.6-flash-tiered*"),
                (88, "Wildcard"),
                (84, "$100.00"),
                (88, "$0.3125"),
                (88, "$31.25"),
                (84, "$187.50"),
                (220, ">$50 in / $187.5 out (>272K)"),
                (102, "2026-11-10"),
                (320, "非促销 Token 基准；Fast 采用订阅倍率")
            };

            var extraPad = cellPadX * 2 + (int)Math.Round(14 * scale);
            for (var i = 0; i < _grid.Columns.Count && i < columnSpecs.Length; i++)
            {
                var col = _grid.Columns[i];
                var spec = columnSpecs[i];
                var headerW = MeasureText(col.HeaderText, _headerFont).Width + extraPad;
                var sampleW = MeasureText(spec.Sample, _cellFont).Width + extraPad;
                var width = Math.Max((int)Math.Round(spec.MinBasePx * scale), Math.Max(headerW, sampleW));

                col.MinimumWidth = i == _grid.Columns.Count - 1
                    ? Math.Max((int)Math.Round(220 * scale), headerW)
                    : Math.Max((int)Math.Round(spec.MinBasePx * scale), headerW);
                col.Width = width;
                if (i == _grid.Columns.Count - 1)
                    _lastColumnMinWidth = width;
            }
        }
        finally
        {
            _grid.ResumeLayout();
            _bottomPanel.ResumeLayout();
            _topPanel.ResumeLayout();
        }
    }

    private void AdjustLastColumnWidth()
    {
        if (_grid.Columns.Count == 0) return;
        var otherWidth = 0;
        for (var i = 0; i < _grid.Columns.Count - 1; i++)
            otherWidth += _grid.Columns[i].Width;

        var scrollWidth = _grid.Controls.OfType<VScrollBar>().FirstOrDefault(s => s.Visible)?.Width
            ?? SystemInformation.VerticalScrollBarWidth;
        var available = _grid.ClientSize.Width - scrollWidth - 2;
        var target = Math.Max(_lastColumnMinWidth, available - otherWidth);
        if (_grid.Columns[^1].Width != target)
        {
            _grid.Columns[^1].Width = target;
            if (IsHandleCreated)
                _grid.AutoResizeRows(DataGridViewAutoSizeRowsMode.AllCells);
        }
    }

    private void ApplyFilter()
    {
        var provider = _providerFilter.SelectedIndex switch
        {
            1 => "Codex",
            2 => "Antigravity",
            _ => null
        };
        var search = _searchBox.Text.Trim();

        var filtered = _rules.Where(r =>
        {
            if (provider is not null && !string.Equals(r.Provider, provider, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.IsNullOrEmpty(search) &&
                !r.ModelPattern.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !r.Provider.Contains(search, StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }).ToList();

        _grid.SuspendLayout();
        try
        {
            _grid.Rows.Clear();
            foreach (var rule in filtered)
            {
                var longCtx = rule.LongContextPrice is not null
                    ? $">${rule.LongContextPrice.InputPerMillionUsd:0.####} in / ${rule.LongContextPrice.OutputPerMillionUsd:0.####} out (>{rule.LongContextThresholdTokens / 1000}K)"
                    : "—";

                var cacheRead = rule.CacheReadPerMillionUsd.HasValue ? $"${rule.CacheReadPerMillionUsd.Value:0.####}" : "—";
                var cacheWrite = rule.CacheWritePerMillionUsd.HasValue ? $"${rule.CacheWritePerMillionUsd.Value:0.####}" : "—";

                var defaultBasis = I18n.T("自定义参考基准", "Custom Reference");
                var sourceLbl = I18n.T("。来源：", ". Source: ");

                var rowIndex = _grid.Rows.Add(
                    rule.Provider,
                    rule.ModelPattern,
                    rule.MatchMode.ToString(),
                    $"${rule.InputPerMillionUsd:0.####}",
                    cacheRead,
                    cacheWrite,
                    $"${rule.OutputPerMillionUsd:0.####}",
                    longCtx,
                    rule.LastVerifiedAt.ToString("yyyy-MM-dd"),
                    rule.UnverifiedReason ?? rule.ReferenceBasis ?? defaultBasis
                );
                var row = _grid.Rows[rowIndex];
                row.MinimumHeight = _rowMinHeight;
                if (rule.UnverifiedReason is not null)
                    for (var col = 3; col <= 7; col++) row.Cells[col].Value = "—";
                foreach (DataGridViewCell cell in row.Cells)
                    cell.ToolTipText = rule.UnverifiedReason ?? $"{rule.ReferenceBasis ?? defaultBasis}{sourceLbl}{rule.SourceUrl}";
            }
        }
        finally
        {
            _grid.ResumeLayout();
        }

        _grid.AutoResizeRows(DataGridViewAutoSizeRowsMode.AllCells);

        _statusLabel.Text = I18n.Format(
            "共显示 {0} / {1} 条价格规则；单位均为 $/1M Tokens。固定参考基准，非实际账单。",
            "Showing {0} / {1} rules; unit $/1M Tokens. Fixed benchmark, not actual bills.",
            filtered.Count, _rules.Count);
    }

    private DataGridView CreateGrid()
    {
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
            EnableHeadersVisualStyles = false,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            BorderStyle = BorderStyle.None,
            GridColor = Color.FromArgb(226, 232, 240),
            Font = _cellFont
        };
        typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.SetValue(grid, true, null);
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(241, 245, 249),
            ForeColor = Color.FromArgb(30, 41, 59),
            Font = _headerFont,
            Alignment = DataGridViewContentAlignment.MiddleLeft,
            WrapMode = DataGridViewTriState.False
        };
        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            Font = _cellFont,
            Alignment = DataGridViewContentAlignment.MiddleLeft,
            WrapMode = DataGridViewTriState.False,
            SelectionBackColor = Color.FromArgb(224, 242, 254),
            SelectionForeColor = Color.FromArgb(15, 23, 42)
        };

        var columns = new (string Key, string Zh, string En)[]
        {
            ("Provider", "Provider", "Provider"),
            ("ModelPattern", "模型模式 (Pattern)", "Model Pattern"),
            ("MatchMode", "匹配类型", "Match Type"),
            ("InputPrice", "输入价格", "Input Price"),
            ("CacheRead", "缓存读取", "Cache Read"),
            ("CacheWrite", "缓存创建", "Cache Write"),
            ("OutputPrice", "输出价格", "Output Price"),
            ("LongContextPrice", "长上下文价格", "Long Context Price"),
            ("VerifiedDate", "核验日期", "Verified Date"),
            ("ReferenceBasis", "参考依据", "Reference Basis")
        };
        foreach (var col in columns)
        {
            var idx = grid.Columns.Add(col.Key, I18n.T(col.Zh, col.En));
            grid.Columns[idx].SortMode = DataGridViewColumnSortMode.NotSortable;
        }

        grid.Columns[9].DefaultCellStyle.WrapMode = DataGridViewTriState.True;

        return grid;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cellFont?.Dispose();
            _headerFont?.Dispose();
        }
        base.Dispose(disposing);
    }
}
