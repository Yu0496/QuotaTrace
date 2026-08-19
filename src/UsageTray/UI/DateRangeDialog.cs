using UsageTray.Core;

namespace UsageTray.UI;

public sealed class DateRangeDialog : Form
{
    private readonly DateTimePicker _from;
    private readonly DateTimePicker _to;

    public DateRange? SelectedRange { get; private set; }

    public DateRangeDialog(DateRange initialRange)
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font("Segoe UI", 9F);
        Icon = AppIcon.Create();
        Text = "选择日期范围";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var textHeight = TextRenderer.MeasureText("开始日期", Font).Height;
        var controlHeight = Math.Max(34, textHeight + 12);
        var pickerWidth = Math.Max(145, TextRenderer.MeasureText("2026-08-19", Font).Width + 34);
        _from = CreatePicker(initialRange.From, pickerWidth, controlHeight);
        _to = CreatePicker(initialRange.To, pickerWidth, controlHeight);

        var title = new Label
        {
            Text = "选择要查看的本地日期范围",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8)
        };
        var fromLabel = new Label { Text = "开始日期", AutoSize = true, TextAlign = ContentAlignment.MiddleLeft };
        var toLabel = new Label { Text = "结束日期", AutoSize = true, TextAlign = ContentAlignment.MiddleLeft };
        var saveButton = new Button
        {
            Text = "应用",
            AutoSize = true,
            MinimumSize = new Size(76, controlHeight),
            Padding = new Padding(10, 2, 10, 2)
        };
        var cancelButton = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            MinimumSize = new Size(76, controlHeight),
            Padding = new Padding(10, 2, 10, 2)
        };
        saveButton.Click += (_, _) => SaveRange();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0, 12, 0, 0)
        };
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(saveButton);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 2,
            RowCount = 4,
            AutoSize = true
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, controlHeight + 8));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, controlHeight + 8));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(title, 0, 0);
        layout.SetColumnSpan(title, 2);
        layout.Controls.Add(fromLabel, 0, 1);
        layout.Controls.Add(_from, 1, 1);
        layout.Controls.Add(toLabel, 0, 2);
        layout.Controls.Add(_to, 1, 2);
        layout.Controls.Add(buttons, 0, 3);
        layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);

        AcceptButton = saveButton;
        CancelButton = cancelButton;
        ClientSize = new Size(Math.Max(390, pickerWidth + 150), Math.Max(250, controlHeight * 3 + textHeight + 90));
    }

    private static DateTimePicker CreatePicker(DateOnly date, int width, int height) => new()
    {
        Format = DateTimePickerFormat.Custom,
        CustomFormat = "yyyy-MM-dd",
        Width = width,
        Height = height,
        Value = date.ToDateTime(TimeOnly.MinValue),
        Margin = new Padding(0)
    };

    private void SaveRange()
    {
        var from = DateOnly.FromDateTime(_from.Value.Date);
        var to = DateOnly.FromDateTime(_to.Value.Date);
        if (to < from)
        {
            MessageBox.Show(this, "结束日期不能早于开始日期。", "日期范围", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SelectedRange = new DateRange(from, to);
        DialogResult = DialogResult.OK;
    }
}
