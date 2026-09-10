using System.Data;
using System.Drawing;
using System.Windows.Forms;
using UsageTray.Services;

namespace UsageTray.UI.Controls;

public sealed class CodexHistoryControl : UserControl
{
    private readonly ComboBox _poolFilterCombo;
    private readonly Label _filterLabel;
    private readonly Label _summaryLabel;
    private readonly DataGridView _grid;
    private readonly Panel _detailPanel;
    private readonly Label _detailPeriodLabel;
    private readonly Label _detailTokenLabel;
    private readonly Label _detailCostLabel;
    private readonly Label _detailModelLabel;
    private readonly FlowLayoutPanel _topBar;

    private IReadOnlyList<CodexHistoricalCycleView> _allCycles = [];
    private bool _isUpdating;

    public event EventHandler? ColumnWidthsChanged;

    public static readonly Dictionary<string, int> BaseColumnWidths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["状态"] = 110,
        ["模型池"] = 150,
        ["周期区间"] = 280,
        ["实际时长"] = 110,
        ["额度变化"] = 145,
        ["已消耗"] = 95,
        ["订阅参考金额"] = 135,
        ["满额预估"] = 145,
        ["模型独立测算"] = 180,
        ["总Tokens"] = 120,
        ["快照数"] = 90
    };

    public static Dictionary<string, int> DefaultColumnWidths => new(BaseColumnWidths, StringComparer.OrdinalIgnoreCase);

    public CodexHistoryControl()
    {
        DoubleBuffered = true;
        Dock = DockStyle.Fill;
        BackColor = Color.White;

        var scale = GetScale();

        // 顶部工具栏
        _topBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = Math.Max(42, (int)(42 * scale)),
            Padding = new Padding((int)(8 * scale), (int)(8 * scale), (int)(8 * scale), (int)(4 * scale)),
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Color.White
        };

        _filterLabel = new Label
        {
            Text = "模型池筛选：",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, (int)(4 * scale), (int)(4 * scale), 0),
            ForeColor = Color.DimGray
        };

        _poolFilterCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = Math.Max(150, (int)(150 * scale)),
            Margin = new Padding(0, 0, (int)(16 * scale), 0)
        };
        _poolFilterCombo.Items.AddRange(["全部", "Codex 主力模型", "GPT-5.3 Spark", "Codex Reserve"]);
        _poolFilterCombo.SelectedIndex = 0;
        _poolFilterCombo.SelectedIndexChanged += (_, _) => ApplyFilter();

        _summaryLabel = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, (int)(4 * scale), 0, 0),
            ForeColor = Color.FromArgb(60, 60, 60)
        };

        _topBar.Controls.AddRange([_filterLabel, _poolFilterCombo, _summaryLabel]);

        // 底部详情面板（不使用容易重叠的百分比 TableLayoutPanel，使用自适应像素绝对纵向布局）
        _detailPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            BackColor = Color.FromArgb(248, 249, 250),
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding((int)(12 * scale), (int)(8 * scale), (int)(12 * scale), (int)(8 * scale))
        };

        _detailPeriodLabel = new Label
        {
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(30, 41, 59),
            Text = "选择上方周期可查看该周期的详细 Token 与快照明细"
        };
        _detailTokenLabel = new Label
        {
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(71, 85, 105),
            Text = "—"
        };
        _detailCostLabel = new Label
        {
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(71, 85, 105),
            Text = "—"
        };
        _detailModelLabel = new Label
        {
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(13, 148, 136),
            Text = "—"
        };

        _detailPanel.Controls.Add(_detailPeriodLabel);
        _detailPanel.Controls.Add(_detailTokenLabel);
        _detailPanel.Controls.Add(_detailCostLabel);
        _detailPanel.Controls.Add(_detailModelLabel);
        _detailPanel.Resize += (_, _) => LayoutDetailLabels();

        // 中间 DataGridView 列表
        _grid = CreateGrid();
        _grid.SelectionChanged += (_, _) => UpdateDetailSelection();
        _grid.ColumnWidthChanged += (_, _) =>
        {
            if (!_isUpdating) ColumnWidthsChanged?.Invoke(this, EventArgs.Empty);
        };

        Controls.Add(_grid);
        Controls.Add(_topBar);
        Controls.Add(_detailPanel);

        UpdateLayoutMetrics();
    }

    private float GetScale()
    {
        var dpi = DeviceDpi > 0 ? DeviceDpi : 96;
        return Math.Max(1.0f, dpi / 96.0f);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdateLayoutMetrics();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        UpdateLayoutMetrics();
    }

    private void UpdateLayoutMetrics()
    {
        var scale = GetScale();
        var baseFont = Font;
        var boldFont = new Font(baseFont, FontStyle.Bold);

        _filterLabel.Font = baseFont;
        _poolFilterCombo.Font = baseFont;
        _summaryLabel.Font = baseFont;

        var lineH = Math.Max(24, TextRenderer.MeasureText("测试", baseFont).Height + (int)(6 * scale));
        _detailPeriodLabel.Font = boldFont;
        _detailTokenLabel.Font = baseFont;
        _detailCostLabel.Font = baseFont;
        _detailModelLabel.Font = baseFont;

        _detailPanel.Height = lineH * 4 + (int)(24 * scale);
        LayoutDetailLabels();

        var headerTextH = TextRenderer.MeasureText("订阅参考金额", boldFont).Height;
        var cellTextH = TextRenderer.MeasureText("Codex 主力模型", baseFont).Height;
        _grid.ColumnHeadersHeight = Math.Max((int)(38 * scale), headerTextH + (int)(16 * scale));
        _grid.RowTemplate.Height = Math.Max((int)(34 * scale), cellTextH + (int)(12 * scale));

        _topBar.Padding = new Padding((int)(8 * scale), (int)(8 * scale), (int)(8 * scale), (int)(4 * scale));
        _filterLabel.Margin = new Padding(0, (int)(4 * scale), (int)(4 * scale), 0);
        _summaryLabel.Margin = new Padding(0, (int)(4 * scale), 0, 0);
        _poolFilterCombo.Margin = new Padding(0, 0, (int)(16 * scale), 0);

        _topBar.Height = Math.Max((int)(44 * scale), _poolFilterCombo.PreferredSize.Height + (int)(14 * scale));
        _poolFilterCombo.Width = Math.Max(150, (int)(150 * scale));

        foreach (DataGridViewRow row in _grid.Rows)
        {
            row.Height = _grid.RowTemplate.Height;
        }
    }

    private void LayoutDetailLabels()
    {
        var scale = GetScale();
        var padX = (int)(12 * scale);
        var padY = (int)(8 * scale);
        var width = Math.Max(100, _detailPanel.ClientSize.Width - padX * 2);
        var lineH = Math.Max(22, (_detailPanel.ClientSize.Height - padY * 2) / 4);

        _detailPeriodLabel.SetBounds(padX, padY, width, lineH);
        _detailTokenLabel.SetBounds(padX, padY + lineH, width, lineH);
        _detailCostLabel.SetBounds(padX, padY + lineH * 2, width, lineH);
        _detailModelLabel.SetBounds(padX, padY + lineH * 3, width, lineH);
    }

    private DataGridView CreateGrid()
    {
        var scale = GetScale();
        var baseFont = Font;
        var headerFont = new Font(baseFont, FontStyle.Bold);
        var headerTextH = TextRenderer.MeasureText("订阅参考金额", headerFont).Height;
        var cellTextH = TextRenderer.MeasureText("Codex 主力模型", baseFont).Height;

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
            BorderStyle = BorderStyle.None,
            EnableHeadersVisualStyles = false,
            GridColor = Color.FromArgb(235, 238, 242),
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight = Math.Max((int)(38 * scale), headerTextH + (int)(16 * scale)),
            RowTemplate = { Height = Math.Max((int)(34 * scale), cellTextH + (int)(12 * scale)) }
        };

        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(245, 247, 250),
            ForeColor = Color.FromArgb(50, 50, 50),
            Font = headerFont,
            Padding = new Padding((int)(8 * scale), (int)(6 * scale), (int)(8 * scale), (int)(6 * scale)),
            WrapMode = DataGridViewTriState.False,
            Alignment = DataGridViewContentAlignment.MiddleLeft
        };
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;

        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            Font = baseFont,
            SelectionBackColor = Color.FromArgb(218, 232, 247),
            SelectionForeColor = Color.FromArgb(20, 20, 20),
            Padding = new Padding((int)(8 * scale), (int)(4 * scale), (int)(8 * scale), (int)(4 * scale)),
            WrapMode = DataGridViewTriState.False,
            Alignment = DataGridViewContentAlignment.MiddleLeft
        };

        var columns = new (string Name, string Text, DataGridViewContentAlignment Align)[]
        {
            ("状态", "状态", DataGridViewContentAlignment.MiddleLeft),
            ("模型池", "模型池", DataGridViewContentAlignment.MiddleLeft),
            ("周期区间", "周期区间", DataGridViewContentAlignment.MiddleLeft),
            ("实际时长", "实际时长", DataGridViewContentAlignment.MiddleCenter),
            ("额度变化", "额度变化", DataGridViewContentAlignment.MiddleCenter),
            ("已消耗", "已消耗", DataGridViewContentAlignment.MiddleRight),
            ("订阅参考金额", "订阅参考金额", DataGridViewContentAlignment.MiddleRight),
            ("满额预估", "满额预估", DataGridViewContentAlignment.MiddleRight),
            ("模型独立测算", "模型独立测算", DataGridViewContentAlignment.MiddleLeft),
            ("总Tokens", "总 Tokens", DataGridViewContentAlignment.MiddleRight),
            ("快照数", "快照数", DataGridViewContentAlignment.MiddleRight)
        };

        var defaultWidths = GetDefaultScaledColumnWidths();

        foreach (var col in columns)
        {
            var width = defaultWidths.TryGetValue(col.Name, out var w) ? w : (int)(110 * scale);
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = col.Name,
                HeaderText = col.Text,
                Width = width,
                MinimumWidth = (int)(50 * scale),
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = { Alignment = col.Align }
            });
        }

        return grid;
    }

    public Dictionary<string, int> GetDefaultScaledColumnWidths()
    {
        var scale = GetScale();
        return BaseColumnWidths.ToDictionary(
            k => k.Key,
            v => Math.Max(v.Value, (int)Math.Round(v.Value * scale)),
            StringComparer.OrdinalIgnoreCase
        );
    }

    public void SetCycles(IReadOnlyList<CodexHistoricalCycleView> cycles)
    {
        _allCycles = cycles ?? [];
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        _isUpdating = true;
        _grid.SuspendLayout();
        try
        {
            var filter = _poolFilterCombo.SelectedIndex switch
            {
                1 => "standard",
                2 => "spark",
                3 => "reserve",
                _ => null
            };

            var filtered = filter == null
                ? _allCycles
                : _allCycles.Where(c => c.PoolCategory == filter).ToList();

            var activeCount = filtered.Count(c => c.IsActive);
            var resetCount = filtered.Count(c => !c.IsActive);
            _summaryLabel.Text = $"共 {filtered.Count} 个周期（进行中 {activeCount} 个，已重置 {resetCount} 个）";

            _grid.Rows.Clear();

            foreach (var cycle in filtered)
            {
                var rowIndex = _grid.Rows.Add();
                var row = _grid.Rows[rowIndex];
                row.Height = _grid.RowTemplate.Height;
                row.Tag = cycle;

                var statusText = cycle.IsActive ? "● 进行中" : "已重置";
                var poolText = cycle.PoolDisplayName;
                var rangeEnd = (!cycle.IsActive && cycle.ActualEnd.HasValue && cycle.ActualEnd.Value < cycle.ResetAt - TimeSpan.FromHours(12))
                    ? cycle.ActualEnd.Value
                    : cycle.ResetAt;
                var rangeText = $"{cycle.CycleStart.ToLocalTime():yyyy-MM-dd HH:mm} ~ {rangeEnd.ToLocalTime():MM-dd HH:mm}";
                var durationText = FormatDuration(cycle.Duration);

                var minStr = cycle.MinRemainingFraction.HasValue ? $"{cycle.MinRemainingFraction.Value * 100:0.#}%" : "—";
                var remChangeText = cycle.MinRemainingFraction.HasValue
                    ? $"100% → {minStr}"
                    : (cycle.StartRemainingFraction.HasValue ? $"{cycle.StartRemainingFraction.Value * 100:0.#}% → —" : "—");

                var consumedVal = cycle.FullCycleConsumedFraction ?? cycle.ConsumedFraction;
                var consumedText = consumedVal.HasValue
                    ? $"{consumedVal.Value * 100:0.#}%"
                    : "—";

                var costText = cycle.CycleCostUsd.HasValue ? $"${cycle.CycleCostUsd.Value:F2}" : "—";
                var fullEstText = cycle.EstimatedWeeklyCostUsd.HasValue ? $"约 ${cycle.EstimatedWeeklyCostUsd.Value:F2}" : "—";
                var tokensText = FormatTokens(cycle.TotalTokens);
                var snapCountText = $"{cycle.SnapshotCount} 次";

                var modelEstText = "—";
                if (cycle.ModelProjections is { Count: > 0 })
                {
                    modelEstText = string.Join(" | ", cycle.ModelProjections.Select(p => $"{ShortenModel(p.ModelId)}: 约${p.EstimatedWeeklyCostUsd:F0}"));
                }

                row.Cells["状态"].Value = statusText;
                row.Cells["模型池"].Value = poolText;
                row.Cells["周期区间"].Value = rangeText;
                row.Cells["实际时长"].Value = durationText;
                row.Cells["额度变化"].Value = remChangeText;
                row.Cells["已消耗"].Value = consumedText;
                row.Cells["订阅参考金额"].Value = costText;
                row.Cells["满额预估"].Value = fullEstText;
                row.Cells["模型独立测算"].Value = modelEstText;
                row.Cells["总Tokens"].Value = tokensText;
                row.Cells["快照数"].Value = snapCountText;

                if (cycle.StartRemainingFraction.HasValue && cycle.StartRemainingFraction.Value < 0.995 && cycle.MinRemainingFraction.HasValue)
                {
                    row.Cells["额度变化"].ToolTipText = $"周期标准额度: 100%\r\n首次采样快照: {cycle.StartRemainingFraction.Value * 100:0.#}%\r\n周期最低剩余: {minStr}\r\n周期实际消耗: {consumedText}";
                }
                else if (cycle.MinRemainingFraction.HasValue)
                {
                    row.Cells["额度变化"].ToolTipText = $"周期标准额度: 100%\r\n周期最低剩余: {minStr}\r\n周期实际消耗: {consumedText}";
                }

                if (cycle.ModelProjections is { Count: > 0 })
                {
                    var tipLines = new List<string> { "【各模型独立测算体系】" };
                    foreach (var mp in cycle.ModelProjections)
                    {
                        var source = mp.IsFromCurrentCycle ? $"本周期实测 (消耗 {mp.ConsumedFraction * 100:0.#}%)" : "历史同套餐基准";
                        tipLines.Add($"• {mp.ModelId}: 约 ${mp.EstimatedWeeklyCostUsd:F2} ({source})");
                        if (mp.IntervalCostUsd > 0)
                        {
                            tipLines.Add($"  本周期产生参考金额: ${mp.IntervalCostUsd:F2}");
                        }
                    }
                    var tip = string.Join(Environment.NewLine, tipLines);
                    row.Cells["模型独立测算"].ToolTipText = tip;
                    row.Cells["满额预估"].ToolTipText = $"综合预估: {fullEstText}\r\n" + tip;
                }

                if (cycle.IsActive)
                {
                    row.Cells["状态"].Style.ForeColor = Color.FromArgb(16, 120, 90);
                    row.Cells["状态"].Style.Font = new Font(Font, FontStyle.Bold);
                }
                else
                {
                    row.Cells["状态"].Style.ForeColor = Color.Gray;
                }
            }

            if (_grid.Rows.Count > 0)
            {
                _grid.Rows[0].Selected = true;
                UpdateDetailSelection();
            }
            else
            {
                _detailPeriodLabel.Text = "当前筛选条件下无历史周期记录";
                _detailTokenLabel.Text = "—";
                _detailCostLabel.Text = "—";
                _detailModelLabel.Text = "—";
            }
        }
        finally
        {
            _grid.ResumeLayout();
            _isUpdating = false;
        }
    }

    private void UpdateDetailSelection()
    {
        if (_grid.SelectedRows.Count == 0 || _grid.SelectedRows[0].Tag is not CodexHistoricalCycleView cycle)
        {
            return;
        }

        var statusDesc = cycle.IsActive ? "【当前进行中】" : "【已重置】";
        var firstCapStr = cycle.FirstCapturedAt.HasValue ? cycle.FirstCapturedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "无";
        var lastCapStr = cycle.LastCapturedAt.HasValue ? cycle.LastCapturedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "无";
        var rangeEnd = (!cycle.IsActive && cycle.ActualEnd.HasValue && cycle.ActualEnd.Value < cycle.ResetAt - TimeSpan.FromHours(12))
            ? cycle.ActualEnd.Value
            : cycle.ResetAt;
        var earlyNote = (!cycle.IsActive && cycle.ActualEnd.HasValue && cycle.ActualEnd.Value < cycle.ResetAt - TimeSpan.FromHours(12))
            ? $" (提前重置，原定: {cycle.ResetAt.ToLocalTime():yyyy-MM-dd HH:mm:ss})"
            : "";

        _detailPeriodLabel.Text = $"{statusDesc} {cycle.PoolDisplayName} | 区间: {cycle.CycleStart.ToLocalTime():yyyy-MM-dd HH:mm:ss} ~ {rangeEnd.ToLocalTime():yyyy-MM-dd HH:mm:ss} (时长 {FormatDuration(cycle.Duration)}){earlyNote} | 快照: {cycle.SnapshotCount} 次 (最早: {firstCapStr}, 最新: {lastCapStr})";

        var nonCached = cycle.NonCachedInputTokens;
        var hitRate = cycle.CacheHitRate;
        _detailTokenLabel.Text = $"Token 明细: 未命中输入 {FormatTokens(nonCached)} | 缓存读取 {FormatTokens(cycle.CycleCachedTokens)} (命中率 {hitRate:F1}%) | 缓存写入 {FormatTokens(cycle.CycleCacheCreationTokens)} | 输出 {FormatTokens(cycle.CycleOutputTokens)} | 总计 {FormatTokens(cycle.TotalTokens)}";

        var costStr = cycle.CycleCostUsd.HasValue ? $"${cycle.CycleCostUsd.Value:F2}" : "—";
        var fullStr = cycle.EstimatedWeeklyCostUsd.HasValue ? $"约 ${cycle.EstimatedWeeklyCostUsd.Value:F2}" : "—";
        var noteStr = !string.IsNullOrWhiteSpace(cycle.EstimateNote) ? $" ({cycle.EstimateNote})" : "";
        _detailCostLabel.Text = $"订阅参考金额: {costStr} | 周满额样本预估: {fullStr}{noteStr}";

        if (cycle.ModelProjections is { Count: > 0 })
        {
            var parts = cycle.ModelProjections.Select(p =>
                $"{p.ModelId} 约 ${p.EstimatedWeeklyCostUsd:F2} ({(p.IsFromCurrentCycle ? $"实测{p.ConsumedFraction * 100:0.#}%" : "历史基准")})");
            _detailModelLabel.Text = $"模型独立测算: {string.Join(" | ", parts)}";
        }
        else if (cycle.PoolCategory == "standard")
        {
            _detailModelLabel.Text = "模型独立测算: 暂无足够独立模型样本";
        }
        else
        {
            _detailModelLabel.Text = "模型独立测算: 单一模型池（与综合预估一致）";
        }
    }

    private static string ShortenModel(string model)
    {
        if (model.StartsWith("gpt-6-", StringComparison.OrdinalIgnoreCase)) return model[6..];
        if (model.StartsWith("gpt-5.", StringComparison.OrdinalIgnoreCase) && model.Length > 8) return model[8..];
        if (model.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)) return model[4..];
        return model;
    }

    private static string FormatDuration(TimeSpan d)
    {
        if (d.TotalDays >= 1.0)
        {
            var days = (int)d.TotalDays;
            var hours = d.Hours;
            return hours > 0 ? $"{days}天{hours}时" : $"{days}天整";
        }
        if (d.TotalHours >= 1.0)
        {
            var hours = (int)d.TotalHours;
            var mins = d.Minutes;
            return mins > 0 ? $"{hours}时{mins}分" : $"{hours}小时";
        }
        return $"{Math.Max(1, (int)d.TotalMinutes)}分钟";
    }

    private static string FormatTokens(long count)
    {
        if (count >= 1_000_000_000) return $"{count / 1_000_000_000.0:0.##}B";
        if (count >= 1_000_000) return $"{count / 1_000_000.0:0.##}M";
        if (count >= 1_000) return $"{count / 1_000.0:0.#}K";
        return count.ToString("N0");
    }

    public Dictionary<string, int> GetColumnWidths()
    {
        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewColumn col in _grid.Columns)
        {
            if (col.Width > 0) dict[col.Name] = col.Width;
        }
        return dict;
    }

    public void ApplyColumnWidths(Dictionary<string, int>? widths)
    {
        var defaultScaled = GetDefaultScaledColumnWidths();
        var scale = GetScale();
        _isUpdating = true;
        try
        {
            foreach (DataGridViewColumn col in _grid.Columns)
            {
                var minSafeWidth = defaultScaled.TryGetValue(col.Name, out var def)
                    ? (int)(def * 0.75f)
                    : (int)(60 * scale);

                if (widths != null && widths.TryGetValue(col.Name, out var userW) && userW >= minSafeWidth)
                {
                    col.Width = userW;
                }
                else
                {
                    // 若用户配置中保存的宽度小于当前 DPI 最小安全宽度（来自低 DPI 屏幕或被挤压），自动恢复为当前 DPI 的标准缩放宽度
                    col.Width = defaultScaled.TryGetValue(col.Name, out var safeW) ? safeW : (int)(110 * scale);
                }
            }
        }
        finally
        {
            _isUpdating = false;
        }
    }
}
