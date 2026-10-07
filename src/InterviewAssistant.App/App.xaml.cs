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
            // Isolate from the user's real settings so the self-test never changes them.
            AppSettings.OverridePath = Path.Combine(Path.GetTempPath(), $"ia-selftest-{Environment.ProcessId}.json");
        }
        Branding.Load(AppContext.BaseDirectory);
        AppLog.Info($"{Branding.ProductName} {typeof(App).Assembly.GetName().Version} starting on {Environment.OSVersion}");
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
