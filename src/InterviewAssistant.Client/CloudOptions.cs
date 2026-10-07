namespace InterviewAssistant.Client;

/// <summary>
/// Public, non-secret cloud endpoints. The Supabase anon key is a publishable key by design (row access is enforced
/// server-side); no provider secret is ever part of the desktop configuration.
/// </summary>
public sealed class CloudOptions
{
    public string ApiBaseUrl { get; set; } = "";
    public string SupabaseUrl { get; set; } = "";
    public string SupabaseAnonKey { get; set; } = "";
    /// <summary>OAuth providers enabled in the Supabase project (e.g. "google", "azure", "github").</summary>
    public string[] OAuthProviders { get; set; } = { "google" };
    public string ClientVersion { get; set; } = "2.0.0";
    /// <summary>Velopack update feed (https URL of a static release folder or a GitHub repository URL). Empty = updates disabled.</summary>
    public string UpdateUrl { get; set; } = "";

    public bool IsConfigured =>
        Uri.TryCreate(ApiBaseUrl, UriKind.Absolute, out var a) && (a.Scheme == "https" || a.IsLoopback) &&
        Uri.TryCreate(SupabaseUrl, UriKind.Absolute, out var s) && (s.Scheme == "https" || s.IsLoopback) &&
        !string.IsNullOrWhiteSpace(SupabaseAnonKey);
}
