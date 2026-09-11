using UsageTray.App;
using UsageTray.Services;

namespace UsageTray.UI;

public sealed class SettingsForm : Form
{
    private readonly AppSettingsStore _settingsStore;
    private readonly RefreshCoordinator _coordinator;
    private readonly StartupManager _startupManager;
    private readonly ComboBox _languageCombo;
    private readonly CheckBox _enableCodex;
    private readonly CheckBox _enableAntigravity;
    private readonly CheckBox _startup;
    private readonly NumericUpDown _refresh;
    private readonly NumericUpDown _retention;
    private readonly TextBox _extraRoots;

    public SettingsForm(AppSettingsStore settingsStore, RefreshCoordinator coordinator)
    {
        _settingsStore = settingsStore;
        _coordinator = coordinator;
        _startupManager = new StartupManager();
        var settings = settingsStore.Load();

        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 9F);
        Icon = AppIcon.Create();
        Text = I18n.T("QuotaTrace 设置", "QuotaTrace Settings");
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;

        var textHeight = TextRenderer.MeasureText(I18n.T("刷新间隔（秒）", "Refresh Interval (Seconds)"), Font).Height;
        var controlHeight = Math.Max(28, textHeight + 8);
        var buttonHeight = Math.Max(30, textHeight + 10);
        var numericWidth = Math.Max(90, TextRenderer.MeasureText("0000", Font).Width + 40);

        _languageCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = Math.Max(220, (int)(220 * (DeviceDpi / 96f))),
            Height = controlHeight,
            Margin = new Padding(0, 2, 0, 8)
        };
        _languageCombo.Items.AddRange([
            I18n.T("自动 (跟随系统) / Auto", "Auto (System) / 自动"),
            "简体中文 (Simplified Chinese)",
            "English"
        ]);
        _languageCombo.SelectedIndex = settings.Language?.ToLowerInvariant() switch
        {
            "zh-cn" or "zh" => 1,
            "en-us" or "en" => 2,
            _ => 0
        };

        _enableCodex = new CheckBox
        {
            Text = I18n.T("启用 OpenAI Codex 监测", "Enable OpenAI Codex Monitoring"),
            Checked = settings.EnableCodex,
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 4)
        };

        _enableAntigravity = new CheckBox
        {
            Text = I18n.T("启用 Google Antigravity 监测", "Enable Google Antigravity Monitoring"),
            Checked = settings.EnableAntigravity,
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 8)
        };

        _startup = new CheckBox
        {
            Text = I18n.T("开机启动", "Start with Windows"),
            Checked = settings.StartWithWindows,
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 8)
        };
        _refresh = new NumericUpDown
        {
            Minimum = 60,
            Maximum = 300,
            Increment = 30,
            Value = Math.Clamp(settings.RefreshSeconds, 60, 300),
            Width = numericWidth,
            Height = controlHeight,
            TextAlign = HorizontalAlignment.Center
        };
        _retention = new NumericUpDown
        {
            Minimum = 7,
            Maximum = 3650,
            Value = Math.Clamp(settings.DataRetentionDays, 7, 3650),
            Width = numericWidth,
            Height = controlHeight,
            TextAlign = HorizontalAlignment.Center
        };
        _extraRoots = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Height = Math.Max(70, textHeight * 3 + 16),
            Text = string.Join(Environment.NewLine, settings.ExtraCodexRoots),
            Margin = new Padding(0, 4, 0, 4)
        };

        var title = new Label
        {
            Text = I18n.T("常规配置", "General Settings"),
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 6)
        };

        var pricingTitle = new Label
        {
            Text = I18n.T("模型定价", "Model Pricing"),
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 12, 0, 6)
        };

        var viewPricingButton = new Button
        {
            Text = I18n.T("查看当前所有模型价格", "View Current Model Prices"),
            AutoSize = true,
            Height = buttonHeight,
            Padding = new Padding(10, 2, 10, 2),
            Margin = new Padding(0, 0, 8, 0)
        };
        viewPricingButton.Click += (_, _) =>
        {
            using var viewer = new PricingViewerForm(_coordinator);
            viewer.ShowDialog(this);
        };

        var pricingButton = new Button
        {
            Text = I18n.T("更新内置参考基准", "Update Built-in Benchmarks"),
            AutoSize = true,
            Height = buttonHeight,
            Padding = new Padding(10, 2, 10, 2),
            Margin = Padding.Empty
        };

        pricingButton.Click += async (_, _) =>
        {
            if (!pricingButton.Enabled) return;
            pricingButton.Enabled = false;
            try
            {
                var result = await _coordinator.UpdatePricingAsync();
                if (IsDisposed || Disposing || !IsHandleCreated) return;
                var message = result.UpdatedCount > 0
                    ? I18n.Format(
                        "已更新内置参考基准，共 {0} 条价格规则（{1:HH:mm:ss}）。",
                        "Updated built-in benchmarks, {0} rules total ({1:HH:mm:ss}).",
                        result.UpdatedCount, result.FetchedAt.ToLocalTime())
                    : I18n.T("已使用当前软件内置基准，自定义规则保留。", "Already using current built-in benchmarks, custom rules preserved.");
                if (result.Warnings.Count > 0) message += $"{Environment.NewLine}{I18n.T("提示：", "Note: ")}{result.Warnings[0]}";
                MessageBox.Show(this, message, "QuotaTrace", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception exception)
            {
                if (IsDisposed || Disposing || !IsHandleCreated) return;
                MessageBox.Show(this, I18n.Format("获取价格失败：{0}", "Failed to update pricing: {0}", exception.Message), "QuotaTrace", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                if (!IsDisposed && !Disposing && IsHandleCreated) pricingButton.Enabled = true;
            }
        };

        var pricingButtonsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0, 2, 0, 4)
        };
        pricingButtonsPanel.Controls.Add(viewPricingButton);
        pricingButtonsPanel.Controls.Add(pricingButton);

        var pricingNote = new Label
        {
            Text = I18n.T(
                "• 可查看 Antigravity 与 Codex 各模型的输入、缓存读写、输出单价及长上下文规则。\r\n• 点击“更新内置参考基准”可合并软件附带的已核对基准；不会用 API 促销价覆盖订阅参考口径。",
                "• View input, cache read/write, output rates and long-context rules for Antigravity and Codex.\r\n• Click 'Update Built-in Benchmarks' to merge verified benchmarks; API promotional prices will not overwrite subscription benchmarks."),
            AutoSize = true,
            ForeColor = Color.FromArgb(71, 85, 105),
            Margin = new Padding(0, 4, 0, 10)
        };

        var reviewNoteTitle = new Label
        {
            Text = I18n.T("Reserve / Auto-Review 计量说明", "Reserve / Auto-Review Pricing Notes"),
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 10, 0, 4)
        };

        var reviewNote = new Label
        {
            Text = I18n.T(
                "• Reserve 与 Auto-Review 按约定使用 GPT-5.6 Luna 参考价：输入 $0.2 / 缓存读取 $0.02 / 缓存创建 $0.25 / 输出 $1.2（每百万 Token）；长上下文及 Fast 档位同 Luna。\r\n• 这是本软件的参考计量约定，底层模型身份及官方实际扣减仍存在不确定性。Spark 维持未定价。\r\n• 本软件统计订阅消耗参考价值：Token × 非促销基准价；Codex Fast 采用订阅倍率。金额并非实际扣费或订阅余额。\r\n• 额度来自官方快照；满额参考金额只能基于同周期本机用量样本外推，其他设备、云端任务及缺失日志都会影响结果。",
                "• Reserve and Auto-Review use GPT-5.6 Luna benchmark: Input $0.2 / CacheRead $0.02 / CacheCreation $0.25 / Output $1.2 per 1M tokens.\r\n• This is a local benchmark convention; underlying models and official consumption carry uncertainty. Spark remains unpriced.\r\n• Subscription reference values reflect estimated equivalent worth, not actual bills or account balances.\r\n• Quotas reflect official snapshots; full projections are estimated solely from local samples."),
            AutoSize = true,
            ForeColor = Color.FromArgb(71, 85, 105),
            Margin = new Padding(0, 4, 0, 10)
        };

        var speedNoteTitle = new Label
        {
            Text = I18n.T("速率反推原理说明", "Speed Estimation Methodology"),
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 10, 0, 4)
        };

        var speedNote = new Label
        {
            Text = I18n.T(
                "• 统计机制：基于本地会话记录中每个 Turn 的时间戳差分与 Token 增量反推。\r\n• 未命中 Prefill 速度：针对未命中输入，计算冷启动 Prompt 矩阵计算吞吐。\r\n• 命中 Prefill 速度：针对命中缓存的输入，计算 KV Cache 检索与加载吞吐。\r\n• 输出速度：计算模型自回归逐字生成（含思考过程与正文）的平均速率。\r\n• 局限性说明：该指标为客户端视角端到端估算，包含了网络 RTT、TLS 握手、云端排队调度及客户端写入缓冲等非模型计算耗时，因此数值不一定绝对准确且通常低于服务端的纯硬件物理速度，仅供性能参考与趋势对比。",
                "• Method: Derived from turn-by-turn timestamp differences and token deltas in local session records.\r\n• Uncached Prefill Speed: Cold prompt matrix throughput.\r\n• Cached Prefill Speed: KV Cache retrieval throughput.\r\n• Output Speed: Autoregressive decoding rate (including reasoning and text).\r\n• Limitations: End-to-end client-side estimate that includes network RTT, TLS, and buffering; provided for trend and performance comparisons."),
            AutoSize = true,
            ForeColor = Color.FromArgb(71, 85, 105),
            Margin = new Padding(0, 4, 0, 10)
        };

        var lastScanText = settings.LastFullScanUtc.HasValue
            ? settings.LastFullScanUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : I18n.T("尚未执行（将自动调度）", "Not yet executed (will auto-schedule)");

        var note = new Label
        {
            Text = I18n.Format(
                "• 普通刷新只处理新增或发生变化的文件；程序启动时默认增量扫描，每 7 天自动执行一次全量校准。\r\n• 上次全量扫描时间：{0}。\r\n• 如需重新解析全部历史记录，请点击下方的“全量重新读取”。",
                "• Normal refresh only inspects modified files; full calibration runs automatically every 7 days.\r\n• Last full scan: {0}.\r\n• Click 'Perform Full Scan' below to re-parse all local records.",
                lastScanText),
            AutoSize = true,
            ForeColor = Color.FromArgb(71, 85, 105),
            Margin = new Padding(0, 8, 0, 6)
        };

        var saveButton = new Button
        {
            Text = I18n.T("保存", "Save"),
            Width = Math.Max(80, TextRenderer.MeasureText(I18n.T("保存", "Save"), Font).Width + 28),
            Height = buttonHeight
        };
        var cancelButton = new Button
        {
            Text = I18n.T("取消", "Cancel"),
            DialogResult = DialogResult.Cancel,
            Width = Math.Max(80, TextRenderer.MeasureText(I18n.T("取消", "Cancel"), Font).Width + 28),
            Height = buttonHeight
        };
        var fullScanButton = new Button
        {
            Text = I18n.T("全量重新读取", "Perform Full Scan"),
            Width = Math.Max(120, TextRenderer.MeasureText(I18n.T("全量重新读取", "Perform Full Scan"), Font).Width + 24),
            Height = buttonHeight
        };

        saveButton.Click += (_, _) =>
        {
            if (SaveSettings())
            {
                DialogResult = DialogResult.OK;
                Close();
            }
        };

        fullScanButton.Click += async (_, _) =>
        {
            if (!fullScanButton.Enabled) return;
            if (!SaveSettings()) return;
            fullScanButton.Enabled = false;
            try
            {
                await _coordinator.RefreshAsync(true);
                if (IsDisposed || Disposing || !IsHandleCreated) return;
                var curScan = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                note.Text = I18n.Format(
                    "• 普通刷新只处理新增或发生变化的文件；程序启动时默认增量扫描，每 7 天自动执行一次全量校准。\r\n• 上次全量扫描时间：{0}。\r\n• 如需重新解析全部历史记录，请点击下方的“全量重新读取”。",
                    "• Normal refresh only inspects modified files; full calibration runs automatically every 7 days.\r\n• Last full scan: {0}.\r\n• Click 'Perform Full Scan' below to re-parse all local records.",
                    curScan);
                MessageBox.Show(this, I18n.T("全量重新读取完成。之后的自动刷新会继续使用增量模式。", "Full scan completed. Automatic refreshes will continue in incremental mode."), "QuotaTrace", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception exception)
            {
                if (IsDisposed || Disposing || !IsHandleCreated) return;
                MessageBox.Show(this, I18n.Format("全量重新读取失败：{0}", "Full scan failed: {0}", exception.Message), "QuotaTrace", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                if (!IsDisposed && !Disposing && IsHandleCreated) fullScanButton.Enabled = true;
            }
        };

        var bottomPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = buttonHeight + 24,
            Padding = new Padding(16, 10, 16, 14),
            BackColor = Color.FromArgb(248, 250, 252)
        };

        var leftActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Margin = Padding.Empty
        };
        leftActions.Controls.Add(fullScanButton);

        var rightActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            Margin = Padding.Empty
        };
        rightActions.Controls.Add(cancelButton);
        rightActions.Controls.Add(saveButton);

        bottomPanel.Controls.Add(leftActions);
        bottomPanel.Controls.Add(rightActions);

        // General settings table
        var numericTable = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 3,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8)
        };
        numericTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        numericTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        numericTable.Controls.Add(CreateLabel(I18n.T("语言 / Language：", "Language / 语言：")), 0, 0);
        numericTable.Controls.Add(_languageCombo, 1, 0);
        numericTable.Controls.Add(CreateLabel(I18n.T("刷新间隔（秒）：", "Refresh Interval (Sec):")), 0, 1);
        numericTable.Controls.Add(_refresh, 1, 1);
        numericTable.Controls.Add(CreateLabel(I18n.T("数据保留天数：", "Retention (Days):")), 0, 2);
        numericTable.Controls.Add(_retention, 1, 2);

        var contentPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(16, 16, 16, 8),
            BackColor = SystemColors.Control
        };

        var mainPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            RowCount = 16,
            AutoSize = true,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = SystemColors.Control
        };
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 0: title
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 1: provider label
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 2: enableCodex
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 3: enableAntigravity
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 4: startup
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 5: numericTable
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 6: extraLabel
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 85)); // 7: extraRoots
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 8: pricingTitle
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 9: pricingButtonsPanel
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 10: pricingNote
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 11: reviewNoteTitle
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 12: reviewNote
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 13: speedNoteTitle
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 14: speedNote
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // 15: note

        mainPanel.Controls.Add(title, 0, 0);
        mainPanel.Controls.Add(CreateLabel(I18n.T("监测提供商 (至少开启一项)：", "Monitored Providers (Enable at least one):")), 0, 1);
        mainPanel.Controls.Add(_enableCodex, 0, 2);
        mainPanel.Controls.Add(_enableAntigravity, 0, 3);
        mainPanel.Controls.Add(_startup, 0, 4);
        mainPanel.Controls.Add(numericTable, 0, 5);
        mainPanel.Controls.Add(CreateLabel(I18n.T("额外 Codex sessions 目录（每行一个）：", "Extra Codex sessions directories (one per line):")), 0, 6);
        mainPanel.Controls.Add(_extraRoots, 0, 7);
        mainPanel.Controls.Add(pricingTitle, 0, 8);
        mainPanel.Controls.Add(pricingButtonsPanel, 0, 9);
        mainPanel.Controls.Add(pricingNote, 0, 10);
        mainPanel.Controls.Add(reviewNoteTitle, 0, 11);
        mainPanel.Controls.Add(reviewNote, 0, 12);
        mainPanel.Controls.Add(speedNoteTitle, 0, 13);
        mainPanel.Controls.Add(speedNote, 0, 14);
        mainPanel.Controls.Add(note, 0, 15);

        contentPanel.Controls.Add(mainPanel);

        Controls.Add(contentPanel);
        Controls.Add(bottomPanel);

        AcceptButton = saveButton;
        CancelButton = cancelButton;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var settings = _settingsStore.Load();
        var width = settings.SettingsWindowWidth ?? 680;
        var height = settings.SettingsWindowHeight ?? 560;
        ClientSize = new Size(Math.Max(560, width), Math.Max(460, height));
        MinimumSize = new Size(560, 460);
        CenterToParent();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (ClientSize.Width < 560 || ClientSize.Height < 460)
        {
            ClientSize = new Size(680, 560);
            CenterToParent();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (WindowState == FormWindowState.Normal)
        {
            var settings = _settingsStore.Load();
            settings.SettingsWindowWidth = ClientSize.Width;
            settings.SettingsWindowHeight = ClientSize.Height;
            _settingsStore.Save(settings);
        }
    }

    private static Label CreateLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = new Padding(0, 4, 8, 4)
    };

    private bool SaveSettings()
    {
        if (!_enableCodex.Checked && !_enableAntigravity.Checked)
        {
            MessageBox.Show(this,
                I18n.T("必须至少勾选一个监控提供商（Codex 或 Antigravity）。", "At least one provider must be enabled (Codex or Antigravity)."),
                "QuotaTrace", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        var settings = _settingsStore.Load();
        settings.EnableCodex = _enableCodex.Checked;
        settings.EnableAntigravity = _enableAntigravity.Checked;
        settings.Language = _languageCombo.SelectedIndex switch
        {
            1 => "zh-CN",
            2 => "en-US",
            _ => "auto"
        };
        settings.StartWithWindows = _startup.Checked;
        settings.RefreshSeconds = (int)_refresh.Value;
        settings.DataRetentionDays = (int)_retention.Value;
        settings.ExtraCodexRoots = _extraRoots.Lines.Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
        if (WindowState == FormWindowState.Normal)
        {
            settings.SettingsWindowWidth = ClientSize.Width;
            settings.SettingsWindowHeight = ClientSize.Height;
        }
        try { _startupManager.SetEnabled(settings.StartWithWindows); } catch { }
        I18n.SetLanguage(settings.Language);
        _coordinator.UpdateSettings(settings);
        return true;
    }
}
