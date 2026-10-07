using InterviewAssistant.Core.Knowledge;
using InterviewAssistant.Core.Preparation;

namespace InterviewAssistant.Tests;

/// <summary>What the installer ships (knowledge/ next to the EXE) must be found, complete and free of any candidate's data.</summary>
public class ShippedAssetsTests
{
    private static string GenericDir => Path.Combine(AppContext.BaseDirectory, "generic-knowledge");

    [Fact]
    public void PublishedLayoutIsFoundWithoutAnySampleQuestionBank()
    {
        var app = Path.Combine(Path.GetTempPath(), "ia-pub-" + Guid.NewGuid().ToString("n"));
        CopyDir(GenericDir, Path.Combine(app, "knowledge"));
        try
        {
            Assert.False(File.Exists(Path.Combine(app, "knowledge", "question_bank.json")));   // no candidate pack is shipped
            var assets = PreparationAssets.LoadShipped(app);
            Assert.True(assets.Library.Count >= 50, $"library {assets.Library.Count}");
            Assert.NotEmpty(assets.Playbooks);
        }
        finally { Directory.Delete(app, true); }
    }

    [Fact]
    public void ShippedKnowledgeContainsNoSampleCandidateOrCompany()
    {
        var sample = KnowledgeBase.Load(Path.Combine(AppContext.BaseDirectory, "knowledge")).Context;
        var forbidden = new[] { sample.CandidateName, sample.CompanyName }
            .Concat(sample.CandidateName.Split(' ')).Where(t => t.Length >= 4).Distinct().ToList();
        Assert.NotEmpty(forbidden);
        foreach (var file in Directory.GetFiles(GenericDir, "*", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (var term in forbidden)
                Assert.False(text.Contains(term, StringComparison.OrdinalIgnoreCase), $"{Path.GetFileName(file)} mentions '{term}'");
        }
    }

    private static void CopyDir(string from, string to)
    {
        foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(d.Replace(from, to));
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) File.Copy(f, f.Replace(from, to), true);
    }
}
