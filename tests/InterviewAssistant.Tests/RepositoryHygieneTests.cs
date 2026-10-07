using System.Text.RegularExpressions;
using Xunit;

namespace InterviewAssistant.Tests;

/// <summary>Security review automation: fails if anything resembling an API key is committed.</summary>
public class RepositoryHygieneTests
{
    private const string FakeMarker = "fake-credential: test fixture";

    [Fact]
    public void NoSecretsInRepository()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "InterviewAssistant.sln"))) dir = dir.Parent;
        if (dir == null) return; // not running from a source checkout
        var secret = new Regex(@"sk-(proj-)?[A-Za-z0-9_\-]{20,}|(api[_-]?key|secret)\s*[:=]\s*[""']?[A-Za-z0-9_\-]{16,}", RegexOptions.IgnoreCase);
        var offenders = Directory.EnumerateFiles(dir.FullName, "*", SearchOption.AllDirectories)
            .Where(f => !Regex.IsMatch(f, @"[\\/](bin|obj|\.git|artifacts)[\\/]"))
            .Where(f => new FileInfo(f).Length < 2_000_000)
            // Deliberately fake values in tests are allowed only on lines explicitly marked as such.
            .Where(f => File.ReadLines(f).Any(l => secret.IsMatch(l) && !l.Contains(FakeMarker)))
            .ToList();
        Assert.Empty(offenders);
    }
}
