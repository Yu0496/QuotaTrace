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
    private readonly CheckBox _cacheValidation;

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
        AutoScroll = true;

        var textHeight = TextRenderer.MeasureText("刷新间隔（秒）", Font).Height;
        var controlHeight = Math.Max(34, textHeight + 12);
        var headingHeight = Math.Max(34, new Font(Font, FontStyle.Bold).Height + 10);
        var editorHeight = Math.Max(120, textHeight * 4 + 24);
        var noteHeight = Math.Max(textHeight * 3 + 18, 72);
        var buttonHeight = controlHeight;
        var numericWidth = Math.Max(96, TextRenderer.MeasureText("0000", Font).Width + 42);
        var labelColumnWidth = Math.Max(190, TextRenderer.MeasureText("刷新间隔（秒）", Font).Width + 22);

        _startup = new CheckBox
        {
            Text = "开机启动",
            Checked = settings.StartWithWindows,
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = controlHeight,
            Padding = new Padding(0, 2, 0, 2)
        };
        _refresh = new NumericUpDown
        {
            Minimum = 60,
            Maximum = 300,
            Increment = 30,
            Value = Math.Clamp(settings.RefreshSeconds, 60, 300),
            Width = numericWidth,
            Height = controlHeight,
            Dock = DockStyle.Left,
            TextAlign = HorizontalAlignment.Center
        };
        _retention = new NumericUpDown
        {
            Minimum = 7,
            Maximum = 3650,
            Value = Math.Clamp(settings.DataRetentionDays, 7, 3650),
            Width = numericWidth,
            Height = controlHeight,
            Dock = DockStyle.Left,
            TextAlign = HorizontalAlignment.Center
        };
        _extraRoots = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Height = editorHeight,
            Text = string.Join(Environment.NewLine, settings.ExtraCodexRoots),
            Margin = new Padding(0, 0, 0, 0)
        };
        _cacheValidation = new CheckBox
        {
            Text = "已用真实 status-line 样本验证 Antigravity cache 拆分",
            Checked = settings.StatusLineCacheSemanticsValidated,
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = controlHeight,
            Padding = new Padding(0, 2, 0, 2)
        };

        var title = new Label
        {
            Text = "常规",
            Dock = DockStyle.Fill,
            AutoSize = false,
            Font = new Font(Font, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        var extraLabel = CreateLabel("额外 Codex sessions 目录（每行一个）");
        var note = new Label
        {
            Text = "普通刷新只处理新增或发生变化的文件；程序启动时自动做一次全量读取。\r\n如需重新解析全部历史记录，请点击“全量重新读取”。\r\nAntigravity API 等值仅使用真实 token，quota 百分比不会参与美元计算。",
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = noteHeight,
            ForeColor = Color.DarkSlateBlue,
            Padding = new Padding(0, 4, 0, 4)
        };
        var saveButton = new Button
        {
            Text = "保存",
            DialogResult = DialogResult.OK,
            Width = Math.Max(76, TextRenderer.MeasureText("保存", Font).Width + 28),
            Height = buttonHeight,
            Padding = new Padding(10, 2, 10, 2)
        };
        var cancelButton = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Width = Math.Max(76, TextRenderer.MeasureText("取消", Font).Width + 28),
            Height = buttonHeight,
            Padding = new Padding(10, 2, 10, 2)
        };
        var fullScanButton = new Button
        {
            Text = "全量重新读取",
            Width = Math.Max(130, TextRenderer.MeasureText("全量重新读取", Font).Width + 28),
            Height = buttonHeight,
            Padding = new Padding(8, 2, 8, 2)
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
                MessageBox.Show(this, "全量重新读取完成。之后的自动刷新会继续使用增量模式。", "UsageTray", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, $"全量重新读取失败：{exception.Message}", "UsageTray", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { fullScanButton.Enabled = true; }
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 0)
        };
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(saveButton);
        buttons.Controls.Add(fullScanButton);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 9,
            Padding = new Padding(16),
            BackColor = SystemColors.Control
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelColumnWidth));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, headingHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, controlHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, controlHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, controlHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, textHeight + 10));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, editorHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, controlHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, noteHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, buttonHeight + 12));
        layout.Controls.Add(title, 0, 0);
        layout.SetColumnSpan(title, 2);
        layout.Controls.Add(_startup, 0, 1);
        layout.SetColumnSpan(_startup, 2);
        layout.Controls.Add(CreateLabel("刷新间隔（秒）"), 0, 2);
        layout.Controls.Add(_refresh, 1, 2);
        layout.Controls.Add(CreateLabel("数据保留天数"), 0, 3);
        layout.Controls.Add(_retention, 1, 3);
        layout.Controls.Add(extraLabel, 0, 4);
        layout.SetColumnSpan(extraLabel, 2);
        layout.Controls.Add(_extraRoots, 0, 5);
        layout.SetColumnSpan(_extraRoots, 2);
        layout.Controls.Add(_cacheValidation, 0, 6);
        layout.SetColumnSpan(_cacheValidation, 2);
        layout.Controls.Add(note, 0, 7);
        layout.SetColumnSpan(note, 2);
        layout.Controls.Add(buttons, 0, 8);
        layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);

        AcceptButton = saveButton;
        CancelButton = cancelButton;
        ClientSize = new Size(650, Math.Max(540, editorHeight + controlHeight * 5 + headingHeight + noteHeight + 112));
    }

    private Label CreateLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        AutoSize = false,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(0, 2, 0, 2)
    };

    private void SaveSettings()
    {
        var settings = _settingsStore.Load();
        settings.StartWithWindows = _startup.Checked;
        settings.RefreshSeconds = (int)_refresh.Value;
        settings.DataRetentionDays = (int)_retention.Value;
        settings.ExtraCodexRoots = _extraRoots.Lines.Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
        settings.StatusLineCacheSemanticsValidated = _cacheValidation.Checked;
        try { _startupManager.SetEnabled(settings.StartWithWindows); } catch { }
        _coordinator.UpdateSettings(settings);
    }
}
