namespace InterviewAssistant.Core.Diagnostics;

/// <summary>
/// Detects an installed (Setup.exe / Velopack) copy from the executable's location, independent of whether automatic
/// updates are enabled. Velopack installs per user as %LocalAppData%\{AppId}\current\{App}.exe next to
/// %LocalAppData%\{AppId}\Update.exe; a portable ZIP runs from any other folder.
/// </summary>
public static class InstallLayout
{
    public static bool IsInstalled(string? processPath, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(processPath)) return false;
        var dir = Path.GetDirectoryName(processPath);
        if (dir == null || !string.Equals(Path.GetFileName(dir), "current", StringComparison.OrdinalIgnoreCase)) return false;
        var root = Path.GetDirectoryName(dir);
        return root != null && fileExists(Path.Combine(root, "Update.exe"));
    }
}
