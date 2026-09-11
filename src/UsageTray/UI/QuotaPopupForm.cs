using UsageTray.Core;
using UsageTray.Services;
using UsageTray.UI.Controls;

namespace UsageTray.UI;

internal sealed class QuotaPopupForm : Form
{
    private readonly QuotaSummaryControl _contentControl;
    private bool _isMouseDown;
    private bool _isDragging;
    private bool _hasBeenDragged;
    private Point _dragStartScreenPoint;
    private Point _formStartLocation;

    public event EventHandler? DismissRequested;
    public event EventHandler? CloseRequested;

    public QuotaPopupForm()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        Icon = AppIcon.Create();
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        ShowIcon = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(226, 232, 240);
        Padding = new Padding(1);
        DoubleBuffered = true;

        _contentControl = new QuotaSummaryControl
        {
            Dock = DockStyle.Fill,
            ShowHeader = true,
            ShowDismissHint = true,
            ShowFooterNote = true,
            BackColor = Color.White
        };

        _contentControl.MouseDown += HandleChildMouseDown;
        _contentControl.MouseMove += HandleChildMouseMove;
        _contentControl.MouseUp += HandleChildMouseUp;

        MouseDown += HandleChildMouseDown;
        MouseMove += HandleChildMouseMove;
        MouseUp += HandleChildMouseUp;
        Controls.Add(_contentControl);

        Deactivate += (_, _) =>
        {
            if (!_hasBeenDragged)
            {
                DismissRequested?.Invoke(this, EventArgs.Empty);
            }
        };

        SetSnapshot(null);
    }

    private void HandleChildMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _isMouseDown = true;
            _isDragging = false;
            _dragStartScreenPoint = ((Control)sender!).PointToScreen(e.Location);
            ((Control)sender!).Capture = true;
            _formStartLocation = Location;
        }
    }

    private void HandleChildMouseMove(object? sender, MouseEventArgs e)
    {
        if (_isMouseDown)
        {
            var currentScreen = ((Control)sender!).PointToScreen(e.Location);
            var dx = currentScreen.X - _dragStartScreenPoint.X;
            var dy = currentScreen.Y - _dragStartScreenPoint.Y;
            if (!_isDragging && (Math.Abs(dx) > 3 || Math.Abs(dy) > 3))
            {
                _isDragging = true;
                _hasBeenDragged = true;
                _contentControl.IsLocked = true;
                _contentControl.Invalidate();
            }
            if (_isDragging)
            {
                Location = new Point(_formStartLocation.X + dx, _formStartLocation.Y + dy);
            }
        }
    }

    private void HandleChildMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !_isMouseDown) return;
        var wasDragging = _isDragging;
        _isMouseDown = false;
        _isDragging = false;
        ((Control)sender!).Capture = false;
        if (!wasDragging) RequestClose();
    }

    private void RequestClose()
    {
        _isMouseDown = false;
        _isDragging = false;
        _contentControl.Capture = false;
        Capture = false;
        Hide();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { RequestClose(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public void SetSnapshot(DashboardSnapshot? snapshot)
    {
        _contentControl.SetSnapshot(snapshot);
        RecalculateSize();
    }

    public void UpdateProviderSettings(bool enableCodex, bool enableAntigravity)
    {
        _contentControl.EnableCodex = enableCodex;
        _contentControl.EnableAntigravity = enableAntigravity;
        RecalculateSize();
        _contentControl.Invalidate();
    }

    private void RecalculateSize()
    {
        var width = (int)Math.Round(580f * DeviceDpi / 96f);
        var height = _contentControl.MeasureHeight(width) + Padding.Vertical;
        var area = Screen.FromPoint(Visible ? Location : Cursor.Position).WorkingArea;
        ClientSize = new Size(Math.Min(width, Math.Max(1, area.Width - 8)),
            Math.Min(height, Math.Max(1, area.Height - 8)));
    }

    public void ShowAt(Point cursor)
    {
        _hasBeenDragged = false;
        _contentControl.IsLocked = false;
        var screen = Screen.FromPoint(cursor).WorkingArea;
        var bounds = FitToWorkingArea(cursor, Size, screen);
        Size = bounds.Size;
        Location = bounds.Location;
        if (!Visible) Show();
        else BringToFront();
        TopMost = true;
    }

    internal static Rectangle FitToWorkingArea(Point cursor, Size requested, Rectangle area)
    {
        var width = Math.Min(requested.Width, Math.Max(1, area.Width - 8));
        var height = Math.Min(requested.Height, Math.Max(1, area.Height - 8));
        var x = Math.Clamp(cursor.X - width + 12, area.Left + 4, area.Right - width - 4);
        var y = Math.Clamp(cursor.Y - height - 12, area.Top + 4, area.Bottom - height - 4);
        return new Rectangle(x, y, width, height);
    }

    protected override bool ShowWithoutActivation => true;
}
