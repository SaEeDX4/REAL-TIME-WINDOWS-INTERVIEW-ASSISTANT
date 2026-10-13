namespace InterviewAssistant.Client;

/// <summary>
/// Public, non-secret cloud settings. Only <see cref="ApiBaseUrl"/> ships with the app; the Supabase URL and publishable
/// key come from the service's public /api/v1/config (cached locally). No provider secret is ever part of the desktop.
/// </summary>
public sealed class CloudOptions
{
    public string ApiBaseUrl { get; set; } = "";
    public string SupabaseUrl { get; set; } = "";
    public string SupabaseAnonKey { get; set; } = "";
    /// <summary>OAuth providers enabled in the Supabase project (e.g. "google"); empty = e-mail sign-in only.</summary>
    public string[] OAuthProviders { get; set; } = Array.Empty<string>();
    public bool EmailPassword { get; set; } = true;
    /// <summary>Page the sign-up confirmation e-mail returns to (served by the API).</summary>
    public string? EmailConfirmedRedirectUrl { get; set; }
    public string ClientVersion { get; set; } = "2.0.0";

    /// <summary>Sign-in can be attempted (Supabase endpoint + publishable key known).</summary>
    public bool AuthReady =>
        Uri.TryCreate(SupabaseUrl, UriKind.Absolute, out var s) && (s.Scheme == "https" || s.IsLoopback) && !string.IsNullOrWhiteSpace(SupabaseAnonKey);
    /// <summary>Velopack update feed (https URL of a static release folder or a GitHub repository URL). Empty = updates disabled.</summary>
    public string UpdateUrl { get; set; } = "";

    /// <summary>The service is configured for this build (https API endpoint, or loopback for local development).</summary>
    public bool IsConfigured => Uri.TryCreate(ApiBaseUrl, UriKind.Absolute, out var a) && (a.Scheme == "https" || a.IsLoopback);
}
