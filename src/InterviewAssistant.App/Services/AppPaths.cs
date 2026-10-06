namespace InterviewAssistant.App.Services;

public static class AppPaths
{
    public static string Roaming => Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InterviewAssistant"));
    public static string Local => Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InterviewAssistant"));
    public static string Logs => Ensure(Path.Combine(Local, "logs"));
    public static string Sessions => Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "InterviewAssistant Sessions"));
    private static string Ensure(string p) { Directory.CreateDirectory(p); return p; }
}
