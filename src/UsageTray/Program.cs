using UsageTray.App;
using UsageTray.Providers.Antigravity;
using UsageTray.UI;

namespace UsageTray;

internal static class Program
{
    private const string MutexName = "UsageTray.SingleInstance";
    private const string WakeEventName = "UsageTray.WakeUpEvent";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(arg => string.Equals(arg, "--status-recorder", StringComparison.OrdinalIgnoreCase)))
            return AntigravityStatusRecorderCli.RunAsync(args).GetAwaiter().GetResult();

        if (args.Any(arg => string.Equals(arg, "--rebuild-codex", StringComparison.OrdinalIgnoreCase)))
            return CodexMaintenanceCli.Rebuild();

        if (!SingleInstance.TryAcquire(MutexName, out var singleInstance))
        {
            // 已有实例在运行：向其发送唤醒信号，拉起主界面置顶，避免用户二次点击无反应
            try
            {
                if (EventWaitHandle.TryOpenExisting(WakeEventName, out var wakeEvent))
                {
                    wakeEvent.Set();
                    wakeEvent.Dispose();
                }
            }
            catch
            {
                // ignored
            }
            return 0;
        }

        using (singleInstance)
        {
            bool isAutoStart = args.Any(arg => string.Equals(arg, "--autostart", StringComparison.OrdinalIgnoreCase) ||
                                              string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase));
            ApplicationConfiguration.Initialize();
            using var wakeHandle = new EventWaitHandle(false, EventResetMode.AutoReset, WakeEventName);
            var context = new TrayApplicationContext(isAutoStart);

            // 监听二次启动唤醒信号
            var waitRegistration = ThreadPool.RegisterWaitForSingleObject(
                wakeHandle,
                (_, _) => context.ActivateMainForm(),
                null,
                -1,
                false);

            try
            {
                Application.Run(context);
            }
            finally
            {
                waitRegistration.Unregister(null);
            }
        }
        return 0;
    }
}
