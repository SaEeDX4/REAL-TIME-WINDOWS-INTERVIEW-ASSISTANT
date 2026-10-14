using System.Reflection;

namespace InterviewAssistant.App.Services;

/// <summary>
/// Product version, release channel and build commit from the assembly's informational version
/// ("2.1.0-rc.1+abc1234…", set by CI with -p:Version and -p:SourceRevisionId).
/// </summary>
public static class BuildInfo
{
    private static readonly string Informational =
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>Semantic version without build metadata, e.g. "2.1.0-rc.1".</summary>
    public static string Version => Informational.Split('+')[0];

    /// <summary>Short commit id, or "local" for a developer build without CI metadata.</summary>
    public static string Commit
    {
        get
        {
            var plus = Informational.IndexOf('+');
            var meta = plus >= 0 ? Informational[(plus + 1)..] : "";
            return meta.Length >= 7 ? meta[..7] : meta.Length > 0 ? meta : "local";
        }
    }

    /// <summary>"Release Candidate", "Beta" or "Stable" — a release candidate is never presented as final.</summary>
    public static string Channel => Version.Contains("-rc", StringComparison.OrdinalIgnoreCase) ? "Release Candidate"
        : Version.Contains("-beta", StringComparison.OrdinalIgnoreCase) || Version.Contains("-alpha", StringComparison.OrdinalIgnoreCase) ? "Beta"
        : Version.Contains('-') ? "Pre-release" : "Stable";

    public static string Summary => $"Version {Version} · {Channel} · Build {Commit}";
}
