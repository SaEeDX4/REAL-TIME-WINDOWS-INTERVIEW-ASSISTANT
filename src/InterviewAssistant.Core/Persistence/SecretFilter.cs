using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace InterviewAssistant.Core.Persistence;

/// <summary>
/// Defensive filter applied to every file the user exports (account data, profile backups, raw ZIPs): properties whose
/// NAME says they hold a password, sign-in token, provider credential, API key or other secret are removed, and string
/// VALUES that look like a credential (API keys, JWTs, bearer headers, private keys) are replaced. The app does not store
/// such values in these documents today — the filter makes sure a future field or a pasted key can never leak through
/// an export.
/// </summary>
public static class SecretFilter
{
    /// <summary>Replacement for a secret-looking value found inside ordinary text.</summary>
    public const string Removed = "[removed]";

    // Matched against the property name lower-cased with every non-letter/digit removed ("refresh_token" → "refreshtoken").
    private static readonly string[] KeyMarkers =
    {
        "password", "passwd", "passphrase", "pwd", "secret", "token", "apikey", "accesskey", "privatekey", "signingkey",
        "credential", "authorization", "bearer", "cookie", "jwt", "sessionkey", "clientkey",
    };

    private static readonly Regex SecretValue = new(
        @"\bsk-[A-Za-z0-9_\-]{16,}" +                                         // OpenAI-style API keys
        @"|\beyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}" + // JSON Web Tokens (access / refresh / id tokens)
        @"|\b[Bb]earer\s+[A-Za-z0-9._~+/\-]{16,}=*" +                         // Authorization headers
        @"|\bpdl_[A-Za-z0-9_]{12,}" +                                         // Paddle API keys
        @"|\b(?:ghp|gho|ghs|github_pat)_[A-Za-z0-9_]{20,}" +                  // GitHub tokens
        @"|\bxox[abprs]-[A-Za-z0-9\-]{10,}" +                                 // Slack tokens
        @"|\bAKIA[0-9A-Z]{16}\b" +                                            // AWS access keys
        @"|-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(?:-----END [A-Z ]*PRIVATE KEY-----|$)" +
        @"|\b(?i:password|passwd|api[_\-]?key|secret|access[_\-]?token|refresh[_\-]?token)\s*[:=]\s*\S+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    /// <summary>True when a property with this name must never appear in an export.</summary>
    public static bool IsSecretKey(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        Span<char> buf = stackalloc char[Math.Min(name.Length, 128)];
        int n = 0;
        foreach (var c in name)
        {
            if (n == buf.Length) break;
            if (char.IsAsciiLetterOrDigit(c)) buf[n++] = char.ToLowerInvariant(c);
        }
        var norm = buf[..n].ToString();
        foreach (var m in KeyMarkers)
            if (norm.Contains(m, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>True when the text contains something shaped like a credential.</summary>
    public static bool LooksSecret(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        try { return SecretValue.IsMatch(text); }
        catch (RegexMatchTimeoutException) { return true; }
    }

    /// <summary>The text with every credential-shaped part replaced by <see cref="Removed"/>.</summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        try { return SecretValue.Replace(text, Removed); }
        catch (RegexMatchTimeoutException) { return Removed; }
    }

    /// <summary>Cleans a JSON tree in place (also JSON documents stored inside string values). Returns true if anything was removed.</summary>
    public static bool Clean(JsonNode? node, int depth = 0)
    {
        if (node == null || depth > 96) return false;
        var changed = false;
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(kv => kv.Key).ToList())
                {
                    if (IsSecretKey(key)) { o.Remove(key); changed = true; continue; }
                    var child = o[key];
                    if (child is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        var cleaned = CleanString(s, depth, out var c);
                        if (c) { o[key] = cleaned; changed = true; }
                    }
                    else changed |= Clean(child, depth + 1);
                }
                break;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        var cleaned = CleanString(s, depth, out var c);
                        if (c) { a[i] = cleaned; changed = true; }
                    }
                    else changed |= Clean(a[i], depth + 1);
                }
                break;
        }
        return changed;
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>A string value: a nested JSON document (stored reports) is cleaned as JSON, other text is redacted.</summary>
    private static string CleanString(string s, int depth, out bool changed)
    {
        changed = false;
        var t = s.TrimStart();
        if (t.Length > 1 && (t[0] == '{' || t[0] == '['))
        {
            try
            {
                var inner = JsonNode.Parse(s, documentOptions: new JsonDocumentOptions { MaxDepth = 128 });
                if (inner != null)
                {
                    if (!Clean(inner, depth + 1)) return s;
                    changed = true;
                    return inner.ToJsonString(Indented);
                }
            }
            catch (JsonException) { /* not JSON: treat as text */ }
        }
        if (!LooksSecret(s)) return s;
        changed = true;
        return Redact(s);
    }

    /// <summary>Serializes <paramref name="value"/> to JSON with secrets removed.</summary>
    public static string ToJson<T>(T value, bool indented = true)
    {
        var node = JsonSerializer.SerializeToNode(value);
        Clean(node);
        return node?.ToJsonString(indented ? Indented : null) ?? "null";
    }

    /// <summary>A cleaned copy of a JSON element (account export from the service).</summary>
    public static JsonElement Clean(JsonElement element)
    {
        var node = JsonNode.Parse(element.GetRawText(), documentOptions: new JsonDocumentOptions { MaxDepth = 128 });
        Clean(node);
        using var doc = JsonDocument.Parse(node?.ToJsonString() ?? "null", new JsonDocumentOptions { MaxDepth = 128 });
        return doc.RootElement.Clone();
    }
}
