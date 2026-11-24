using UsageTray.App;
using UsageTray.Core;
using UsageTray.Providers.Antigravity;
using UsageTray.Services;

namespace UsageTray.UI;

public sealed class AntigravityManualQuotaDialog : Form
{
    private readonly DateTimeOffset _now;
    private readonly PoolEditor _geminiEditor;
    private readonly PoolEditor _claudeEditor;
    private readonly Label _validationLabel;
    private readonly Button _saveButton;
    private readonly Button _cancelButton;

    public IReadOnlyList<QuotaSnapshot> ResultSnapshots { get; private set; } = [];

    public AntigravityManualQuotaDialog(IReadOnlyList<QuotaSnapshot>? existingQuotas = null, DateTimeOffset? now = null)
    {
        _now = now ?? DateTimeOffset.Now;

        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font("Segoe UI", 9F);
        Icon = AppIcon.Create();
        Text = I18n.T("手动校准 Antigravity 额度", "Calibrate Antigravity Quota");
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;

        var scale = DeviceDpi / 96f;
        var textH = TextRenderer.MeasureText("Ag测试", Font).Height;
        var btnH = Math.Max(30, textH + 12);

        var infoPanel = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(10),
            BackColor = Color.FromArgb(239, 246, 255),
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 0, 0, 10)
        };
        var infoLabel = new Label
        {
            Text = I18n.T(
                "Token 与订阅参考金额仍从本地对话数据库全自动统计。\r\n在此录入 Antigravity 设置中看到的剩余 % 与重置倒计时（或日期时间），即可切换本周周期并推算满额。",
                "Tokens & Sub Ref Values are still tracked automatically from local conversation DBs.\r\nEnter remaining % and reset countdown (or date/time) from Antigravity Settings to switch the weekly cycle and project full quota."),
            AutoSize = true,
            MaximumSize = new Size((int)(520 * scale), 0),
            ForeColor = Color.FromArgb(30, 64, 175),
            Dock = DockStyle.Fill
        };
        infoPanel.Controls.Add(infoLabel);

        var geminiSnap = FindWeeklySnapshot(existingQuotas, "gemini");
        var claudeSnap = FindWeeklySnapshot(existingQuotas, "3p");

        _geminiEditor = new PoolEditor(
            poolCategory: "gemini",
            title: I18n.T("Gemini Models 周额度", "Gemini Models Weekly Quota"),
            defaultChecked: true,
            existing: geminiSnap,
            now: _now,
            font: Font,
            syncButton: null);

        var syncBtn = new Button
        {
            Text = I18n.T("与 Gemini 重置时间一致", "Copy Gemini Reset Time"),
            AutoSize = true,
            Height = btnH,
            Padding = new Padding(8, 2, 8, 2),
            Margin = new Padding(8, 0, 0, 0)
        };

        _claudeEditor = new PoolEditor(
            poolCategory: "3p",
            title: I18n.T("Claude / GPT 周额度（可选）", "Claude / GPT Weekly Quota (Optional)"),
            defaultChecked: false,
            existing: claudeSnap,
            now: _now,
            font: Font,
            syncButton: syncBtn);

        syncBtn.Click += (_, _) => _claudeEditor.CopyResetFrom(_geminiEditor);

        _geminiEditor.Changed += (_, _) => UpdatePreviewAndValidation();
        _claudeEditor.Changed += (_, _) => UpdatePreviewAndValidation();

        _validationLabel = new Label
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(220, 38, 38),
            Margin = new Padding(0, 4, 0, 4),
            MaximumSize = new Size((int)(520 * scale), 0)
        };

        _saveButton = new Button
        {
            Text = I18n.T("保存并立即测算", "Save & Calculate"),
            AutoSize = true,
            Height = btnH,
            Padding = new Padding(12, 2, 12, 2)
        };
        _cancelButton = new Button
        {
            Text = I18n.T("取消", "Cancel"),
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            Height = btnH,
            Padding = new Padding(12, 2, 12, 2)
        };

        _saveButton.Click += (_, _) => HandleSave();

        var bottomPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = btnH + 22,
            Padding = new Padding(14, 10, 14, 12),
            BackColor = Color.FromArgb(248, 250, 252)
        };
        var buttonFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true
        };
        buttonFlow.Controls.Add(_cancelButton);
        buttonFlow.Controls.Add(_saveButton);
        bottomPanel.Controls.Add(buttonFlow);

        var contentScroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(14, 12, 14, 8),
            BackColor = Color.White
        };

        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            RowCount = 4,
            AutoSize = true
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.Controls.Add(infoPanel, 0, 0);
        stack.Controls.Add(_geminiEditor.Container, 0, 1);
        stack.Controls.Add(_claudeEditor.Container, 0, 2);
        stack.Controls.Add(_validationLabel, 0, 3);

        contentScroll.Controls.Add(stack);
        Controls.Add(contentScroll);
        Controls.Add(bottomPanel);

        AcceptButton = _saveButton;
        CancelButton = _cancelButton;

        var preferredWidth = Math.Max((int)(590 * scale), 590);
        var preferredHeight = Math.Max((int)(560 * scale), 560);
        var workArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        ClientSize = new Size(
            Math.Min(preferredWidth, workArea.Width - 60),
            Math.Min(preferredHeight, workArea.Height - 80));
        MinimumSize = new Size(Math.Min((int)(520 * scale), workArea.Width - 80), Math.Min((int)(440 * scale), workArea.Height - 100));

        UpdatePreviewAndValidation();
    }

    private static QuotaSnapshot? FindWeeklySnapshot(IReadOnlyList<QuotaSnapshot>? snapshots, string pool)
    {
        if (snapshots is null || snapshots.Count == 0) return null;
        return snapshots
            .Where(s => s.Provider == ProviderKind.Antigravity &&
                        AntigravityQuotaEstimator.SnapshotMatchesPool(s, pool) &&
                        string.Equals(s.WindowKind, "weekly", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.CapturedAt)
            .FirstOrDefault();
    }

    private void UpdatePreviewAndValidation()
    {
        var errors = new List<string>();
        var anyChecked = _geminiEditor.IsEnabled || _claudeEditor.IsEnabled;
        if (!anyChecked)
        {
            errors.Add(I18n.T("请至少勾选一个模型池进行校准。", "Please enable at least one model pool."));
        }

        if (_geminiEditor.IsEnabled && !_geminiEditor.TryValidate(_now, out var err1))
        {
            errors.Add(err1!);
        }
        if (_claudeEditor.IsEnabled && !_claudeEditor.TryValidate(_now, out var err2))
        {
            errors.Add(err2!);
        }

        _validationLabel.Text = string.Join(Environment.NewLine, errors);
        _validationLabel.Visible = errors.Count > 0;
        _saveButton.Enabled = errors.Count == 0;
    }

    private void HandleSave()
    {
        UpdatePreviewAndValidation();
        if (!_saveButton.Enabled) return;

        var list = new List<QuotaSnapshot>();
        try
        {
            var captureNow = DateTimeOffset.UtcNow;
            if (_geminiEditor.IsEnabled)
            {
                list.Add(_geminiEditor.BuildSnapshot(_now, captureNow));
            }
            if (_claudeEditor.IsEnabled)
            {
                list.Add(_claudeEditor.BuildSnapshot(_now, captureNow));
            }
        }
        catch (Exception ex)
        {
            _validationLabel.Text = ex.Message;
            _validationLabel.Visible = true;
            return;
        }

        ResultSnapshots = list;
        DialogResult = DialogResult.OK;
        Close();
    }

    private sealed class PoolEditor
    {
        private readonly string _poolCategory;
        private readonly CheckBox _enableCheck;
        private readonly NumericUpDown _remainingInput;
        private readonly RadioButton _countdownRadio;
        private readonly RadioButton _exactDateRadio;
        private readonly NumericUpDown _daysInput;
        private readonly NumericUpDown _hoursInput;
        private readonly NumericUpDown _minutesInput;
        private readonly DateTimePicker _resetPicker;
        private readonly Label _previewLabel;
        private readonly Panel _bodyPanel;
        private readonly Button? _syncButton;

        public GroupBox Container { get; }
        public bool IsEnabled => _enableCheck.Checked;
        public event EventHandler? Changed;

        public PoolEditor(
            string poolCategory,
            string title,
            bool defaultChecked,
            QuotaSnapshot? existing,
            DateTimeOffset now,
            Font font,
            Button? syncButton)
        {
            _poolCategory = poolCategory;
            _syncButton = syncButton;

            var numW = Math.Max(70, TextRenderer.MeasureText("100.0", font).Width + 32);
            var smallNumW = Math.Max(58, TextRenderer.MeasureText("24", font).Width + 30);

            Container = new GroupBox
            {
                Text = title,
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(10, 8, 10, 10),
                Margin = new Padding(0, 0, 0, 10)
            };

            _enableCheck = new CheckBox
            {
                Text = I18n.T("校准该模型池周额度", "Calibrate this pool's weekly quota"),
                Checked = defaultChecked,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 6)
            };

            _remainingInput = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 100,
                DecimalPlaces = 1,
                Increment = 1,
                Value = existing?.RemainingFraction.HasValue == true && !existing.IsResetPassed(now)
                    ? (decimal)Math.Clamp(Math.Round(existing.RemainingFraction.Value * 100.0, 1), 0.0, 100.0)
                    : 100.0m,
                Width = numW,
                TextAlign = HorizontalAlignment.Center
            };

            var hasActiveReset = existing?.ResetAt.HasValue == true && !existing.IsResetPassed(now);
            var defaultReset = hasActiveReset ? existing!.ResetAt!.Value.ToLocalTime() : now.AddDays(7);
            var (defDays, defHours, defMinutes) = hasActiveReset
                ? AntigravityManualQuotaHelper.SplitCountdown(now, existing!.ResetAt!.Value)
                : (7, 0, 0);

            _countdownRadio = new RadioButton
            {
                Text = I18n.T("按剩余倒计时填写", "By remaining countdown"),
                AutoSize = true,
                Checked = !(hasActiveReset && existing!.IsManualCalibration)
            };
            _exactDateRadio = new RadioButton
            {
                Text = I18n.T("手动指定重置时间", "Specify reset date/time"),
                AutoSize = true,
                Checked = hasActiveReset && existing!.IsManualCalibration
            };

            _daysInput = new NumericUpDown { Minimum = 0, Maximum = 7, Value = Math.Clamp(defDays, 0, 7), Width = smallNumW, TextAlign = HorizontalAlignment.Center };
            _hoursInput = new NumericUpDown { Minimum = 0, Maximum = 23, Value = Math.Clamp(defHours, 0, 23), Width = smallNumW, TextAlign = HorizontalAlignment.Center };
            _minutesInput = new NumericUpDown { Minimum = 0, Maximum = 59, Value = Math.Clamp(defMinutes, 0, 59), Width = smallNumW, TextAlign = HorizontalAlignment.Center };

            _resetPicker = new DateTimePicker
            {
                Format = DateTimePickerFormat.Custom,
                CustomFormat = "yyyy-MM-dd HH:mm",
                Value = defaultReset.LocalDateTime,
                Width = Math.Max(180, TextRenderer.MeasureText("2026-12-31 23:59", font).Width + 40)
            };

            _previewLabel = new Label
            {
                AutoSize = true,
                ForeColor = Color.FromArgb(71, 85, 105),
                Margin = new Padding(0, 6, 0, 0)
            };

            _bodyPanel = new Panel { Dock = DockStyle.Top, AutoSize = true };
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 1,
                RowCount = 5,
                AutoSize = true
            };

            var rowRem = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
            rowRem.Controls.Add(new Label { Text = I18n.T("当前剩余额度：", "Remaining Quota: "), AutoSize = true, Margin = new Padding(0, 5, 4, 0) });
            rowRem.Controls.Add(_remainingInput);
            rowRem.Controls.Add(new Label { Text = "%", AutoSize = true, Margin = new Padding(2, 5, 10, 0) });
            if (syncButton != null) rowRem.Controls.Add(syncButton);

            var rowMode = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Margin = new Padding(0, 4, 0, 2) };
            rowMode.Controls.Add(_countdownRadio);
            rowMode.Controls.Add(_exactDateRadio);

            var rowCountdown = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
            rowCountdown.Controls.Add(new Label { Text = I18n.T("剩余时间：", "Countdown: "), AutoSize = true, Margin = new Padding(0, 5, 4, 0) });
            rowCountdown.Controls.Add(_daysInput);
            rowCountdown.Controls.Add(new Label { Text = I18n.T("天", "d"), AutoSize = true, Margin = new Padding(2, 5, 6, 0) });
            rowCountdown.Controls.Add(_hoursInput);
            rowCountdown.Controls.Add(new Label { Text = I18n.T("小时", "h"), AutoSize = true, Margin = new Padding(2, 5, 6, 0) });
            rowCountdown.Controls.Add(_minutesInput);
            rowCountdown.Controls.Add(new Label { Text = I18n.T("分后重置", "m until reset"), AutoSize = true, Margin = new Padding(2, 5, 0, 0) });

            var rowExact = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Margin = new Padding(0, 2, 0, 0) };
            rowExact.Controls.Add(new Label { Text = I18n.T("重置日期时间：", "Reset Date/Time: "), AutoSize = true, Margin = new Padding(0, 5, 4, 0) });
            rowExact.Controls.Add(_resetPicker);

            grid.Controls.Add(rowRem, 0, 0);
            grid.Controls.Add(rowMode, 0, 1);
            grid.Controls.Add(rowCountdown, 0, 2);
            grid.Controls.Add(rowExact, 0, 3);
            grid.Controls.Add(_previewLabel, 0, 4);
            _bodyPanel.Controls.Add(grid);

            var outer = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, RowCount = 2, AutoSize = true };
            outer.Controls.Add(_enableCheck, 0, 0);
            outer.Controls.Add(_bodyPanel, 0, 1);
            Container.Controls.Add(outer);

            void RefreshModeAndPreview()
            {
                _bodyPanel.Enabled = _enableCheck.Checked;
                var useCountdown = _countdownRadio.Checked;
                _daysInput.Enabled = useCountdown;
                _hoursInput.Enabled = useCountdown;
                _minutesInput.Enabled = useCountdown;
                _resetPicker.Enabled = !useCountdown;
                if (_syncButton != null) _syncButton.Enabled = _enableCheck.Checked;

                if (TryGetResetAt(now, out var rAt, out var err))
                {
                    var start = rAt.AddDays(-7).ToLocalTime();
                    var end = rAt.ToLocalTime();
                    var rel = TimeFormatter.FormatRelativeFuture(rAt, now);
                    _previewLabel.ForeColor = Color.FromArgb(5, 150, 105);
                    _previewLabel.Text = I18n.Format("推算周期：{0:MM-dd HH:mm} ~ {1:MM-dd HH:mm}（{2}）", "Cycle: {0:MM-dd HH:mm} ~ {1:MM-dd HH:mm} ({2})", start, end, rel);
                }
                else
                {
                    _previewLabel.ForeColor = Color.FromArgb(220, 38, 38);
                    _previewLabel.Text = err ?? string.Empty;
                }
                Changed?.Invoke(this, EventArgs.Empty);
            }

            _enableCheck.CheckedChanged += (_, _) => RefreshModeAndPreview();
            _remainingInput.ValueChanged += (_, _) => RefreshModeAndPreview();
            _countdownRadio.CheckedChanged += (_, _) => RefreshModeAndPreview();
            _exactDateRadio.CheckedChanged += (_, _) => RefreshModeAndPreview();
            _daysInput.ValueChanged += (_, _) => RefreshModeAndPreview();
            _hoursInput.ValueChanged += (_, _) => RefreshModeAndPreview();
            _minutesInput.ValueChanged += (_, _) => RefreshModeAndPreview();
            _resetPicker.ValueChanged += (_, _) => RefreshModeAndPreview();

            RefreshModeAndPreview();
        }

        public void CopyResetFrom(PoolEditor source)
        {
            _countdownRadio.Checked = source._countdownRadio.Checked;
            _exactDateRadio.Checked = source._exactDateRadio.Checked;
            _daysInput.Value = source._daysInput.Value;
            _hoursInput.Value = source._hoursInput.Value;
            _minutesInput.Value = source._minutesInput.Value;
            _resetPicker.Value = source._resetPicker.Value;
        }

        public bool TryValidate(DateTimeOffset now, out string? error) => TryGetResetAt(now, out _, out error);

        private bool TryGetResetAt(DateTimeOffset now, out DateTimeOffset resetAt, out string? error)
        {
            if (_countdownRadio.Checked)
            {
                var span = new TimeSpan((int)_daysInput.Value, (int)_hoursInput.Value, (int)_minutesInput.Value, 0);
                if (span <= TimeSpan.Zero || span > TimeSpan.FromDays(7))
                {
                    resetAt = default;
                    error = I18n.T("剩余倒计时必须大于 0 分钟且不超过 7 天。", "Countdown must be > 0m and <= 7d.");
                    return false;
                }
                resetAt = now.Add(span);
                error = null;
                return true;
            }
            else
            {
                var local = DateTime.SpecifyKind(_resetPicker.Value, DateTimeKind.Local);
                resetAt = new DateTimeOffset(local);
                if (resetAt <= now)
                {
                    error = I18n.T("指定的重置时间必须晚于当前时间。", "Specified reset time must be in the future.");
                    return false;
                }
                if (resetAt - now > TimeSpan.FromDays(7) + TimeSpan.FromMinutes(5))
                {
                    error = I18n.T("周额度重置时间不能超过未来 7 天。", "Weekly reset time cannot exceed 7 days from now.");
                    return false;
                }
                error = null;
                return true;
            }
        }

        public QuotaSnapshot BuildSnapshot(DateTimeOffset uiNow, DateTimeOffset captureNow)
        {
            var resetAt = _countdownRadio.Checked
                ? AntigravityManualQuotaHelper.CalculateResetAtFromCountdown(uiNow, (int)_daysInput.Value, (int)_hoursInput.Value, (int)_minutesInput.Value)
                : new DateTimeOffset(DateTime.SpecifyKind(_resetPicker.Value, DateTimeKind.Local));

            return AntigravityManualQuotaHelper.CreateManualWeeklySnapshot(
                _poolCategory,
                (double)_remainingInput.Value,
                resetAt,
                captureNow);
        }
    }
}
