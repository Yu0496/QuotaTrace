using System.Data;
using System.Drawing;
using System.Windows.Forms;
using UsageTray.App;
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
            Text = I18n.T("模型池筛选：", "Pool Filter: "),
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
        _poolFilterCombo.Items.AddRange([I18n.T("全部", "All"), I18n.T("Codex 主力模型", "Codex Primary"), "GPT-5.3 Spark", "Codex Reserve"]);
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
            Text = I18n.T("选择上方周期可查看该周期的详细 Token 与快照明细", "Select a cycle above to view detailed token and snapshot breakdown.")
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
        I18n.LanguageChanged += RefreshLocalizedTexts;
    }

    private void RefreshLocalizedTexts()
    {
        if (IsDisposed || Disposing || _grid.IsDisposed) return;
        _filterLabel.Text = I18n.T("模型池筛选：", "Pool Filter: ");
        var curFilter = _poolFilterCombo.SelectedIndex;
        _poolFilterCombo.Items.Clear();
        _poolFilterCombo.Items.AddRange([I18n.T("全部", "All"), I18n.T("Codex 主力模型", "Codex Primary"), "GPT-5.3 Spark", "Codex Reserve"]);
        _poolFilterCombo.SelectedIndex = Math.Clamp(curFilter, 0, _poolFilterCombo.Items.Count - 1);

        var colMap = new Dictionary<string, (string Zh, string En)>
        {
            ["状态"] = ("状态", "Status"),
            ["模型池"] = ("模型池", "Pool"),
            ["周期区间"] = ("周期区间", "Cycle Range"),
            ["实际时长"] = ("实际时长", "Duration"),
            ["额度变化"] = ("额度变化", "Quota Change"),
            ["已消耗"] = ("已消耗", "Consumed"),
            ["订阅参考金额"] = ("订阅参考金额", "Sub Ref Cost"),
            ["满额预估"] = ("满额预估", "Est. Full Quota"),
            ["模型独立测算"] = ("模型独立测算", "Model Projections"),
            ["总Tokens"] = ("总 Tokens", "Total Tokens"),
            ["快照数"] = ("快照数", "Snapshots")
        };

        foreach (DataGridViewColumn col in _grid.Columns)
        {
            if (colMap.TryGetValue(col.Name, out var texts))
            {
                col.HeaderText = I18n.T(texts.Zh, texts.En);
            }
        }

        ApplyFilter();
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

        var headerTextH = TextRenderer.MeasureText(I18n.T("订阅参考金额", "Sub Ref Cost"), boldFont).Height;
        var cellTextH = TextRenderer.MeasureText(I18n.T("Codex 主力模型", "Codex Primary"), baseFont).Height;
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
        var headerTextH = TextRenderer.MeasureText(I18n.T("订阅参考金额", "Sub Ref Cost"), headerFont).Height;
        var cellTextH = TextRenderer.MeasureText(I18n.T("Codex 主力模型", "Codex Primary"), baseFont).Height;

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

        var columns = new (string Name, string Zh, string En, DataGridViewContentAlignment Align)[]
        {
            ("状态", "状态", "Status", DataGridViewContentAlignment.MiddleLeft),
            ("模型池", "模型池", "Pool", DataGridViewContentAlignment.MiddleLeft),
            ("周期区间", "周期区间", "Cycle Range", DataGridViewContentAlignment.MiddleLeft),
            ("实际时长", "实际时长", "Duration", DataGridViewContentAlignment.MiddleCenter),
            ("额度变化", "额度变化", "Quota Change", DataGridViewContentAlignment.MiddleCenter),
            ("已消耗", "已消耗", "Consumed", DataGridViewContentAlignment.MiddleRight),
            ("订阅参考金额", "订阅参考金额", "Sub Ref Cost", DataGridViewContentAlignment.MiddleRight),
            ("满额预估", "满额预估", "Est. Full Quota", DataGridViewContentAlignment.MiddleRight),
            ("模型独立测算", "模型独立测算", "Model Projections", DataGridViewContentAlignment.MiddleLeft),
            ("总Tokens", "总 Tokens", "Total Tokens", DataGridViewContentAlignment.MiddleRight),
            ("快照数", "快照数", "Snapshots", DataGridViewContentAlignment.MiddleRight)
        };

        var defaultWidths = GetDefaultScaledColumnWidths();

        foreach (var col in columns)
        {
            var width = defaultWidths.TryGetValue(col.Name, out var w) ? w : (int)(110 * scale);
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = col.Name,
                HeaderText = I18n.T(col.Zh, col.En),
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
        if (IsDisposed || Disposing || _grid.IsDisposed || _grid.Columns.Count == 0) return;
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
            _summaryLabel.Text = I18n.Format(
                "共 {0} 个周期（进行中 {1} 个，已重置 {2} 个）",
                "{0} cycles total ({1} active, {2} reset)",
                filtered.Count, activeCount, resetCount);

            _grid.Rows.Clear();

            foreach (var cycle in filtered)
            {
                var rowIndex = _grid.Rows.Add();
                var row = _grid.Rows[rowIndex];
                row.Height = _grid.RowTemplate.Height;
                row.Tag = cycle;

                var statusText = cycle.IsActive ? I18n.T("● 进行中", "● Active") : I18n.T("已重置", "Reset");
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
                var snapCountText = I18n.Format("{0} 次", "{0} snaps", cycle.SnapshotCount);

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
                    row.Cells["额度变化"].ToolTipText = I18n.Format(
                        "周期标准额度: 100%\r\n首次采样快照: {0:0.#}%\r\n周期最低剩余: {1}\r\n周期实际消耗: {2}",
                        "Benchmark Quota: 100%\r\nFirst Sample: {0:0.#}%\r\nLowest Remaining: {1}\r\nActual Consumed: {2}",
                        cycle.StartRemainingFraction.Value * 100, minStr, consumedText);
                }
                else if (cycle.MinRemainingFraction.HasValue)
                {
                    row.Cells["额度变化"].ToolTipText = I18n.Format(
                        "周期标准额度: 100%\r\n周期最低剩余: {0}\r\n周期实际消耗: {1}",
                        "Benchmark Quota: 100%\r\nLowest Remaining: {0}\r\nActual Consumed: {1}",
                        minStr, consumedText);
                }

                if (cycle.ModelProjections is { Count: > 0 })
                {
                    var tipLines = new List<string> { I18n.T("【各模型独立测算体系】", "[Model Independent Projections]") };
                    foreach (var mp in cycle.ModelProjections)
                    {
                        var source = mp.IsFromCurrentCycle
                            ? I18n.Format("本周期实测 (消耗 {0:0.#}%)", "Measured this cycle ({0:0.#}% consumed)", mp.ConsumedFraction * 100)
                            : I18n.T("历史同套餐基准", "Historical benchmark");
                        tipLines.Add(I18n.Format("• {0}: 约 ${1:F2} ({2})", "• {0}: ~${1:F2} ({2})", mp.ModelId, mp.EstimatedWeeklyCostUsd, source));
                        if (mp.IntervalCostUsd > 0)
                        {
                            tipLines.Add(I18n.Format("  本周期产生参考金额: ${0:F2}", "  Interval ref cost: ${0:F2}", mp.IntervalCostUsd));
                        }
                    }
                    var tip = string.Join(Environment.NewLine, tipLines);
                    row.Cells["模型独立测算"].ToolTipText = tip;
                    row.Cells["满额预估"].ToolTipText = $"{I18n.T("综合预估: ", "Composite Est: ")}{fullEstText}\r\n" + tip;
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
                _detailPeriodLabel.Text = I18n.T("当前筛选条件下无历史周期记录", "No historical cycles found for the selected filter.");
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

        var statusDesc = cycle.IsActive ? I18n.T("【当前进行中】", "[Active]") : I18n.T("【已重置】", "[Reset]");
        var firstCapStr = cycle.FirstCapturedAt.HasValue ? cycle.FirstCapturedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : I18n.T("无", "None");
        var lastCapStr = cycle.LastCapturedAt.HasValue ? cycle.LastCapturedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : I18n.T("无", "None");
        var rangeEnd = (!cycle.IsActive && cycle.ActualEnd.HasValue && cycle.ActualEnd.Value < cycle.ResetAt - TimeSpan.FromHours(12))
            ? cycle.ActualEnd.Value
            : cycle.ResetAt;
        var earlyNote = (!cycle.IsActive && cycle.ActualEnd.HasValue && cycle.ActualEnd.Value < cycle.ResetAt - TimeSpan.FromHours(12))
            ? I18n.Format(" (提前重置，原定: {0:yyyy-MM-dd HH:mm:ss})", " (Early reset, scheduled: {0:yyyy-MM-dd HH:mm:ss})", cycle.ResetAt.ToLocalTime())
            : "";

        _detailPeriodLabel.Text = I18n.Format(
            "{0} {1} | 区间: {2:yyyy-MM-dd HH:mm:ss} ~ {3:yyyy-MM-dd HH:mm:ss} (时长 {4}){5} | 快照: {6} 次 (最早: {7}, 最新: {8})",
            "{0} {1} | Range: {2:yyyy-MM-dd HH:mm:ss} ~ {3:yyyy-MM-dd HH:mm:ss} (Duration {4}){5} | Snaps: {6} (First: {7}, Last: {8})",
            statusDesc, cycle.PoolDisplayName, cycle.CycleStart.ToLocalTime(), rangeEnd.ToLocalTime(), FormatDuration(cycle.Duration), earlyNote, cycle.SnapshotCount, firstCapStr, lastCapStr);

        var nonCached = cycle.NonCachedInputTokens;
        var hitRate = cycle.CacheHitRate;
        _detailTokenLabel.Text = I18n.Format(
            "Token 明细: 未命中输入 {0} | 缓存读取 {1} (命中率 {2:F1}%) | 缓存写入 {3} | 输出 {4} | 总计 {5}",
            "Token Breakdown: Uncached Input {0} | Cache Read {1} (Hit Rate {2:F1}%) | Cache Write {3} | Output {4} | Total {5}",
            FormatTokens(nonCached), FormatTokens(cycle.CycleCachedTokens), hitRate, FormatTokens(cycle.CycleCacheCreationTokens), FormatTokens(cycle.CycleOutputTokens), FormatTokens(cycle.TotalTokens));

        var costStr = cycle.CycleCostUsd.HasValue ? $"${cycle.CycleCostUsd.Value:F2}" : "—";
        var fullStr = cycle.EstimatedWeeklyCostUsd.HasValue ? $"约 ${cycle.EstimatedWeeklyCostUsd.Value:F2}" : "—";
        var noteStr = !string.IsNullOrWhiteSpace(cycle.EstimateNote) ? $" ({cycle.EstimateNote})" : "";
        _detailCostLabel.Text = I18n.Format(
            "订阅参考金额: {0} | 周满额样本预估: {1}{2}",
            "Sub Ref Cost: {0} | Full Quota Projection: {1}{2}",
            costStr, fullStr, noteStr);

        if (cycle.ModelProjections is { Count: > 0 })
        {
            var parts = cycle.ModelProjections.Select(p =>
                $"{p.ModelId} 约 ${p.EstimatedWeeklyCostUsd:F2} ({(p.IsFromCurrentCycle ? I18n.Format("实测{0:0.#}%", "Actual {0:0.#}%", p.ConsumedFraction * 100) : I18n.T("历史基准", "Hist. benchmark"))})");
            _detailModelLabel.Text = I18n.Format("模型独立测算: {0}", "Model Projections: {0}", string.Join(" | ", parts));
        }
        else if (cycle.PoolCategory == "standard")
        {
            _detailModelLabel.Text = I18n.T("模型独立测算: 暂无足够独立模型样本", "Model Projections: Insufficient independent model samples");
        }
        else
        {
            _detailModelLabel.Text = I18n.T("模型独立测算: 单一模型池（与综合预估一致）", "Model Projections: Single-model pool (matches composite)");
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
            return hours > 0
                ? I18n.Format("{0}天{1}时", "{0}d {1}h", days, hours)
                : I18n.Format("{0}天整", "{0}d", days);
        }
        if (d.TotalHours >= 1.0)
        {
            var hours = (int)d.TotalHours;
            var mins = d.Minutes;
            return mins > 0
                ? I18n.Format("{0}时{1}分", "{0}h {1}m", hours, mins)
                : I18n.Format("{0}小时", "{0}h", hours);
        }
        return I18n.Format("{0}分钟", "{0}m", Math.Max(1, (int)d.TotalMinutes));
    }

    private static string FormatTokens(long count)
    {
        if (count >= 1_000_000_000) return $"{count / 1_000_000_000.0:F2}B";
        if (count >= 1_000_000) return $"{count / 1_000_000.0:F2}M";
        if (count >= 1_000) return $"{count / 1_000.0:F1}K";
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
        if (widths == null || widths.Count == 0) return;
        foreach (DataGridViewColumn col in _grid.Columns)
        {
            if (widths.TryGetValue(col.Name, out var w) && w >= 30)
            {
                col.Width = w;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            I18n.LanguageChanged -= RefreshLocalizedTexts;
        }
        base.Dispose(disposing);
    }
}
