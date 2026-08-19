using UsageTray.App;
using UsageTray.Providers.Antigravity;
using UsageTray.UI;

namespace UsageTray;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(arg => string.Equals(arg, "--status-recorder", StringComparison.OrdinalIgnoreCase)))
            return AntigravityStatusRecorderCli.RunAsync(args).GetAwaiter().GetResult();

        if (!SingleInstance.TryAcquire("Global\\UsageTray.SingleInstance", out var singleInstance)) return 0;
        using (singleInstance)
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new TrayApplicationContext());
        }
        return 0;
    }
}
