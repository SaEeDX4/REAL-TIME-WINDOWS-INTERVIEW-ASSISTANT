using System.Security.Cryptography;

namespace InterviewAssistant.Client;

/// <summary>
/// Random per-installation identifier (no hardware fingerprinting). Survives upgrades because it lives in the user
/// profile, so reinstalling on the same machine re-uses the same device slot.
/// </summary>
public static class InstallationIdentity
{
    public static string GetOrCreate(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (IsValid(existing)) return existing;
            }
        }
        catch (IOException) { }
        var id = "inst-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, id);
        return id;
    }

    public static bool IsValid(string s) => s.Length is >= 12 and <= 64 && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
}
