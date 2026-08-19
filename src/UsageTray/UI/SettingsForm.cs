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

        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font("Segoe UI", 9F);
        Icon = AppIcon.Create();
        Text = "UsageTray 设置";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
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

        var note = new Label
        {
            Text = "• 普通刷新只处理新增或发生变化的文件；程序启动时自动执行一次全量读取。\r\n• 如需重新解析全部历史记录，请点击“全量重新读取”。",
            AutoSize = true,
            ForeColor = Color.FromArgb(71, 85, 105),
            Margin = new Padding(0, 8, 0, 12)
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
        var pricingButton = new Button
        {
            Text = "获取最新价格",
            Width = Math.Max(120, TextRenderer.MeasureText("获取最新价格", Font).Width + 24),
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

        // Actions row (Tool buttons on left, Dialog buttons on right)
        var actionPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            Margin = new Padding(0, 6, 0, 0)
        };
        actionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        actionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));

        var leftActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            Margin = Padding.Empty
        };
        leftActions.Controls.Add(pricingButton);
        leftActions.Controls.Add(fullScanButton);

        var rightActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            Margin = Padding.Empty
        };
        rightActions.Controls.Add(cancelButton);
        rightActions.Controls.Add(saveButton);

        actionPanel.Controls.Add(leftActions, 0, 0);
        actionPanel.Controls.Add(rightActions, 1, 0);

        // Numeric fields panel
        var numericTable = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 2,
            AutoSize = true,
            Margin = Padding.Empty
        };
        numericTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        numericTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        numericTable.Controls.Add(CreateLabel("刷新间隔（秒）："), 0, 0);
        numericTable.Controls.Add(_refresh, 1, 0);
        numericTable.Controls.Add(CreateLabel("数据保留天数："), 0, 1);
        numericTable.Controls.Add(_retention, 1, 1);

        var mainPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 7,
            Padding = new Padding(16),
            BackColor = SystemColors.Control
        };
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // title
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // startup
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // numericTable
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // extraLabel
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 85)); // extraRoots
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // note
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // actionPanel

        mainPanel.Controls.Add(title, 0, 0);
        mainPanel.Controls.Add(_startup, 0, 1);
        mainPanel.Controls.Add(numericTable, 0, 2);
        mainPanel.Controls.Add(CreateLabel("额外 Codex sessions 目录（每行一个）："), 0, 3);
        mainPanel.Controls.Add(_extraRoots, 0, 4);
        mainPanel.Controls.Add(note, 0, 5);
        mainPanel.Controls.Add(actionPanel, 0, 6);

        Controls.Add(mainPanel);

        AcceptButton = saveButton;
        CancelButton = cancelButton;
        ClientSize = new Size(540, 450);
        MinimumSize = new Size(500, 420);
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
        try { _startupManager.SetEnabled(settings.StartWithWindows); } catch { }
        _coordinator.UpdateSettings(settings);
    }
}

