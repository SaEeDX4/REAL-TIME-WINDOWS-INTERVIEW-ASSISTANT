using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace InterviewAssistant.App.Services;

/// <summary>Small structured log: in-memory ring for the diagnostics panel + daily file. Redacts anything key-like.</summary>
public static class AppLog
{
    private static readonly ConcurrentQueue<string> Ring = new();
    private static readonly object FileLock = new();
    private static readonly Regex KeyPattern = new(@"(sk-[A-Za-z0-9_\-]{6})[A-Za-z0-9_\-]+", RegexOptions.Compiled);
    public static event Action<string>? Written;

    public static void Info(string m) => Write("INFO", m);
    public static void Warn(string m) => Write("WARN", m);
    public static void Error(string m, Exception? ex = null) => Write("ERROR", ex == null ? m : $"{m}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {level} {KeyPattern.Replace(message, "$1…")}";
        Ring.Enqueue(line);
        while (Ring.Count > 400 && Ring.TryDequeue(out _)) { }
        try
        {
            lock (FileLock) File.AppendAllText(Path.Combine(AppPaths.Logs, $"app-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
        }
        catch (IOException) { /* logging must never crash the app */ }
        Written?.Invoke(line);
    }

    public static IReadOnlyList<string> Recent => Ring.ToArray();
}
