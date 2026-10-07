using Velopack;

namespace InterviewAssistant.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Handles install/update/uninstall hooks (and exits for them) before any UI is created.
        VelopackApp.Build().Run();
        var u = Array.FindIndex(args, a => a.Equals("--update-test", StringComparison.OrdinalIgnoreCase));
        if (u >= 0 && u + 2 < args.Length)
        {
            Environment.ExitCode = Services.UpdateService.RunUpdateTestAsync(args[u + 1], args[u + 2]).GetAwaiter().GetResult();
            return;
        }
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
