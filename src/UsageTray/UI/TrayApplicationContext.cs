using UsageTray.App;
using UsageTray.Data;
using UsageTray.Pricing;
using UsageTray.Providers.Antigravity;
using UsageTray.Providers.Codex;
using UsageTray.Services;

namespace UsageTray.UI;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _applicationIcon;
    private readonly AppSettingsStore _settingsStore;
    private readonly UsageDatabase _database;
    private readonly RefreshCoordinator _coordinator;
    private readonly System.Threading.Timer _refreshTimer;
    private readonly System.Windows.Forms.Timer _singleClickTimer;
    private readonly SynchronizationContext _uiContext;
    private readonly ToolStripMenuItem _quotaMenuItem;
    private readonly ToolStripMenuItem _pinQuotaMenuItem;
    private MainForm? _mainForm;
    private QuotaPopupForm? _quotaPopup;
    private DashboardSnapshot _lastSnapshot;
    private bool _quotaPopupPinned;
    private bool _contextMenuOpen;
    private bool _disposed;

    public TrayApplicationContext()
    {
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        AppPaths.EnsureDirectories();
        _settingsStore = new AppSettingsStore(AppPaths.SettingsPath);
        var settings = _settingsStore.Load();
        var pricing = PricingService.LoadOrCreate(AppPaths.PricingPath, Path.Combine(AppContext.BaseDirectory, "Pricing", "default-pricing.json"));
        _database = new UsageDatabase(AppPaths.DatabasePath);
        var repository = new UsageRepository(_database);
        var providers = new Providers.IUsageProvider[] { new CodexProvider(), new AntigravityProvider() };
        _coordinator = new RefreshCoordinator(providers, _settingsStore, repository, pricing, new UsageAggregator(repository, pricing), settings);
        _lastSnapshot = _coordinator.CurrentSnapshot;
        _coordinator.SnapshotChanged += (_, snapshot) => _uiContext.Post(_ => ApplySnapshot(snapshot), null);

        _singleClickTimer = new System.Windows.Forms.Timer
        {
            Interval = Math.Max(180, Math.Min(350, SystemInformation.DoubleClickTime))
        };
        _singleClickTimer.Tick += (_, _) =>
        {
            _singleClickTimer.Stop();
            if (!_contextMenuOpen) ToggleQuotaPopup();
        };

        _applicationIcon = AppIcon.Create();
        _notifyIcon = new NotifyIcon { Icon = _applicationIcon, Visible = true, Text = "AI Usage Tray" };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                if (_quotaPopup is { IsDisposed: false, Visible: true })
                {
                    _singleClickTimer.Stop();
                    HideQuotaPopup();
                }
                else
                {
                    _singleClickTimer.Stop();
                    _singleClickTimer.Start();
                }
            }
        };
        _notifyIcon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                _singleClickTimer.Stop();
                HideQuotaPopup();
                ShowMainForm();
            }
        };

        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) =>
        {
            _contextMenuOpen = true;
            _singleClickTimer.Stop();
            HideQuotaPopup();
        };
        menu.Closed += (_, _) =>
        {
            _contextMenuOpen = false;
            if (_quotaPopupPinned) ShowQuotaPopup();
        };
        menu.Items.Add("打开仪表盘", null, (_, _) => ShowMainForm());
        menu.Items.Add("立即刷新（增量）", null, async (_, _) => await RefreshAsync(false));
        _quotaMenuItem = new ToolStripMenuItem("额度：等待刷新") { Enabled = false };
        menu.Items.Add(_quotaMenuItem);
        _pinQuotaMenuItem = new ToolStripMenuItem("固定显示额度摘要") { CheckOnClick = true };
        _pinQuotaMenuItem.Click += (_, _) => SetQuotaPopupPinned(_pinQuotaMenuItem.Checked);
        menu.Items.Add(_pinQuotaMenuItem);
        var startup = new ToolStripMenuItem("开机启动") { Checked = settings.StartWithWindows, CheckOnClick = true };
        startup.Click += (_, _) => { try { new StartupManager().SetEnabled(startup.Checked); } catch { } };
        menu.Items.Add(startup);
        menu.Items.Add("设置", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApplication());
        _notifyIcon.ContextMenuStrip = menu;

        _refreshTimer = new System.Threading.Timer(async _ => await RefreshAsync(false), null,
            TimeSpan.FromSeconds(settings.RefreshSeconds), TimeSpan.FromSeconds(settings.RefreshSeconds));
        ApplySnapshot(_lastSnapshot);

        var now = DateTimeOffset.UtcNow;
        var isWeeklyFullScanDue = !settings.LastFullScanUtc.HasValue || (now - settings.LastFullScanUtc.Value).TotalDays >= 7;
        _ = RefreshAsync(isWeeklyFullScanDue);
    }

    private async Task RefreshAsync(bool force)
    {
        try { await _coordinator.RefreshAsync(force); } catch { }
    }

    private void ApplySnapshot(DashboardSnapshot snapshot)
    {
        if (_disposed) return;
        _lastSnapshot = snapshot;
        if (_mainForm is { IsDisposed: false }) _mainForm.RefreshCurrentSelection();
        if (_quotaPopup is { IsDisposed: false, Visible: true }) _quotaPopup.SetSnapshot(snapshot);
        UpdateTooltip(snapshot);
    }

    private void ShowMainForm()
    {
        _mainForm ??= CreateMainForm();
        if (!_mainForm.Visible) _mainForm.Show();
        _mainForm.WindowState = FormWindowState.Normal;
        _mainForm.Activate();
        _mainForm.RefreshCurrentSelection();
    }

    private MainForm CreateMainForm()
    {
        var form = new MainForm(_coordinator, _settingsStore);
        WindowGeometryPersistence.Attach(form, _settingsStore);
        return form;
    }

    private void ShowSettings()
    {
        ShowMainForm();
        using var form = new SettingsForm(_settingsStore, _coordinator);
        form.ShowDialog(_mainForm);
    }

    private void ToggleQuotaPopup()
    {
        if (_quotaPopup is { IsDisposed: false, Visible: true })
            HideQuotaPopup();
        else
            ShowQuotaPopup();
    }

    private void TogglePinnedQuotaPopup() => SetQuotaPopupPinned(!_quotaPopupPinned);

    private void SetQuotaPopupPinned(bool pinned)
    {
        if (_disposed) return;
        _quotaPopupPinned = pinned;
        _pinQuotaMenuItem.Checked = pinned;
        if (!pinned)
        {
            HideQuotaPopup();
            return;
        }

        if (!_contextMenuOpen) ShowQuotaPopup();
    }

    private void ShowQuotaPopup()
    {
        if (_disposed || _contextMenuOpen || _notifyIcon.ContextMenuStrip?.Visible == true) return;
        _quotaPopup ??= CreateQuotaPopup();
        _quotaPopup.SetSnapshot(_lastSnapshot);
        _quotaPopup.ShowAt(Cursor.Position);
    }

    private QuotaPopupForm CreateQuotaPopup()
    {
        var popup = new QuotaPopupForm();
        popup.DismissRequested += (_, _) =>
        {
            if (!_quotaPopupPinned) HideQuotaPopup();
        };
        return popup;
    }

    private void HideQuotaPopup()
    {
        if (_quotaPopup is { IsDisposed: false, Visible: true }) _quotaPopup.Hide();
    }

    private void UpdateTooltip(DashboardSnapshot snapshot)
    {
        var text = QuotaDisplayFormatter.BuildCompactText(snapshot);
        _notifyIcon.Text = text.Length > 63 ? text[..63] : text;
        _quotaMenuItem.Text = $"额度：{text}";
    }

    private void ExitApplication()
    {
        DisposeResources();
        ExitThread();
    }

    private void DisposeResources()
    {
        if (_disposed) return;
        _disposed = true;
        _singleClickTimer.Stop();
        _singleClickTimer.Dispose();
        _quotaPopup?.Dispose();
        _refreshTimer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _applicationIcon.Dispose();
        _coordinator.Dispose();
        _database.Dispose();
        _mainForm?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeResources();
        base.Dispose(disposing);
    }
}
