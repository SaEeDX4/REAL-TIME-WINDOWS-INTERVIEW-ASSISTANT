using System.Security.Cryptography;
using System.Text;

namespace InterviewAssistant.Core.Persistence;

/// <summary>
/// One local workspace per signed-in account, so another account on the same Windows user can never see this
/// account's profiles, résumé facts, interviews, prepared answers or reports:
///   {base}/accounts/{hash(userId)}/…   signed-in accounts (hash: no e-mail or id in the path)
///   {base}/local/…                     developer builds without an account
/// The pre-RC2 shared workspace ({base}/profiles, …) is adopted once, by the first account that signs in after the
/// upgrade, and a marker records which account it went to.
/// </summary>
public static class WorkspaceLocator
{
    private static readonly string[] Folders = { "profiles", "targets", "packs", "sessions" };
    public const string LegacyMarker = "legacy-adopted-by.txt";

    public static string AccountKey(string userId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("ia-workspace:" + userId.Trim().ToLowerInvariant())))[..24].ToLowerInvariant();

    public static string RootFor(string baseDir, string? userId) =>
        string.IsNullOrWhiteSpace(userId) ? Path.Combine(baseDir, "local") : Path.Combine(baseDir, "accounts", AccountKey(userId));

    public static bool HasLegacyData(string baseDir) =>
        Folders.Any(f => Directory.Exists(Path.Combine(baseDir, f)) && Directory.EnumerateFiles(Path.Combine(baseDir, f), "*.bin").Any());

    /// <summary>
    /// Moves the legacy shared workspace into <paramref name="userId"/>'s workspace if nobody adopted it yet and that
    /// workspace is still empty. Returns true when data was moved. Never overwrites existing files.
    /// </summary>
    public static bool AdoptLegacy(string baseDir, string userId)
    {
        var accounts = Path.Combine(baseDir, "accounts");
        var marker = Path.Combine(accounts, LegacyMarker);
        if (File.Exists(marker) || !HasLegacyData(baseDir)) return false;
        var root = RootFor(baseDir, userId);
        if (Folders.Any(f => Directory.Exists(Path.Combine(root, f)) && Directory.EnumerateFiles(Path.Combine(root, f), "*.bin").Any())) return false;
        Directory.CreateDirectory(accounts);
        foreach (var f in Folders)
        {
            var from = Path.Combine(baseDir, f);
            if (!Directory.Exists(from)) continue;
            var to = Path.Combine(root, f);
            Directory.CreateDirectory(to);
            foreach (var file in Directory.EnumerateFiles(from, "*.bin"))
            {
                var dest = Path.Combine(to, Path.GetFileName(file));
                if (!File.Exists(dest)) File.Move(file, dest);
            }
        }
        File.WriteAllText(marker, AccountKey(userId));
        return true;
    }
}
