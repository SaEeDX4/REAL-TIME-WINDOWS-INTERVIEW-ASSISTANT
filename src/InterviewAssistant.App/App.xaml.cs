using System.Windows;
using System.Windows.Threading;
using InterviewAssistant.App.Services;

namespace InterviewAssistant.App;

public partial class App : Application
{
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

        AppLog.Info($"Interview Assistant {typeof(App).Assembly.GetName().Version} starting on {Environment.OSVersion}");
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
