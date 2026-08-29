using UsageTray.App;

namespace UsageTray.UI;

internal static class WindowGeometryPersistence
{
    public static void Attach(MainForm form, AppSettingsStore settingsStore)
    {
        var settings = settingsStore.Load();
        if (settings.MainWindowWidth is int width && settings.MainWindowHeight is int height)
            form.ClientSize = new Size(width, height);

        form.RestoreColumnWidths(settings.ModelColumnWidths, settings.ProjectColumnWidths);

        var debounceTimer = new System.Windows.Forms.Timer { Interval = 500 };
        debounceTimer.Tick += (_, _) =>
        {
            debounceTimer.Stop();
            if (!form.IsDisposed)
            {
                Save(form, settingsStore);
            }
        };

        form.ColumnWidthsChanged += (_, _) =>
        {
            if (form.IsDisposed) return;
            debounceTimer.Stop();
            debounceTimer.Start();
        };

        form.ResizeEnd += (_, _) =>
        {
            debounceTimer.Stop();
            Save(form, settingsStore);
        };
        form.FormClosing += (_, _) =>
        {
            debounceTimer.Stop();
            Save(form, settingsStore);
        };
        form.VisibleChanged += (_, _) =>
        {
            if (!form.Visible)
            {
                debounceTimer.Stop();
                Save(form, settingsStore);
            }
        };
        form.Disposed += (_, _) =>
        {
            debounceTimer.Stop();
            debounceTimer.Dispose();
        };
    }

    public static void Save(MainForm? form, AppSettingsStore settingsStore)
    {
        if (form == null || form.IsDisposed)
            return;

        var modelWidths = form.GetModelColumnWidths();
        var projectWidths = form.GetProjectColumnWidths();
        var isNormal = form.WindowState == FormWindowState.Normal;
        var clientWidth = isNormal && form.ClientSize.Width > 0 ? (int?)form.ClientSize.Width : null;
        var clientHeight = isNormal && form.ClientSize.Height > 0 ? (int?)form.ClientSize.Height : null;

        settingsStore.Update(settings =>
        {
            if (clientWidth.HasValue) settings.MainWindowWidth = clientWidth.Value;
            if (clientHeight.HasValue) settings.MainWindowHeight = clientHeight.Value;
            if (modelWidths.Count > 0) settings.ModelColumnWidths = modelWidths;
            if (projectWidths.Count > 0) settings.ProjectColumnWidths = projectWidths;
        });
    }
}
