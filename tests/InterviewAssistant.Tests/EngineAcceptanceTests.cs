using InterviewAssistant.Core.Intelligence;
using InterviewAssistant.Core.Knowledge;
using InterviewAssistant.Core.Orchestration;
using InterviewAssistant.Core.Providers;
using Xunit;
using Xunit.Abstractions;

namespace InterviewAssistant.Tests;

/// <summary>
/// Release-candidate acceptance tests (A–I) at engine level. Service-side parts of B/G/H (no lease for typed questions,
/// no billing while paused, new lease on resume, context on reconnect) are in the backend LiveContextAndManualAnswerTests.
/// </summary>
public class EngineAcceptanceTests
{
    private readonly ITestOutputHelper _out;
    public EngineAcceptanceTests(ITestOutputHelper o) => _out = o;

    private static readonly System.Text.RegularExpressions.Regex PastOwnership = new(
        @"\bI(?:'ve|\s+have)?\s+(built|implemented|led|owned|designed|launched|created|developed|ran)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static async Task<InterviewSimulator> Sim(EngineOptions? o = null)
    {
        var s = new InterviewSimulator(options: o);
        await s.StartAsync();
        return s;
    }

    /// <summary>A: one correct question, one 3-bullet answer, no duplicate.</summary>
    [Fact]
    public async Task A_CryptoBackgroundQuestionFromAudio()
    {
        var s = await Sim();
        s.Say("Can you tell me about your experience in cryptocurrency and how it prepared you for this Product Owner role?");
        await s.RunAsync(9000); await s.SettleAsync();
        var a = Assert.Single(s.Visible);
        Assert.Single(s.E.History);
        Assert.Equal("Can you tell me about your experience in cryptocurrency and how it prepared you for this Product Owner role?", a.Question);
        Assert.Equal(3, a.SnapshotBullets().Count);
        Assert.Equal(0, s.E.Metrics.DuplicatesSuppressed);
        _out.WriteLine($"{a.Source} {a.Mode}: " + string.Join(" | ", a.SnapshotBullets()));
    }

    private const string QuestionB = "Imagine XAB suddenly has strong token distribution but very low real utility and users mostly sell the token immediately. What would you prioritize in your first 30 days as Product Owner, and how would you measure whether your changes are working?";

    /// <summary>B (engine): a novel typed question while paused is answered by AI, in hypothetical language.</summary>
    [Fact]
    public async Task B_NovelTypedQuestionWhilePausedUsesAiWithHypotheticalLanguage()
    {
        var s = await Sim();
        s.P.Responder = _ => new[] { "MODE: HYPOTHETICAL\n", "• I'd first find out why holders sell, by cohort and by reward source.\n",
            "• Then I'd add real spending utility, like fee discounts and VIP benefits, before more distribution.\n", "• I'd measure 30-day holding rate, utility usage per holder and net sell pressure.\n" };
        s.E.Pause();
        Assert.Equal(EngineStatus.Paused, s.E.Status);
        await s.E.SubmitManualQuestionAsync(QuestionB);
        var a = s.E.Current!;
        Assert.True(a.IsComplete);
        Assert.Equal(AnswerSource.Llm, a.Source);
        Assert.Equal(1, s.P.Requests);
        Assert.Equal(3, a.SnapshotBullets().Count);
        Assert.All(a.SnapshotBullets(), b => Assert.DoesNotMatch(PastOwnership, b));
        Assert.StartsWith("I'd", a.SnapshotBullets()[0]);
        Assert.Equal(AnswerMode.Hypothetical, a.Mode);
        Assert.Equal(EngineStatus.Paused, s.E.Status);              // still paused (no live session was started)
        Assert.False(s.E.IsListening);
    }

    private const string QuestionE = "Have you personally built a closed-loop internal token ledger?";

    /// <summary>E: direct honesty — a fabricated "yes" from the model is never shown as history.</summary>
    [Fact]
    public async Task E_DirectExperienceQuestionNeverClaimsUnsupportedHistory()
    {
        var s = await Sim(new EngineOptions { AutoTick = false, UseFastCache = false });   // force the AI path
        s.P.Responder = _ => new[] { "MODE: VERIFIED\n", "• I built a closed-loop internal token ledger at Teroxx in 2021.\n",
            "• I led the reconciliation design with Engineering and Finance.\n", "• It reduced settlement errors by 40% within six months.\n" };
        await s.E.SubmitManualQuestionAsync(QuestionE);
        var a = s.E.Current!;
        var bullets = a.SnapshotBullets();
        _out.WriteLine(string.Join("\n", bullets));
        Assert.Equal(AnswerMode.Bridge, a.Mode);
        Assert.Equal(InterviewEngine.HonestBridge, bullets[0]);       // honest bridge first, always
        Assert.All(bullets, b => Assert.DoesNotMatch(PastOwnership, b));
        Assert.DoesNotContain(bullets, b => b.Contains("40%") || b.Contains("2021"));
        var prompt = s.P.Calls.Single()[^1].Content;
        Assert.Contains("REQUIRED MODE: BRIDGE", prompt);
    }

    [Fact]
    public async Task E_WellBehavedBridgeAnswerIsShownAsIs()
    {
        var s = await Sim(new EngineOptions { AutoTick = false, UseFastCache = false });
        var model = new[] { "I haven't owned a closed-loop token ledger end-to-end, but my exchange operations work is closely related.",
            "I'd start with double-entry accounts, idempotent transaction states and daily reconciliation.",
            "I'd measure success by zero unexplained balance differences and fast, audited corrections." };
        s.P.Responder = _ => new[] { "MODE: BRIDGE\n" }.Concat(model.Select(m => "• " + m + "\n")).ToArray();
        await s.E.SubmitManualQuestionAsync(QuestionE);
        Assert.Equal(model, s.E.Current!.SnapshotBullets());
    }

    [Fact]
    public async Task E_PreparedAnswerPathIsHonestToo()
    {
        var s = await Sim();
        await s.E.SubmitManualQuestionAsync(QuestionE);
        var a = s.E.Current!;
        Assert.Equal(AnswerSource.Cache, a.Source);
        Assert.Matches(@"^I haven't", a.SnapshotBullets()[0]);
        Assert.All(a.SnapshotBullets(), b => Assert.DoesNotMatch(PastOwnership, b));
        Assert.Equal(0, s.P.Requests);
    }

    /// <summary>F: a hypothetical design question gets a confident expert answer — no unnecessary disclaimer.</summary>
    [Fact]
    public async Task F_HypotheticalDesignQuestionIsConfidentWithoutDisclaimer()
    {
        var s = await Sim(new EngineOptions { AutoTick = false, UseFastCache = false });
        s.P.Responder = _ => new[] { "MODE: HYPOTHETICAL\n", "• I'd design it as a double-entry ledger where every token movement has a matching debit and credit.\n",
            "• I'd make every transaction idempotent with clear states, so retries never double-credit.\n", "• I'd reconcile daily against supply and treasury, with alerts on any mismatch.\n" };
        await s.E.SubmitManualQuestionAsync("How would you design a closed-loop internal token ledger?");
        var a = s.E.Current!;
        Assert.Equal(AnswerMode.Hypothetical, a.Mode);
        Assert.All(a.SnapshotBullets(), b => Assert.StartsWith("I'd", b));
        Assert.DoesNotContain(a.SnapshotBullets(), b => b.Contains("haven't", StringComparison.OrdinalIgnoreCase));
        var prompt = s.P.Calls.Single()[^1].Content;
        Assert.DoesNotContain("REQUIRED MODE: BRIDGE", prompt);
        Assert.Contains("SUGGESTED MODE: HYPOTHETICAL", prompt);
    }

    /// <summary>The direct-experience decision is made from the user's confirmed evidence, not a fixed topic list.</summary>
    [Fact]
    public void DirectExperienceIsVerifiedOnlyWhenEvidenceSupportsIt()
    {
        var kb = TestKnowledge.Load();
        var ev = EvidenceIndex.For(kb);
        Assert.Equal(AnswerMode.Bridge, QuestionClassifier.Classify(QuestionE, kb.Context, ev).Mode);
        Assert.Equal(AnswerMode.Verified, QuestionClassifier.Classify("Have you worked with Jira?", kb.Context, ev).Mode);          // confirmed skill
        Assert.Equal(AnswerMode.Verified, QuestionClassifier.Classify("Did you grow Arzif's active traders?", kb.Context, ev).Mode);
        Assert.Equal(AnswerMode.Hypothetical, QuestionClassifier.Classify("How would you design a closed-loop internal token ledger?", kb.Context, ev).Mode);
        // A user with no confirmed history is never "verified".
        var empty = new EvidenceIndex(new InterviewAssistant.Core.Knowledge.CandidateProfile(), Array.Empty<InterviewAssistant.Core.Knowledge.CandidateStory>());
        Assert.Equal(AnswerMode.Bridge, QuestionClassifier.Classify("Have you worked with Jira?", kb.Context, empty).Mode);
    }

    /// <summary>Prompt for one user contains only that user's confirmed facts (no other candidate's education/languages).</summary>
    [Fact]
    public void PromptContainsOnlyThisUsersConfirmedFacts()
    {
        var other = new InterviewAssistant.Core.Knowledge.KnowledgeBase
        {
            Profile = new InterviewAssistant.Core.Knowledge.CandidateProfile { Name = "Jane Example", Headline = "Product Manager" },
            SystemPromptTemplate = InterviewAssistant.Core.Knowledge.PromptTemplates.LiveAnswer,
            Questions = Array.Empty<BankQuestion>(), Stories = Array.Empty<CandidateStory>(), Snippets = Array.Empty<KnowledgeSnippet>(),
        };
        var msgs = new PromptBuilder(other).Build("Tell me about your education.", QuestionClassifier.Classify("Tell me about your education."),
            new RetrievedContext(Array.Empty<InterviewAssistant.Core.Knowledge.CandidateStory>(), Array.Empty<InterviewAssistant.Core.Knowledge.KnowledgeSnippet>(), null), AnswerStyle.Balanced, Array.Empty<ConversationTurn>());
        var all = string.Join("\n", msgs.Select(m => m.Content));
        Assert.DoesNotContain("Tehran", all);
        Assert.DoesNotContain("MBA", all);
        Assert.DoesNotContain("Lithuanian", all);
        Assert.Contains("No confirmed work history", all);
    }

    /// <summary>H: pause, then resume — same interview context, no stale text, no duplicate turn.</summary>
    [Fact]
    public async Task H_ResumeAfterPauseContinuesCleanly()
    {
        var s = await Sim();
        s.Say("How do you prioritize the backlog?");
        await s.RunAsync(6000); await s.SettleAsync();
        s.E.Pause();
        // Interviewer audio during the pause is ignored (not listening, nothing billed).
        s.T.Start(); s.T.Partial("x1", "this should be ignored"); s.T.Stop(); s.T.Complete("This should be ignored while paused?", "x1");
        await s.RunAsync(3000);
        Assert.Single(s.E.History);
        await s.E.StartListeningAsync();
        Assert.Equal("", s.E.Turn.CurrentText());                        // no stale partial text
        s.Say("How do you balance technical debt against new features?");
        await s.RunAsync(7000); await s.SettleAsync();
        Assert.Equal(2, s.E.History.Count);
        Assert.Equal(2, s.Visible.Count);
        Assert.Equal(s.E.Knowledge.Context.CompanyName, TestKnowledge.Load().Context.CompanyName);   // same interview pack
        Assert.Equal(0, s.E.Metrics.DuplicatesSuppressed);
    }

    /// <summary>I: backend AI failure — safe prepared answer when available, otherwise an actionable message (never "Unknown").</summary>
    [Fact]
    public async Task I_AiFailureUsesSafePreparedAnswerOrActionableMessage()
    {
        var s = await Sim(new EngineOptions { AutoTick = false, UseFastCache = false, DeveloperKeyMode = false });
        s.P.Throw = new ProviderException(ProviderErrorKind.ServiceUnavailable, "503") { Code = "ai_unavailable" };

        await s.E.SubmitManualQuestionAsync("How would you increase XAB adoption?");   // prepared answer exists
        var known = s.E.Current!;
        Assert.Equal(AnswerSource.CacheFallback, known.Source);
        Assert.Equal(3, known.SnapshotBullets().Count);
        Assert.StartsWith("Prepared answer — ", known.Note);
        Assert.DoesNotContain("Unknown", known.Note);

        await s.E.SubmitManualQuestionAsync("How would you design a quantum-safe custody roadmap for 2030?");   // nothing prepared
        var novel = s.E.Current!;
        Assert.Equal(AnswerSource.Error, novel.Source);
        Assert.Equal(ProviderErrorKind.ServiceUnavailable, novel.Error);
        Assert.Equal("Service temporarily unavailable. Prepared answers still work.", novel.Note);
    }

    [Theory]
    [InlineData(ProviderErrorKind.SessionEnded, "Your live session has ended. Press Resume.")]
    [InlineData(ProviderErrorKind.QuotaExceeded, "Daily answer limit reached. Prepared answers still work.")]
    [InlineData(ProviderErrorKind.NotSignedIn, "Please sign in again (Home → Account).")]
    [InlineData(ProviderErrorKind.DeviceRevoked, "This device is no longer registered. Sign in again in Home → Account.")]
    [InlineData(ProviderErrorKind.PlanRequired, "AI answers need an active plan. Upgrade in Home → Account. Prepared answers still work.")]
    public async Task KnownServiceErrorsShowSpecificText(ProviderErrorKind kind, string expected)
    {
        var s = await Sim(new EngineOptions { AutoTick = false, UseFastCache = false, DeveloperKeyMode = false });
        s.P.Throw = new ProviderException(kind, "x");
        await s.E.SubmitManualQuestionAsync("How would you design a quantum-safe custody roadmap for 2030?");
        Assert.Equal(expected, s.E.Current!.Note);
    }

    /// <summary>D (client side): the interview's names reach the transcription context; aliases fix known misrecognitions.</summary>
    [Fact]
    public void D_InterviewTermsAreSentForTranscriptionAndAliasesNormalizeText()
    {
        var kb = TestKnowledge.Load();
        var terms = kb.Context.TranscriptionTerms();
        foreach (var t in new[] { "Teroxx", "XAB", "Abloxx" }) Assert.Contains(t, terms);
        Assert.True(terms.Count <= 80);
        Assert.All(terms, t => Assert.True(t.Length <= 48));
        var cleaned = TextNormalizer.CleanTranscript("how would you improve terox's ab locks token", kb.Context.Aliases);
        Assert.Contains("Teroxx", cleaned);
        Assert.Contains("Abloxx", cleaned);
    }
}
