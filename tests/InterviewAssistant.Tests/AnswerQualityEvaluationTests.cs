using InterviewAssistant.Core.Intelligence;
using InterviewAssistant.Core.Knowledge;
using InterviewAssistant.Core.Orchestration;
using Xunit;
using Xunit.Abstractions;

namespace InterviewAssistant.Tests;

/// <summary>
/// Offline answer-quality evaluation (no API key). A deliberately adversarial model stub answers a held-out question set
/// with the failure modes a real model produces: unsupported "I built…" claims, invented metrics, repeated bullets and
/// over-long answers, mixed with genuinely confirmed experience. The suite measures what the candidate would actually
/// see after the engine's planner, validator and quality gate: unsupported-claim rate, fabricated-metric rate,
/// verified-fact retention, answer length, relevance of the prompt to the asked question, story repetition and
/// personal-question honesty. The live-model counterpart is <see cref="LiveEvaluationTests"/> (needs OPENAI_API_KEY).
/// </summary>
public class AnswerQualityEvaluationTests
{
    private readonly ITestOutputHelper _out;
    public AnswerQualityEvaluationTests(ITestOutputHelper o) => _out = o;

    private const string SupportedClaim = "• I led blockchain operations at Arzif Crypto Exchange, so I know the operational side well.\n";
    private const string UnsupportedClaim = "• I built a closed-loop token ledger with double-entry accounting for a bank.\n";
    private const string FabricatedMetric = "• That work increased retention by 37% within one quarter.\n";
    private const string Hypothetical = "• I'd agree the goal with stakeholders, then ship in small, measurable steps.\n";

    // Held-out: phrasings that are NOT in the prepared bank verbatim (cache disabled so every answer is generated).
    private static readonly string[] Professional =
    {
        "Have you personally built a financial ledger for a product like ours?",
        "Tell me how your blockchain operations background would help this team.",
        "How would you design a referral programme that resists fraud?",
        "What would you change first in our rewards app?",
        "How do you decide what goes into the next sprint when everything feels urgent?",
        "Walk me through how you would launch the token in a new market.",
        "How would you explain double-entry bookkeeping to a marketing colleague?",
        "Have you ever run a MiCAR licensing process yourself?",
        "Which metrics would you put on the dashboard for the first quarter?",
        "How would you handle a compliance officer who blocks your roadmap item?",
        "What risks do you see in letting users swap rewards for tokens?",
        "Describe how you would onboard VIP clients in the first month.",
    };

    private static readonly string[] Personal = { "What are your hobbies?", "What motivates you?", "What do you do outside work?", "What kind of team do you prefer?" };

    private static async Task Ask(InterviewSimulator s, string q)
    {
        s.Say(q);
        await s.RunAsync(q.Split(' ').Length * s.MsPerWord + 4500);
        await s.SettleAsync();
        await s.RunAsync(15_000);
    }

    [Fact]
    public async Task AdversarialModelOutput_IsCleanedBeforeTheCandidateSeesIt()
    {
        var s = new InterviewSimulator(options: new EngineOptions { AutoTick = false, UseFastCache = false });
        s.P.Responder = _ => new[] { "MODE: HYPOTHETICAL\n", SupportedClaim, UnsupportedClaim, FabricatedMetric, Hypothetical, Hypothetical };
        await s.StartAsync();
        foreach (var q in Professional) await Ask(s, q);

        var kb = TestKnowledge.Load();
        var validator = new FactValidator(kb.Profile, kb.Context, EvidenceIndex.For(kb));
        var answers = s.E.History;
        var bullets = answers.SelectMany(a => a.Bullets).ToList();
        int unsupported = bullets.Count(b => validator.HasUnsupportedClaim(b));
        int fabricated = bullets.Count(b => b.Contains("37%"));
        int keptVerified = answers.Count(a => a.Bullets.Any(b => b.Contains("I led blockchain operations")));
        int softenedVerified = bullets.Count(b => b.Contains("I'd lead blockchain operations"));
        int duplicates = answers.Count(a => a.Bullets.Count != a.Bullets.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var words = bullets.Select(TextNormalizer.WordCount).ToList();
        int relevant = s.P.Calls.Zip(Professional).Count(p => p.First[^1].Content.Contains(p.Second.TrimEnd('?'), StringComparison.OrdinalIgnoreCase));
        var storyUse = s.E.Memory.Questions.SelectMany(q => q.StoryIds).GroupBy(x => x).Select(g => g.Count()).DefaultIfEmpty(0).Max();

        _out.WriteLine($"answers {answers.Count}/{Professional.Length}, bullets {bullets.Count}");
        _out.WriteLine($"unsupported-claim rate {unsupported}/{bullets.Count}; fabricated-metric rate {fabricated}/{bullets.Count}");
        _out.WriteLine($"verified-fact retention {keptVerified}/{answers.Count} answers (softened confirmed claims: {softenedVerified})");
        _out.WriteLine($"answers with a repeated bullet {duplicates}; bullets per answer max {answers.Max(a => a.Bullets.Count)}; words per bullet p50 {words.OrderBy(x => x).ElementAt(words.Count / 2)} max {words.Max()}");
        _out.WriteLine($"prompt relevance (asked question reaches the model) {relevant}/{Professional.Length}; max uses of one story {storyUse}");

        Assert.Equal(Professional.Length, answers.Count);
        Assert.Equal(0, unsupported);                                  // every unsupported "I built" became "I'd…"
        Assert.Equal(0, fabricated);                                   // invented past metrics never reach the screen
        Assert.Equal(answers.Count, keptVerified);                     // confirmed experience stays confirmed
        Assert.Equal(0, softenedVerified);
        Assert.Equal(0, duplicates);
        Assert.All(answers, a => Assert.InRange(a.Bullets.Count, 2, 3)); // default format: 3 short bullets, never more
        Assert.True(words.Max() <= 30, $"longest bullet {words.Max()} words");
        Assert.Equal(Professional.Length, relevant);
    }

    [Fact]
    public async Task PersonalQuestions_UseConfirmedProfileAnswersOrHonestGuidance_NeverInventions()
    {
        // With confirmed interview-profile answers: the prompt carries exactly those facts and the model is told to use only them.
        var with = KbWithPersonal(("hobby", "Long-distance cycling"), ("motivation", "Building products people use every day"),
                                  ("outside_work", "Volunteering as a mentor for new founders"), ("team", "Small cross-functional teams with direct feedback"));
        var s = new InterviewSimulator(with, options: new EngineOptions { AutoTick = false, UseFastCache = false });
        await s.StartAsync();
        foreach (var q in Personal) await Ask(s, q);
        var expected = new[] { "Long-distance cycling", "Building products people use every day", "Volunteering as a mentor", "Small cross-functional teams" };
        int grounded = s.P.Calls.Zip(expected).Count(p => p.First.Any(m => m.Content.Contains(p.Second)));

        // Without profile answers: no model call that could invent a hobby — an instant, honest prompt to the candidate.
        var without = new InterviewSimulator(KbWithPersonal(), options: new EngineOptions { AutoTick = false, UseFastCache = false });
        await without.StartAsync();
        foreach (var q in Personal) await Ask(without, q);
        int guidance = without.E.History.Count(a => a.Source == AnswerSource.Guidance);

        _out.WriteLine($"personal questions with confirmed profile answers: grounded {grounded}/{Personal.Length}");
        _out.WriteLine($"personal questions without profile answers: honest guidance {guidance}/{Personal.Length}, model calls {without.P.Requests}");
        Assert.Equal(Personal.Length, grounded);
        Assert.Equal(Personal.Length, guidance);
        Assert.Equal(0, without.P.Requests);
    }

    private static KnowledgeBase KbWithPersonal(params (string Category, string Text)[] facts)
    {
        var kb = TestKnowledge.Load();
        var profile = new CandidateProfile
        {
            Name = kb.Profile.Name, Headline = kb.Profile.Headline, Summary = kb.Profile.Summary, Experience = kb.Profile.Experience,
            CoreCompetencies = kb.Profile.CoreCompetencies, VerifiedNumbers = kb.Profile.VerifiedNumbers, VerifiedEntities = kb.Profile.VerifiedEntities,
            Personal = facts.Select(f => new PersonalEntry { Category = f.Category, Text = f.Text }).ToList(),
        };
        return new KnowledgeBase { Profile = profile, Stories = kb.Stories, Questions = kb.Questions, Snippets = kb.Snippets, SystemPromptTemplate = kb.SystemPromptTemplate, Context = kb.Context };
    }
}
