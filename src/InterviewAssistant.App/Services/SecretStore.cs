using System.Security.Cryptography;
using System.Text;

namespace InterviewAssistant.App.Services;

/// <summary>
/// Stores the API key encrypted with Windows DPAPI (CurrentUser scope): only this Windows user on this machine
/// can decrypt it. Never written in plaintext, never logged, never packaged.
/// </summary>
public static class SecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("InterviewAssistant.v1.apikey");
    private static string FilePath => Path.Combine(AppPaths.Roaming, "apikey.dpapi");
    private static string? _cache;

    public static bool HasKey => !string.IsNullOrEmpty(GetApiKey());

    public static string? GetApiKey()
    {
        if (_cache != null) return _cache;
        try
        {
            if (!File.Exists(FilePath)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            _cache = Encoding.UTF8.GetString(plain);
            CryptographicOperations.ZeroMemory(plain);
            return _cache;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Stored API key could not be decrypted (" + ex.GetType().Name + "). Please re-enter it.");
            return null;
        }
    }

    public static void SetApiKey(string key)
    {
        key = key.Trim();
        var plain = Encoding.UTF8.GetBytes(key);
        var enc = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(plain);
        File.WriteAllBytes(FilePath, enc);
        _cache = key;
    }

    public static void Clear()
    {
        _cache = null;
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }

    public static string Mask(string? key) => string.IsNullOrEmpty(key) ? "(not set)" : key.Length <= 8 ? "••••" : key[..3] + "…" + key[^4..];
}
