using UsageTray.App;
using UsageTray.Services;

namespace UsageTray.UI;

public sealed class UpdateDialog : Form
{
    private readonly ReleaseInfo _release;
    private readonly CancellationTokenSource _cts = new();

    private readonly Button _btnDirectUpdate;
    private readonly Button _btnOpenGitHub;
    private readonly Button _btnCancel;
    private readonly ProgressBar _progressBar;
    private readonly Label _progressLabel;
    private readonly TextBox _bodyBox;

    public UpdateDialog(ReleaseInfo release)
    {
        _release = release;

        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font("Segoe UI", 9F);
        Icon = AppIcon.Create();
        Text = I18n.T("发现新版本 - QuotaTrace", "New Version Available - QuotaTrace");
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(540, 420);

        var topPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 70,
            Padding = new Padding(16, 12, 16, 8),
            BackColor = Color.FromArgb(248, 250, 252)
        };

        var titleLabel = new Label
        {
            Text = I18n.Format("发现新版本 {0}", "New version {0} is available", release.TagName),
            Font = new Font(Font.FontFamily, 12F, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 23, 42),
            AutoSize = true,
            Dock = DockStyle.Top
        };

        var subtitleLabel = new Label
        {
            Text = I18n.Format(
                "当前版本：{0}  |  发布时间：{1:yyyy-MM-dd HH:mm}",
                "Current: {0}  |  Released: {1:yyyy-MM-dd HH:mm}",
                AppUpdateService.GetCurrentVersionString(),
                release.PublishedAt.ToLocalTime()),
            ForeColor = Color.FromArgb(100, 116, 139),
            AutoSize = true,
            Dock = DockStyle.Bottom
        };

        topPanel.Controls.Add(titleLabel);
        topPanel.Controls.Add(subtitleLabel);

        var changelogLabel = new Label
        {
            Text = I18n.T("更新说明：", "Release Notes:"),
            Font = new Font(Font, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 4)
        };

        _bodyBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(30, 41, 59),
            Text = string.IsNullOrWhiteSpace(release.Description)
                ? I18n.T("（暂无更新说明）", "(No release notes provided)")
                : release.Description.Replace("\n", Environment.NewLine),
            Margin = new Padding(0, 4, 0, 8)
        };

        _progressBar = new ProgressBar
        {
            Dock = DockStyle.Top,
            Height = 16,
            Minimum = 0,
            Maximum = 100,
            Visible = false,
            Margin = new Padding(0, 4, 0, 2)
        };

        _progressLabel = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Visible = false,
            ForeColor = Color.FromArgb(71, 85, 105),
            Margin = new Padding(0, 2, 0, 4)
        };

        var centerPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 12, 16, 8)
        };
        centerPanel.Controls.Add(_bodyBox);
        centerPanel.Controls.Add(_progressLabel);
        centerPanel.Controls.Add(_progressBar);
        centerPanel.Controls.Add(changelogLabel);

        var bottomPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 54,
            Padding = new Padding(16, 8, 16, 12),
            BackColor = Color.FromArgb(248, 250, 252)
        };

        var buttonsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true
        };

        _btnCancel = new Button
        {
            Text = I18n.T("稍后提醒", "Remind Later"),
            DialogResult = DialogResult.Cancel,
            Height = 32,
            AutoSize = true,
            Padding = new Padding(10, 0, 10, 0),
            Margin = new Padding(6, 0, 0, 0)
        };
        _btnCancel.Click += (_, _) => { _cts.Cancel(); Close(); };

        _btnOpenGitHub = new Button
        {
            Text = I18n.T("前往 GitHub 下载", "Open in GitHub"),
            Height = 32,
            AutoSize = true,
            Padding = new Padding(10, 0, 10, 0),
            Margin = new Padding(6, 0, 0, 0)
        };
        _btnOpenGitHub.Click += (_, _) =>
        {
            AppUpdateService.OpenUrl(_release.HtmlUrl);
            Close();
        };

        _btnDirectUpdate = new Button
        {
            Text = I18n.T("在软件内直接升级", "Update in App"),
            Height = 32,
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            BackColor = Color.FromArgb(14, 165, 233),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Padding = new Padding(12, 0, 12, 0),
            Margin = Padding.Empty
        };
        _btnDirectUpdate.FlatAppearance.BorderSize = 0;
        _btnDirectUpdate.Click += async (_, _) => await StartDirectUpdateAsync();

        buttonsPanel.Controls.Add(_btnCancel);
        buttonsPanel.Controls.Add(_btnOpenGitHub);
        buttonsPanel.Controls.Add(_btnDirectUpdate);
        bottomPanel.Controls.Add(buttonsPanel);

        Controls.Add(centerPanel);
        Controls.Add(bottomPanel);
        Controls.Add(topPanel);

        AcceptButton = _btnDirectUpdate;
        CancelButton = _btnCancel;
    }

    private async Task StartDirectUpdateAsync()
    {
        if (_release.Asset == null)
        {
            var res = MessageBox.Show(this,
                I18n.T("当前版本未找到自动安装包资产，是否打开 GitHub 网页手动下载？",
                       "No automatic package asset found for this release. Open GitHub in browser to download?"),
                "QuotaTrace", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (res == DialogResult.Yes)
            {
                AppUpdateService.OpenUrl(_release.HtmlUrl);
                Close();
            }
            return;
        }

        _btnDirectUpdate.Enabled = false;
        _btnOpenGitHub.Enabled = false;
        _btnCancel.Text = I18n.T("取消下载", "Cancel Download");
        _progressBar.Visible = true;
        _progressLabel.Visible = true;
        _progressBar.Value = 0;
        _progressLabel.Text = I18n.T("正在连接下载服务器...", "Connecting to download server...");

        var progress = new Progress<(long received, long total, int percentage)>(p =>
        {
            if (IsDisposed || Disposing) return;
            _progressBar.Value = p.percentage;
            var receivedMb = p.received / 1048576.0;
            var totalMb = p.total / 1048576.0;
            _progressLabel.Text = I18n.Format(
                "正在下载更新包：{0:F1} MB / {1:F1} MB ({2}%)",
                "Downloading update: {0:F1} MB / {1:F1} MB ({2}%)",
                receivedMb, totalMb, p.percentage);
        });

        try
        {
            var scriptPath = await AppUpdateService.DownloadAndPrepareUpdateAsync(_release, progress, _cts.Token);
            _progressBar.Value = 100;
            _progressLabel.Text = I18n.T("下载解压完成，正在重启并应用更新...", "Download complete. Restarting to apply update...");
            await Task.Delay(1000);

            AppUpdateService.ExecuteUpdateScriptAndExit(scriptPath);
        }
        catch (OperationCanceledException)
        {
            _progressBar.Visible = false;
            _progressLabel.Visible = false;
            _btnDirectUpdate.Enabled = true;
            _btnOpenGitHub.Enabled = true;
            _btnCancel.Text = I18n.T("稍后提醒", "Remind Later");
        }
        catch (Exception ex)
        {
            _progressBar.Visible = false;
            _progressLabel.Visible = false;
            _btnDirectUpdate.Enabled = true;
            _btnOpenGitHub.Enabled = true;
            _btnCancel.Text = I18n.T("稍后提醒", "Remind Later");

            MessageBox.Show(this,
                I18n.Format("自动升级失败：{0}\r\n\r\n建议点击“前往 GitHub 下载”手动获取更新。",
                            "Auto-update failed: {0}\r\n\r\nYou can click 'Open in GitHub' to download manually.",
                            ex.Message),
                "QuotaTrace", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _cts.Cancel();
        base.OnFormClosing(e);
    }
}
