namespace InterviewAssistant.App.Services;

public static class AppPaths
{
    /// <summary>Self-test isolation: user data (settings, workspace, tokens, reports) goes to a throwaway folder. Logs stay put.</summary>
    public static string? IsolatedRoot { get; set; }

    public static string Roaming => Ensure(IsolatedRoot != null ? Path.Combine(IsolatedRoot, "roaming") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InterviewAssistant"));
    public static string Local => Ensure(IsolatedRoot != null ? Path.Combine(IsolatedRoot, "local") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InterviewAssistant"));
    public static string Logs => Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InterviewAssistant", "logs"));
    public static string Sessions => Ensure(IsolatedRoot != null ? Path.Combine(IsolatedRoot, "sessions") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "InterviewAssistant Sessions"));
    private static string Ensure(string p) { Directory.CreateDirectory(p); return p; }
}
