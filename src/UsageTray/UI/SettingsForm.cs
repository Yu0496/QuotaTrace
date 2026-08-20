using UsageTray.App;
using UsageTray.Services;

namespace UsageTray.UI;

public sealed class SettingsForm : Form
{
    private readonly AppSettingsStore _settingsStore;
    private readonly RefreshCoordinator _coordinator;
    private readonly StartupManager _startupManager;
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
        Text = "UsageTray 设置";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;

        var textHeight = TextRenderer.MeasureText("刷新间隔（秒）", Font).Height;
        var controlHeight = Math.Max(28, textHeight + 8);
        var buttonHeight = Math.Max(30, textHeight + 10);
        var numericWidth = Math.Max(90, TextRenderer.MeasureText("0000", Font).Width + 40);

        _startup = new CheckBox
        {
            Text = "开机启动",
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
            Text = "常规配置",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 6)
        };

        var pricingTitle = new Label
        {
            Text = "模型定价",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 12, 0, 6)
        };

        var viewPricingButton = new Button
        {
            Text = "查看当前所有模型价格",
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
            Text = "从官方获取最新价格",
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
                    ? $"已从官方定价页更新 {result.UpdatedCount} 条价格规则（{result.FetchedAt.ToLocalTime():HH:mm:ss}）。"
                    : "价格已是最新，已保留本地规则。";
                if (result.Warnings.Count > 0) message += $"{Environment.NewLine}提示：{result.Warnings[0]}";
                MessageBox.Show(this, message, "UsageTray", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception exception)
            {
                if (IsDisposed || Disposing || !IsHandleCreated) return;
                MessageBox.Show(this, $"获取价格失败：{exception.Message}", "UsageTray", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            Text = "• 可查看 Antigravity 与 Codex 各模型的输入、缓存读写、输出单价及长上下文规则。\r\n• 点击“从官方获取最新价格”可自动从官方定价页同步最新单价。",
            AutoSize = true,
            ForeColor = Color.FromArgb(71, 85, 105),
            Margin = new Padding(0, 4, 0, 10)
        };

        var lastScanText = settings.LastFullScanUtc.HasValue
            ? settings.LastFullScanUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : "尚未执行（将自动调度）";

        var note = new Label
        {
            Text = $"• 普通刷新只处理新增或发生变化的文件；程序启动时默认增量扫描，每 7 天自动执行一次全量校准。\r\n• 上次全量扫描时间：{lastScanText}。\r\n• 如需重新解析全部历史记录，请点击下方的“全量重新读取”。",
            AutoSize = true,
            ForeColor = Color.FromArgb(71, 85, 105),
            Margin = new Padding(0, 8, 0, 6)
        };

        var saveButton = new Button
        {
            Text = "保存",
            DialogResult = DialogResult.OK,
            Width = Math.Max(80, TextRenderer.MeasureText("保存", Font).Width + 28),
            Height = buttonHeight
        };
        var cancelButton = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Width = Math.Max(80, TextRenderer.MeasureText("取消", Font).Width + 28),
            Height = buttonHeight
        };
        var fullScanButton = new Button
        {
            Text = "全量重新读取",
            Width = Math.Max(120, TextRenderer.MeasureText("全量重新读取", Font).Width + 24),
            Height = buttonHeight
        };

        saveButton.Click += (_, _) => SaveSettings();
        fullScanButton.Click += async (_, _) =>
        {
            if (!fullScanButton.Enabled) return;
            SaveSettings();
            fullScanButton.Enabled = false;
            try
            {
                await _coordinator.RefreshAsync(true);
                if (IsDisposed || Disposing || !IsHandleCreated) return;
                note.Text = $"• 普通刷新只处理新增或发生变化的文件；程序启动时默认增量扫描，每 7 天自动执行一次全量校准。\r\n• 上次全量扫描时间：{DateTime.Now:yyyy-MM-dd HH:mm}。\r\n• 如需重新解析全部历史记录，请点击下方的“全量重新读取”。";
                MessageBox.Show(this, "全量重新读取完成。之后的自动刷新会继续使用增量模式。", "UsageTray", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception exception)
            {
                if (IsDisposed || Disposing || !IsHandleCreated) return;
                MessageBox.Show(this, $"全量重新读取失败：{exception.Message}", "UsageTray", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                if (!IsDisposed && !Disposing && IsHandleCreated) fullScanButton.Enabled = true;
            }
        };

        // 底部固定操作栏
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

        // Numeric fields panel
        var numericTable = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 2,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8)
        };
        numericTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        numericTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        numericTable.Controls.Add(CreateLabel("刷新间隔（秒）："), 0, 0);
        numericTable.Controls.Add(_refresh, 1, 0);
        numericTable.Controls.Add(CreateLabel("数据保留天数："), 0, 1);
        numericTable.Controls.Add(_retention, 1, 1);

        // 可滚动的设置内容容器
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
            RowCount = 9,
            AutoSize = true,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = SystemColors.Control
        };
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // title
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // startup
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // numericTable
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // extraLabel
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 85)); // extraRoots
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // pricingTitle
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // pricingButtonsPanel
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // pricingNote
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // note

        mainPanel.Controls.Add(title, 0, 0);
        mainPanel.Controls.Add(_startup, 0, 1);
        mainPanel.Controls.Add(numericTable, 0, 2);
        mainPanel.Controls.Add(CreateLabel("额外 Codex sessions 目录（每行一个）："), 0, 3);
        mainPanel.Controls.Add(_extraRoots, 0, 4);
        mainPanel.Controls.Add(pricingTitle, 0, 5);
        mainPanel.Controls.Add(pricingButtonsPanel, 0, 6);
        mainPanel.Controls.Add(pricingNote, 0, 7);
        mainPanel.Controls.Add(note, 0, 8);

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

    private void SaveSettings()
    {
        var settings = _settingsStore.Load();
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
        _coordinator.UpdateSettings(settings);
    }
}


