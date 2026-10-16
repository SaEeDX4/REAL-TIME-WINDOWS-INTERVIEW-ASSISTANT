using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace InterviewAssistant.Tests;

/// <summary>
/// Commercial redistribution: every shipped dependency is listed in THIRD_PARTY_NOTICES.md, every license file the index
/// references exists with real content, and the release build copies the notices and the licenses\ folder into the package.
/// </summary>
public class LicenseComplianceTests
{
    private static readonly Regex LicenseRef = new(@"licenses/([A-Za-z0-9._-]+\.txt)");

    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "InterviewAssistant.sln"))) dir = dir.Parent;
        return dir?.FullName;
    }

    private static string Notices(string root) => File.ReadAllText(Path.Combine(root, "THIRD_PARTY_NOTICES.md"));

    [Fact]
    public void EveryLicenseFileReferencedByTheNoticesExistsAndIsSubstantive()
    {
        var root = RepoRoot();
        if (root == null) return; // not running from a source checkout
        var referenced = LicenseRef.Matches(Notices(root)).Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.True(referenced.Count >= 8, $"only {referenced.Count} license files referenced");
        foreach (var file in referenced)
        {
            var path = Path.Combine(root, "licenses", file);
            Assert.True(File.Exists(path), $"THIRD_PARTY_NOTICES.md references missing licenses/{file}");
            Assert.True(File.ReadAllText(path).Trim().Length > 200, $"licenses/{file} is too short to be a license text");
        }
    }

    [Fact]
    public void EveryFileInTheLicensesFolderIsIndexed()
    {
        var root = RepoRoot();
        if (root == null) return;
        var notices = Notices(root);
        foreach (var file in Directory.GetFiles(Path.Combine(root, "licenses")))
            Assert.True(notices.Contains("licenses/" + Path.GetFileName(file), StringComparison.Ordinal),
                $"licenses/{Path.GetFileName(file)} is not listed in THIRD_PARTY_NOTICES.md");
    }

    [Fact]
    public void RequiredRuntimeAndPackageLicensesArePresent()
    {
        var root = RepoRoot();
        if (root == null) return;
        var notices = Notices(root);
        foreach (var file in new[] { "dotnet-runtime-LICENSE.txt", "dotnet-runtime-THIRD-PARTY-NOTICES.txt",
                                     "windowsdesktop-runtime-LICENSE.txt", "windowsdesktop-runtime-THIRD-PARTY-NOTICES.txt",
                                     "NAudio.txt", "PdfPig.txt", "Velopack.txt", "Velopack-native-THIRD-PARTY-NOTICES.txt" })
            Assert.Contains("licenses/" + file, notices);
        var pdfPig = File.ReadAllText(Path.Combine(root, "licenses", "PdfPig.txt"));
        Assert.Contains("Apache License", pdfPig);
        Assert.Contains("AFM files", pdfPig);   // embedded Adobe font metrics terms travel with the package
        Assert.Contains("Permission is hereby granted",
            File.ReadAllText(Path.Combine(root, "licenses", "dotnet-runtime-LICENSE.txt")));
    }

    [Fact]
    public void EveryShippedPackageReferenceIsListedInTheNotices()
    {
        var root = RepoRoot();
        if (root == null) return;
        var notices = Notices(root);
        var projects = new[] { "InterviewAssistant.App", "InterviewAssistant.Core", "InterviewAssistant.Client" }
            .SelectMany(p => Directory.GetFiles(Path.Combine(root, "src", p), "*.csproj"))
            .ToList();
        Assert.Equal(3, projects.Count);
        var packages = projects
            .SelectMany(p => XDocument.Load(p).Descendants("PackageReference"))
            .Select(e => (string?)e.Attribute("Include"))
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct()
            .ToList();
        Assert.NotEmpty(packages);
        foreach (var package in packages)
            Assert.True(notices.Contains(package!, StringComparison.Ordinal), $"PackageReference {package} is not listed in THIRD_PARTY_NOTICES.md");
    }

    [Fact]
    public void ReleaseBuildShipsTheNoticesAndTheLicensesFolder()
    {
        var root = RepoRoot();
        if (root == null) return;
        var script = File.ReadAllText(Path.Combine(root, "scripts", "build-release.ps1"));
        Assert.Contains("Copy-Item $notices -Destination $out", script);
        Assert.Contains("Copy-Item $licenses -Destination (Join-Path $out \"licenses\") -Recurse", script);
        // Missing notices or license texts fail the build instead of being skipped silently.
        var licenseLines = script.Split('\n').Where(l => l.Contains("licenses", StringComparison.OrdinalIgnoreCase) || l.Contains("$notices"));
        Assert.DoesNotContain(licenseLines, l => l.Contains("SilentlyContinue", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("throw \"licenses\\ folder is missing", script);
        Assert.Contains("throw \"THIRD_PARTY_NOTICES.md is missing", script);
    }
}
