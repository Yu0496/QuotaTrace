using System.Drawing.Drawing2D;
using UsageTray.Services;

namespace UsageTray.UI.Controls;

public sealed class DailyBarChartControl : Control
{
    private IReadOnlyList<DailyUsageView> _items = [];

    public DailyBarChartControl()
    {
        DoubleBuffered = true;
        BackColor = Color.White;
        MinimumSize = new Size(0, 150);
    }

    public void SetData(IReadOnlyList<DailyUsageView> items)
    {
        _items = items;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var labelHeight = (int)Math.Ceiling(e.Graphics.MeasureString("00-00", Font).Height);
        var bottomPadding = Math.Max(10, labelHeight + 8);
        var axisY = Math.Max(1, Height - bottomPadding);
        var topPadding = Math.Max(8, labelHeight / 2);
        var plotHeight = Math.Max(1, axisY - topPadding - 2);

        using var axis = new Pen(Color.LightGray);
        e.Graphics.DrawLine(axis, 8, axisY, Math.Max(8, Width - 8), axisY);
        if (_items.Count == 0)
        {
            using var emptyBrush = new SolidBrush(Color.Gray);
            e.Graphics.DrawString("暂无已记录的真实 token", Font, emptyBrush, 12, topPadding);
            return;
        }

        var values = _items.Select(item => item.ApiEquivalentUsd ?? 0m).ToList();
        var max = Math.Max(0.01m, values.Max());
        var slot = Math.Max(4, (Width - 20) / Math.Max(1, values.Count));
        for (var index = 0; index < values.Count; index++)
        {
            var barHeight = (int)(plotHeight * values[index] / max);
            var x = 10 + index * slot;
            var y = axisY - barHeight;
            using var brush = new SolidBrush(Color.FromArgb(69, 125, 196));
            e.Graphics.FillRectangle(brush, x, y, Math.Max(2, slot - 2), Math.Max(1, barHeight));
            if (values.Count <= 14)
            {
                using var textBrush = new SolidBrush(Color.DimGray);
                var labelY = Math.Min(axisY + 3, Math.Max(0, Height - labelHeight));
                e.Graphics.DrawString(_items[index].Date.ToString("MM-dd"), Font, textBrush, x, labelY);
            }
        }
    }
}
