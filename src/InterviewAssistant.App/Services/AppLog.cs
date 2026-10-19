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
    public static void Error(string m, Exception? ex = null) => Write("ERROR", ex == null ? m : $"{m}: {Describe(ex)}");

    /// <summary>
    /// What the log keeps of an exception: file-system errors only by type (their message holds a path with the Windows
    /// user name and, for exports, the candidate's name — and Copy diagnostics shares the last lines); others with their message.
    /// </summary>
    public static string Describe(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException ? ex.GetType().Name : $"{ex.GetType().Name}: {ex.Message}";

    /// <summary>One log line: control characters (line breaks from untrusted text such as a hostile file's JSON keys) become spaces.</summary>
    public static string Sanitize(string message)
    {
        if (!message.Any(char.IsControl)) return message;
        return string.Create(message.Length, message, (span, m) => { for (int i = 0; i < m.Length; i++) span[i] = char.IsControl(m[i]) ? ' ' : m[i]; });
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {level} {KeyPattern.Replace(Sanitize(message), "$1…")}";
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

    /// <summary>Daily log files older than <paramref name="keepDays"/> days are deleted (called at startup).</summary>
    public static int Prune(int keepDays = 14)
    {
        int removed = 0;
        try
        {
            var cutoff = DateTime.Now.Date.AddDays(-keepDays);
            foreach (var f in Directory.EnumerateFiles(AppPaths.Logs, "app-*.log"))
            {
                var stamp = Path.GetFileNameWithoutExtension(f)["app-".Length..];
                if (DateTime.TryParseExact(stamp, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day) && day < cutoff)
                    try { File.Delete(f); removed++; } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* retention is best-effort */ }
        return removed;
    }
}
