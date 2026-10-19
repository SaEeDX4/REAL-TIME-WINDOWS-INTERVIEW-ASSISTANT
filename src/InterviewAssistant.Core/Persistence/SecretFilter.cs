using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace InterviewAssistant.Core.Persistence;

/// <summary>
/// Defensive filter applied to every file the user exports (account data, profile backups, raw ZIPs): properties whose
/// NAME says they hold a password, sign-in token, provider credential, API key or other secret are removed, and string
/// VALUES that look like a credential (API keys, JWTs, bearer headers, private keys) are replaced. The app does not store
/// such values in these documents today — the filter makes sure a future field or a pasted key can never leak through
/// an export. It fails closed: anything nested deeper than it inspects is removed, and a document it cannot parse is
/// not exported (<see cref="Clean(JsonElement)"/> throws).
/// </summary>
public static class SecretFilter
{
    /// <summary>Replacement for a secret-looking value found inside ordinary text.</summary>
    public const string Removed = "[removed]";

    /// <summary>Containers nested deeper than this are replaced by <see cref="Removed"/> (never passed through unchecked).</summary>
    public const int MaxDepth = 96;

    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = 128 };

    // Matched against the property name lower-cased with every non-letter/digit removed ("refresh_token" → "refreshtoken").
    private static readonly string[] KeyMarkers =
    {
        "password", "passwd", "passphrase", "pwd", "secret", "token", "apikey", "accesskey", "privatekey", "signingkey",
        "credential", "authorization", "bearer", "cookie", "jwt", "sessionkey", "clientkey", "recoverycode", "signature",
    };

    // Matched against whole words of the name (camelCase, snake_case, kebab-case): "Auth", "x-auth", "otpCode" — but not
    // "author" or "footprint".
    private static readonly string[] KeyWords = { "auth", "otp" };

    // Unmistakable credentials: distinctive key/token formats and explicit assignments of credential names. Résumé and
    // interview prose never has these shapes, so even the profile backup (which restores text exactly) removes them.
    private const string CredentialShapes =
        @"\bsk-[A-Za-z0-9_\-]{16,}" +                                         // OpenAI/Anthropic-style API keys
        @"|\beyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}" + // JSON Web Tokens (access / refresh / id tokens)
        @"|\b[Bb]earer\s+[A-Za-z0-9._~+/\-]{16,}=*" +                         // Authorization: Bearer …
        @"|\bBasic\s+(?=[A-Za-z0-9+/]*[0-9+/])[A-Za-z0-9+/]{12,}={0,2}" +     // Authorization: Basic … (base64; "Basic Spanish" is prose)
        @"|\bpdl_[A-Za-z0-9_]{12,}" +                                         // Paddle API keys
        @"|\b(?:ghp|gho|ghs|github_pat)_[A-Za-z0-9_]{20,}" +                  // GitHub tokens
        @"|\bxox[abprs]-[A-Za-z0-9\-]{10,}" +                                 // Slack tokens
        @"|\bAKIA[0-9A-Z]{16}\b" +                                            // AWS access keys
        @"|\bAIza[0-9A-Za-z_\-]{30,}" +                                       // Google API keys (35 after the prefix; shortened copies too)
        @"|\bsb_(?:secret|publishable)_[A-Za-z0-9_\-]{8,}" +                  // Supabase keys
        @"|-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(?:-----END [A-Z ]*PRIVATE KEY-----|$)" +
        // Names that only ever label a credential: any value, also JSON-ish text that is not valid JSON ("access_token": "…").
        @"|\b(?i:(?:access|refresh|id)[_\-]?token|api[_\-]?key|client[_\-]?secret|passwd)[""']?\s*[:=]\s*[""']?[^\s""',;}]+" +
        // "password"/"secret" as an assignment or with a quoted value ("password=…", "secret": "…").
        @"|\b(?i:password|secret)[""']?\s*(?:=\s*[""']?[^\s""',;}]+|:\s*[""'][^""'\s]+[""']?)";

    // Credentials written as a sentence. Also safe for résumé prose ("Secret: active clearance" and "Password management"
    // do not match: the value must be one word with a digit), but only used where text is not restored as-is.
    private const string CredentialSentences =
        @"\b(?i:password|secret)\s*:\s*(?=[^\s""',;}]*\d)[^\s""',;}]{6,}" +
        @"|\b(?i:my\s+password\s+is)\s+\S+";

    private static readonly Regex SecretValue = new(CredentialShapes + "|" + CredentialSentences,
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    private static readonly Regex CredentialValue = new(CredentialShapes,
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    /// <summary>True when a property with this name must never appear in an export.</summary>
    public static bool IsSecretKey(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        // The whole name is checked (no truncation: a long prefix must not hide a marker at the end).
        var norm = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
            if (char.IsAsciiLetterOrDigit(c)) norm.Append(char.ToLowerInvariant(c));
        var n = norm.ToString();
        foreach (var m in KeyMarkers)
            if (n.Contains(m, StringComparison.Ordinal)) return true;
        if (n.EndsWith("key", StringComparison.Ordinal)) return true;   // openAiKey, licenseKey, providerKey (not "keywords")
        foreach (var word in Words(name))
            if (Array.IndexOf(KeyWords, word) >= 0) return true;
        return false;
    }

    /// <summary>The name split into lower-case words at separators and lower→upper case changes ("xAuthToken" → x, auth, token).</summary>
    private static IEnumerable<string> Words(string name)
    {
        var word = new System.Text.StringBuilder();
        char prev = '\0';
        foreach (var c in name)
        {
            var boundary = !char.IsAsciiLetterOrDigit(c) || (char.IsUpper(c) && (char.IsLower(prev) || char.IsDigit(prev)));
            if (boundary && word.Length > 0) { yield return word.ToString(); word.Clear(); }
            if (char.IsAsciiLetterOrDigit(c)) word.Append(char.ToLowerInvariant(c));
            prev = c;
        }
        if (word.Length > 0) yield return word.ToString();
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
    /// <param name="keepProse">True for the profile backup, which restores the user's text exactly as written: values are
    /// rewritten only for unmistakable credentials (key formats, tokens, "password=…"), never for sentence-like text.
    /// False (readable and raw exports) also replaces credentials written as a sentence.</param>
    public static bool Clean(JsonNode? node, int depth = 0, bool keepProse = false)
    {
        if (node == null) return false;
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
                        var cleaned = CleanString(s, depth, keepProse, out var c);
                        if (c) { o[key] = cleaned; changed = true; }
                    }
                    else if (child is JsonObject or JsonArray && depth + 1 > MaxDepth) { o[key] = Removed; changed = true; }   // fail closed
                    else changed |= Clean(child, depth + 1, keepProse);
                }
                break;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        var cleaned = CleanString(s, depth, keepProse, out var c);
                        if (c) { a[i] = cleaned; changed = true; }
                    }
                    else if (a[i] is JsonObject or JsonArray && depth + 1 > MaxDepth) { a[i] = Removed; changed = true; }   // fail closed
                    else changed |= Clean(a[i], depth + 1, keepProse);
                }
                break;
        }
        return changed;
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>A string value: a nested JSON document (stored reports) is cleaned as JSON, other text is redacted.</summary>
    private static string CleanString(string s, int depth, bool keepProse, out bool changed)
    {
        changed = false;
        var t = s.TrimStart();
        if (t.Length > 1 && (t[0] == '{' || t[0] == '['))
        {
            JsonNode? inner = null;
            try { inner = JsonNode.Parse(s, documentOptions: ParseOptions); }
            catch (JsonException) { /* not JSON: treat as text */ }
            if (inner != null)
            {
                if (depth + 1 > MaxDepth) { changed = true; return Removed; }   // fail closed
                bool innerChanged;
                try { innerChanged = Clean(inner, depth + 1, keepProse); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                { changed = true; return Removed; }   // e.g. a duplicate property name: cannot be checked, so not exported
                if (!innerChanged) return s;
                changed = true;
                return inner.ToJsonString(Indented);
            }
        }
        var pattern = keepProse ? CredentialValue : SecretValue;
        try
        {
            if (!pattern.IsMatch(s)) return s;
            changed = true;
            return pattern.Replace(s, Removed);
        }
        catch (RegexMatchTimeoutException) { changed = true; return Removed; }
    }

    /// <summary>Serializes <paramref name="value"/> to JSON with secrets removed.</summary>
    /// <param name="keepProse">See <see cref="Clean(JsonNode?, int, bool)"/>.</param>
    public static string ToJson<T>(T value, bool indented = true, bool keepProse = false)
    {
        var node = JsonSerializer.SerializeToNode(value);
        Clean(node, keepProse: keepProse);
        return node?.ToJsonString(indented ? Indented : null) ?? "null";
    }

    /// <summary>A cleaned copy of a JSON element (account export from the service).</summary>
    /// <exception cref="InvalidDataException">The document cannot be checked (too deep, duplicate property names): it
    /// must not be exported at all.</exception>
    public static JsonElement Clean(JsonElement element)
    {
        try
        {
            var node = JsonNode.Parse(element.GetRawText(), documentOptions: ParseOptions);
            Clean(node);
            using var doc = JsonDocument.Parse(node?.ToJsonString() ?? "null", ParseOptions);
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException("The export could not be checked for secrets (" + ex.GetType().Name + ").", ex);
        }
    }
}
