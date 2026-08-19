using UsageTray.App;

namespace UsageTray.UI;

internal static class WindowGeometryPersistence
{
    public static void Attach(MainForm form, AppSettingsStore settingsStore)
    {
        var settings = settingsStore.Load();
        if (settings.MainWindowWidth is int width && settings.MainWindowHeight is int height)
            form.ClientSize = new Size(width, height);

        form.ResizeEnd += (_, _) => Save(form, settingsStore);
        form.FormClosing += (_, _) => Save(form, settingsStore);
    }

    private static void Save(Form form, AppSettingsStore settingsStore)
    {
        if (form.WindowState != FormWindowState.Normal || form.ClientSize.Width <= 0 || form.ClientSize.Height <= 0)
            return;

        var settings = settingsStore.Load();
        settings.MainWindowWidth = form.ClientSize.Width;
        settings.MainWindowHeight = form.ClientSize.Height;
        settingsStore.Save(settings);
    }
}
