using System.Data;
using UsageTray.Core;
using UsageTray.Pricing;
using UsageTray.Services;

namespace UsageTray.UI;

public sealed class PricingViewerForm : Form
{
    private readonly RefreshCoordinator _coordinator;
    private readonly ComboBox _providerFilter;
    private readonly TextBox _searchBox;
    private readonly DataGridView _grid;
    private readonly Label _statusLabel;
    private IReadOnlyList<PricingRule> _rules;

    public PricingViewerForm(RefreshCoordinator coordinator)
    {
        _coordinator = coordinator;
        _rules = coordinator.Pricing.Rules;

        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 9F);
        Icon = AppIcon.Create();
        Text = "当前模型与定价规则";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;

        var textHeight = TextRenderer.MeasureText("刷新", Font).Height;
        var buttonHeight = Math.Max(32, textHeight + 10);

        _providerFilter = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 130,
            Height = buttonHeight,
            Margin = new Padding(0, 0, 10, 0)
        };
        _providerFilter.Items.AddRange(["全部 Provider", "Codex", "Antigravity"]);
        _providerFilter.SelectedIndex = 0;
        _providerFilter.SelectedIndexChanged += (_, _) => ApplyFilter();

        _searchBox = new TextBox
        {
            Width = 200,
            Height = buttonHeight,
            PlaceholderText = "按模型名称搜索…",
            Margin = new Padding(0, 0, 10, 0)
        };
        _searchBox.TextChanged += (_, _) => ApplyFilter();

        var refreshButton = new Button
        {
            Text = "重新读取",
            Width = 90,
            Height = buttonHeight,
            Margin = Padding.Empty
        };
        refreshButton.Click += (_, _) =>
        {
            _rules = _coordinator.Pricing.Rules;
            ApplyFilter();
        };

        var topPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = buttonHeight + 16,
            Padding = new Padding(12, 8, 12, 8),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.FromArgb(248, 250, 252)
        };
        topPanel.Controls.Add(new Label { Text = "筛选：", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        topPanel.Controls.Add(_providerFilter);
        topPanel.Controls.Add(new Label { Text = "搜索：", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        topPanel.Controls.Add(_searchBox);
        topPanel.Controls.Add(refreshButton);

        _grid = CreateGrid();

        _statusLabel = new Label
        {
            Dock = DockStyle.Left,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(71, 85, 105),
            Margin = new Padding(0, 6, 0, 0)
        };

        var closeButton = new Button
        {
            Text = "关闭",
            DialogResult = DialogResult.OK,
            Width = 85,
            Height = buttonHeight,
            Dock = DockStyle.Right
        };

        var bottomPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = buttonHeight + 16,
            Padding = new Padding(12, 8, 12, 8),
            BackColor = Color.FromArgb(248, 250, 252)
        };
        bottomPanel.Controls.Add(_statusLabel);
        bottomPanel.Controls.Add(closeButton);

        Controls.Add(_grid);
        Controls.Add(topPanel);
        Controls.Add(bottomPanel);

        AcceptButton = closeButton;
        CancelButton = closeButton;

        ApplyFilter();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ClientSize = new Size(880, 520);
        MinimumSize = new Size(760, 400);
        CenterToParent();
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

        _grid.Rows.Clear();
        foreach (var rule in filtered)
        {
            var longCtx = rule.LongContextPrice is not null
                ? $">${rule.LongContextPrice.InputPerMillionUsd:0.##} in / ${rule.LongContextPrice.OutputPerMillionUsd:0.##} out (>{rule.LongContextThresholdTokens / 1000}K)"
                : "—";

            var cacheRead = rule.CacheReadPerMillionUsd.HasValue ? $"${rule.CacheReadPerMillionUsd.Value:0.####}" : "—";
            var cacheWrite = rule.CacheWritePerMillionUsd.HasValue ? $"${rule.CacheWritePerMillionUsd.Value:0.####}" : "—";

            var rowIndex = _grid.Rows.Add(
                rule.Provider,
                rule.ModelPattern,
                rule.MatchMode.ToString(),
                $"${rule.InputPerMillionUsd:0.##}",
                cacheRead,
                cacheWrite,
                $"${rule.OutputPerMillionUsd:0.##}",
                longCtx,
                rule.LastVerifiedAt.ToString("yyyy-MM-dd"),
                rule.UnverifiedReason ?? rule.ReferenceBasis ?? "自定义参考基准"
            );
            if (rule.UnverifiedReason is not null)
                for (var col = 3; col <= 7; col++) _grid.Rows[rowIndex].Cells[col].Value = "—";
            foreach (DataGridViewCell cell in _grid.Rows[rowIndex].Cells)
                cell.ToolTipText = rule.UnverifiedReason ?? $"{rule.ReferenceBasis ?? "自定义参考基准"}。来源：{rule.SourceUrl}";
        }

        _statusLabel.Text = $"共显示 {filtered.Count} / {_rules.Count} 条价格规则；单位均为 $/1M Tokens。固定参考基准，非实际账单。";
    }

    private DataGridView CreateGrid()
    {
        var cellFont = new Font(Font, FontStyle.Regular);
        var headerFont = new Font(Font, FontStyle.Bold);
        var cellPadding = new Padding(6, 4, 6, 4);
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
            ColumnHeadersHeight = 36,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.EnableResizing,
            BorderStyle = BorderStyle.None,
            GridColor = Color.FromArgb(226, 232, 240),
            Font = cellFont
        };
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(241, 245, 249),
            ForeColor = Color.FromArgb(30, 41, 59),
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
            SelectionBackColor = Color.FromArgb(224, 242, 254),
            SelectionForeColor = Color.FromArgb(15, 23, 42)
        };

        string[] columns = [
            "Provider", "模型模式 (Pattern)", "匹配类型", "输入价格", "缓存读取", "缓存创建", "输出价格", "长上下文价格", "核验日期", "参考依据"
        ];
        foreach (var col in columns) grid.Columns.Add(col, col);

        grid.Columns[0].FillWeight = 55;
        grid.Columns[1].FillWeight = 110;
        grid.Columns[2].FillWeight = 45;
        grid.Columns[3].FillWeight = 45;
        grid.Columns[4].FillWeight = 45;
        grid.Columns[5].FillWeight = 45;
        grid.Columns[6].FillWeight = 45;
        grid.Columns[7].FillWeight = 90;
        grid.Columns[8].FillWeight = 50;
        grid.Columns[9].FillWeight = 120;
        grid.Columns[9].DefaultCellStyle.WrapMode = DataGridViewTriState.True;

        return grid;
    }
}
