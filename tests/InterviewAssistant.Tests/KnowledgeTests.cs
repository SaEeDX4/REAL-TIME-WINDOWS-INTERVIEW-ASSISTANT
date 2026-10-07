using System.Text.Json;
using InterviewAssistant.Core.Intelligence;
using InterviewAssistant.Core.Knowledge;
using Xunit;
using Xunit.Abstractions;

namespace InterviewAssistant.Tests;

public class KnowledgeTests
{
    private readonly ITestOutputHelper _out;
    public KnowledgeTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void AllRequiredKnowledgeFilesExist()
    {
        foreach (var f in new[] { "candidate_profile.json", "candidate_stories.json", "role_brief.md", "company_brief.md", "product_brief.md",
                                  "whitepaper_notes.md", "product_owner_playbook.md", "crypto_fintech_playbook.md", "question_bank.json",
                                  "answer_policy.md", "target_context.json", "test_questions.json" })
            Assert.True(File.Exists(Path.Combine(TestKnowledge.Dir, f)), f);
    }

    [Fact]
    public void LoadsProfileWithAllFiveEmployers()
    {
        var kb = TestKnowledge.Load();
        Assert.Equal("Shervin Fallahdoust", kb.Profile.Name);
        Assert.Equal(new[] { "Gabrielyte UAB", "Arzif Crypto Exchange", "LG Electronics", "Philips", "Epson" }, kb.Profile.Experience.Select(e => e.Company));
        Assert.Contains(kb.Profile.Experience[1].Achievements, a => a.Contains("60,000 active traders"));
        Assert.True(kb.Snippets.Count > 40);
    }

    [Fact]
    public void QuestionBankHasAtLeast80UniqueQuestionsWithAllFields()
    {
        var kb = TestKnowledge.Load();
        Assert.True(kb.Questions.Count >= 80, $"only {kb.Questions.Count}");
        Assert.Equal(kb.Questions.Count, kb.Questions.Select(q => q.QuestionId).Distinct().Count());
        foreach (var q in kb.Questions)
        {
            Assert.False(string.IsNullOrWhiteSpace(q.CanonicalQuestion));
            Assert.NotEmpty(q.Keywords);
            Assert.NotEmpty(q.SemanticVariants);
            Assert.False(string.IsNullOrWhiteSpace(q.OptionalFullAnswer));
            foreach (var id in q.CandidateEvidence) Assert.Contains(kb.Stories, s => s.Id == id);
        }
    }

    /// <summary>Automated answer-quality evaluation of every prepared answer (format + factuality).</summary>
    [Fact]
    public void EveryPreparedAnswerPassesQualityAndFactChecks()
    {
        var kb = TestKnowledge.Load();
        var v = new FactValidator(kb.Profile);
        var failures = new List<string>();
        int totalWords = 0;
        foreach (var q in kb.Questions)
        {
            var r = v.Validate(q.ShortBullets, q.Mode);
            totalWords += r.WordCount;
            foreach (var i in r.Issues) failures.Add($"{q.QuestionId} {i.Kind} {i.Detail} :: {(i.BulletIndex >= 0 ? q.ShortBullets[i.BulletIndex] : "")}");
            if (q.ShortBullets.Count is < 3 or > 4) failures.Add($"{q.QuestionId} has {q.ShortBullets.Count} bullets");
            if (q.Mode != AnswerMode.Verified)
                foreach (var b in q.ShortBullets)
                    if (b.Contains("I'd") == false && b.Contains("I would") == false && b.StartsWith("I ")) { /* allowed: present-tense statements */ }
            // Presentation-style labels (no verb) are not speakable.
            foreach (var b in q.ShortBullets) if (TextNormalizer.WordCount(b) < 7) failures.Add($"{q.QuestionId} label-like bullet: {b}");
        }
        _out.WriteLine($"Average words per answer: {totalWords / (double)kb.Questions.Count:0.0}");
        foreach (var f in failures) _out.WriteLine(f);
        Assert.Empty(failures);
    }

    [Fact]
    public void BridgeAnswersStartWithHonestBridge()
    {
        var kb = TestKnowledge.Load();
        foreach (var q in kb.Questions.Where(q => q.Mode == AnswerMode.Bridge))
            Assert.Matches(@"(?i)(haven't|wasn't|correct)", q.ShortBullets[0]);
    }

    [Fact]
    public void HypotheticalAnswersDoNotClaimFabricatedHistory()
    {
        var kb = TestKnowledge.Load();
        foreach (var q in kb.Questions.Where(q => q.Mode == AnswerMode.Hypothetical))
            foreach (var b in q.ShortBullets)
                Assert.DoesNotMatch(@"\bI (built|implemented|owned|designed) (a|the|an) (ledger|wallet|reward|token)", b);
    }

    [Fact]
    public void TestQuestionFileHasAtLeast80Utterances()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestKnowledge.Dir, "test_questions.json")));
        Assert.True(doc.RootElement.GetProperty("tests").GetArrayLength() >= 80);
    }
}
