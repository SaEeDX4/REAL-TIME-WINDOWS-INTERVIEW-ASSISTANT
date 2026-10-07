using System.Security.Cryptography;
using System.Text;

namespace InterviewAssistant.Client;

/// <summary>RFC 7636 PKCE helpers (S256 only).</summary>
public static class Pkce
{
    public static string CreateVerifier() => Base64Url(RandomNumberGenerator.GetBytes(48));   // 64 chars, within 43–128
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    public static string CreateState() => Base64Url(RandomNumberGenerator.GetBytes(24));

    public static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool IsValidVerifier(string v) =>
        v.Length is >= 43 and <= 128 && v.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~');
}
