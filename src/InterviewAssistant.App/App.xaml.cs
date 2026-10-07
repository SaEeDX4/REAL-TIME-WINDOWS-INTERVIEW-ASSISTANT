using System.Windows;
using System.Windows.Threading;
using System.IO;
using InterviewAssistant.App.Services;

namespace InterviewAssistant.App;

public partial class App : Application
{
    /// <summary>Set by --selftest; used as the process exit code after clean shutdown.</summary>
    public static int RequestedExitCode { get; set; }
    public static string? SelfTestOutput { get; private set; }
    /// <summary>--fixture &lt;dir&gt;: test pack used by the self-test (never shipped in the product).</summary>
    public static string? FixtureDir { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // A live-interview tool must never die on an unexpected exception: log, surface, continue.
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("UI exception", args.Exception);
            (MainWindow as MainWindow)?.ShowTransientError("Unexpected error — the assistant is still running. Details in diagnostics.");
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) => { AppLog.Error("Background task exception", args.Exception); args.SetObserved(); };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => AppLog.Error("Fatal exception", args.ExceptionObject as Exception);

        var idx = Array.FindIndex(e.Args, a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            SelfTestOutput = Path.GetFullPath(idx + 1 < e.Args.Length ? e.Args[idx + 1] : "selftest-result.json");
            var f = Array.FindIndex(e.Args, a => a.Equals("--fixture", StringComparison.OrdinalIgnoreCase));
            if (f >= 0 && f + 1 < e.Args.Length) FixtureDir = Path.GetFullPath(e.Args[f + 1]);
            // Isolate from the user's real settings so the self-test never changes them.
            AppSettings.OverridePath = Path.Combine(Path.GetTempPath(), $"ia-selftest-{Environment.ProcessId}.json");
            AppPaths.IsolatedRoot = Path.Combine(Path.GetTempPath(), $"ia-selftest-{Environment.ProcessId}");
        }
        Branding.Load(AppContext.BaseDirectory);
        AppLog.Info($"{Branding.ProductName} {typeof(App).Assembly.GetName().Version} starting on {Environment.OSVersion}");
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
