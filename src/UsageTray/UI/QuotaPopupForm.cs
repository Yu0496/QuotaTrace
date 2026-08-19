using UsageTray.Services;

namespace UsageTray.UI;

internal sealed class QuotaPopupForm : Form
{
    private readonly Label _body;
    private readonly Label _footer;

    public event EventHandler? DismissRequested;

    public QuotaPopupForm()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font("Segoe UI", 9F);
        Icon = AppIcon.Create();
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        ShowIcon = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.White;
        Padding = new Padding(1);

        var title = new Label
        {
            Text = "额度摘要",
            Dock = DockStyle.Fill,
            AutoSize = false,
            Font = new Font(Font, FontStyle.Bold),
            Padding = new Padding(12, 8, 12, 2),
            ForeColor = Color.FromArgb(35, 65, 100)
        };
        _body = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Padding = new Padding(12, 4, 12, 4),
            ForeColor = Color.FromArgb(35, 35, 35)
        };
        _footer = new Label
        {
            Text = "额度仅用于展示，不参与 API 等值计算。点击此面板可关闭。",
            Dock = DockStyle.Fill,
            AutoSize = false,
            Padding = new Padding(12, 2, 12, 8),
            ForeColor = Color.DimGray
        };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.White };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Math.Max(34, Font.Height + 18)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Math.Max(30, Font.Height + 16)));
        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(_body, 0, 1);
        layout.Controls.Add(_footer, 0, 2);
        Controls.Add(layout);
        AttachDismissHandlers(this);

        SetSnapshot(null);
    }

    public void SetSnapshot(DashboardSnapshot? snapshot)
    {
        var text = snapshot is null ? "暂无额度快照，请先刷新。" : QuotaDisplayFormatter.BuildPopupText(snapshot);
        _body.Text = text;
        var measure = TextRenderer.MeasureText(text, Font, new Size(580, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        var width = Math.Max(420, Math.Min(620, measure.Width + 32));
        var bodyHeight = Math.Max(Font.Height * 4, measure.Height + 12);
        ClientSize = new Size(width, bodyHeight + Math.Max(34, Font.Height + 18) + Math.Max(30, Font.Height + 16));
    }

    public void ShowAt(Point cursor)
    {
        var screen = Screen.FromPoint(cursor).WorkingArea;
        var x = Math.Clamp(cursor.X - Width + 12, screen.Left + 4, screen.Right - Width - 4);
        var y = Math.Clamp(cursor.Y - Height - 12, screen.Top + 4, screen.Bottom - Height - 4);
        Location = new Point(x, y);
        if (!Visible) Show();
        else BringToFront();
        TopMost = true;
    }

    private void AttachDismissHandlers(Control control)
    {
        control.Click += (_, _) => DismissRequested?.Invoke(this, EventArgs.Empty);
        foreach (Control child in control.Controls) AttachDismissHandlers(child);
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.FromArgb(150, 170, 195));
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}
