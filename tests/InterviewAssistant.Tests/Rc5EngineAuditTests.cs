using InterviewAssistant.Core.Audio;
using InterviewAssistant.Core.Intelligence;
using InterviewAssistant.Core.Turn;
using Xunit;
using Xunit.Abstractions;

namespace InterviewAssistant.Tests;

/// <summary>
/// Regression tests for the independent engine audit of the rc.5 follow-up and non-speech changes (findings 2–9; finding 1,
/// short held tones, is covered in <see cref="Rc5FollowUpTests"/>). Each test reproduces an auditor's case.
/// </summary>
public class Rc5EngineAuditTests
{
    private readonly ITestOutputHelper _out;
    public Rc5EngineAuditTests(ITestOutputHelper o) => _out = o;

    private static UtteranceGate Gate() => new(new PromptEchoDetector(TestKnowledge.Load().Context.BuildTranscriptionPrompt()), new[] { "Shervin", "Fallahdoust" });

    // ---------------- finding 2: one- and two-word follow-ups over the voice look-alikes ----------------

    public static readonly string[] OneWordFollowUps =
    {
        "Continue.", "More.", "Expand.", "Clarify.", "Details.", "Illustrate.", "Unpack.", "Go on.", "Keep going.", "Be specific.",
        "Say more.", "Elaborate.", "Explain.", "Example.", "Go deeper.",
    };

    public static IEnumerable<object[]> Lookalikes() => RecordedStyleAudioTests.KnownVoiceLookalikes.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(Lookalikes))]
    public async Task LookalikeFixtures_OneWordFollowUps_CreateNothing(string file)
    {
        foreach (var text in OneWordFollowUps)
        {
            var s = await Rc5FollowUpTests.PlayPcmAsync(AudioFixtures.Pcm(AudioFixtures.Get(file)), text);
            var what = $"{file} heard as '{text}'";
            Assert.True(s.E.Memory.Questions.Count == 0, what + ": a question was recorded");
            Assert.True(s.E.History.Count == 0, what + ": a history item was created");
            Assert.True(s.P.Requests == 0, what + ": an AI request was made");
            // A follow-up's words are never shown over these sounds. ("More." is no follow-up any more: like any other
            // non-question over a voice-like sound it may show as live text and is then dropped — rc.4 behaviour.)
            if (FollowUpIntent.Classify(text) != FollowUpKind.None)
                Assert.All(s.LiveTexts, t => Assert.True(t.Length == 0, $"{what}: live text '{t}'"));
        }
    }

    [Fact]
    public void BareMore_IsNotAFollowUp()
    {
        Assert.Equal(FollowUpKind.None, FollowUpIntent.Classify("More."));
        Assert.True(TurnDetector.IsNonQuestion("More."));
        Assert.False(TurnDetector.IsNonQuestion("More?"));          // asked, it stays a question (rc.4 behaviour)
        Assert.Equal(FollowUpKind.Imperative, FollowUpIntent.Classify("More detail."));
    }

    [Theory]
    [InlineData("speech_fu_goon__calln.wav", "Go on.")]
    [InlineData("speech_fu_continue__callq.wav", "Continue.")]
    [InlineData("speech_fu_elaborate__calln.wav", "Elaborate.")]
    public void RealOneWordFollowUps_AreClearlySpeech(string file, string text)
    {
        // The very-short rule (Strong support, a sustained voiced stretch or judged pitch, ≤ 350 ms voiced per syllable)
        // passes every real one- or two-word follow-up fixture; the look-alikes above fail it.
        var e = RecordedStyleAudioTests.Evidence(AudioFixtures.Pcm(AudioFixtures.Get(file))) with { WindowMs = 0 };
        _out.WriteLine(e.ToString());
        Assert.True(FollowUpIntent.IsVeryShortImperative(text));
        Assert.True(e.SustainedVoicing || e.PitchJudged, e.ToString());
        Assert.Equal(UtteranceKind.FollowUp, Gate().Evaluate(text, e).Kind);
    }

    [Fact]
    public void KnockAndSungMelody_FailTheVeryShortRule()
    {
        var knock = RecordedStyleAudioTests.Evidence(AudioFixtures.Pcm(AudioFixtures.Get("knock_door__callq.wav")));
        var sung = RecordedStyleAudioTests.Evidence(AudioFixtures.Pcm(AudioFixtures.Get("music_sung__callq.wav")));
        Assert.True(knock.VoiceLike && sung.VoiceLike);   // still voice-like for longer transcripts: the documented residual
        Assert.False(knock.SustainedVoicing || knock.PitchJudged, knock.ToString());
        Assert.True(sung.VoicedMs > 4 * UtteranceGate.MaxVoicedMsPerShortSyllable, sung.ToString());
        Assert.False(Gate().Evaluate("Continue.", knock).Answerable);
        Assert.False(Gate().Evaluate("Elaborate.", sung).Answerable);
    }

    // ---------------- finding 3: a repeated real follow-up after another answer ----------------

    [Theory]
    [InlineData("Tell me more.")]
    [InlineData("Give me an example.")]
    [InlineData("Why?")]
    public async Task RepeatedFollowUp_AfterItsAnswer_IsAnsweredAgain(string followUp)
    {
        var s = await Rc2EngineAcceptanceTests.StartAsync();
        s.Say("What did you build at Teroxx?");
        await s.RunAsync(8000); await s.SettleAsync();
        s.Say(followUp);
        await s.RunAsync(10_000); await s.SettleAsync();
        s.Say(followUp);
        await s.RunAsync(8000); await s.SettleAsync();
        Assert.Equal(3, s.E.Memory.Questions.Count);
        Assert.Equal(3, s.E.History.Count);
        Assert.Equal(0, s.E.Metrics.DuplicatesSuppressed);
    }

    [Fact]
    public void DuplicateGuard_ScopesFollowUpsByTheAnsweredQuestion()
    {
        var g = new DuplicateGuard();
        g.Register("Tell me more.", 1000, scope: 1);
        Assert.True(g.IsDuplicate("Tell me more.", 4000, scope: 1));     // the same transcript echoed before anything new was answered
        Assert.False(g.IsDuplicate("Tell me more.", 12_000, scope: 2));  // asked again after the first follow-up was answered
        g.Register("How would you increase XAB adoption?", 20_000);
        Assert.True(g.IsDuplicate("So how would you increase XAB adoption?", 21_000));   // unscoped questions: unchanged
    }

    // ---------------- finding 4: hand-off to a colleague, then a bare follow-up ----------------

    [Theory]
    [InlineData("Over to you, Maria. Go on.")]
    [InlineData("John, do you want to take this? Go on.")]
    [InlineData("I want to hear from Sarah. Sarah, go on.")]
    public void HandOff_ThenBareFollowUp_IsOtherDirected(string text) =>
        Assert.Equal(UtteranceKind.OtherDirected, Gate().Evaluate(text).Kind);

    [Fact]
    public void HandOff_ThenFollowUpNamingTheCandidate_IsAnswered() =>
        Assert.True(Gate().Evaluate("Over to you, Maria. Shervin, go on.").Answerable);

    [Fact]
    public async Task HandOff_ThenBareFollowUp_CreatesNothing()
    {
        var s = await Rc2EngineAcceptanceTests.StartAsync();
        s.Say("Over to you, Maria. Go on.");
        await s.RunAsync(8000); await s.SettleAsync();
        Assert.Empty(s.E.History);
        Assert.Equal(0, s.P.Requests);
        Assert.Equal(1, s.E.Metrics.OtherDirectedSuppressed);
    }

    // ---------------- finding 5: instructions that are not asks to speak ----------------

    [Theory]
    [InlineData("Go on mute please.")]
    [InlineData("I want you to go on mute.")]
    [InlineData("Continue to the next slide.")]
    [InlineData("Go on to the next slide.")]
    [InlineData("Okay, go on to the next question.")]
    [InlineData("And go on to the next one.")]
    [InlineData("Keep going with the exercise.")]
    [InlineData("Carry on with your code.")]
    [InlineData("Continue coding.")]
    [InlineData("Now continue with the coding exercise.")]
    [InlineData("Clarify that in the follow-up email.")]
    [InlineData("Show me your screen.")]
    [InlineData("Show us the dashboard.")]
    [InlineData("Feel free to share any questions you have for us at the end.")]
    [InlineData("Tell me when you can see my screen.")]
    [InlineData("Tell us when you're done.")]
    [InlineData("Give us a second.")]
    public void Instruction_IsNotAnswered(string text) => Assert.False(Gate().Evaluate(text).Answerable, $"{text}: {Gate().Evaluate(text).Kind}");

    [Theory]
    [InlineData("Go on.")] [InlineData("Please continue.")] [InlineData("Continue, please.")] [InlineData("Go on, I'm listening.")]
    [InlineData("Carry on with your answer.")] [InlineData("Keep going.")] [InlineData("Go on then.")] [InlineData("Show me how you would do it.")]
    [InlineData("Continue where you left off.")]
    public void AskToKeepTalking_IsAFollowUp(string text)
    {
        Assert.Equal(UtteranceKind.FollowUp, Gate().Evaluate(text).Kind);
        Assert.True(FollowUpIntent.IsStandaloneFollowUp(text), text);
    }

    [Theory]
    [InlineData("Continue to the next slide.")]
    [InlineData("Show me your screen.")]
    [InlineData("Feel free to share any questions you have for us at the end.")]
    [InlineData("Keep going with the exercise.")]
    public async Task Instruction_CreatesNothing(string text)
    {
        var s = await Rc2EngineAcceptanceTests.StartAsync();
        s.Say(text);
        await s.RunAsync(8000); await s.SettleAsync();
        Assert.Empty(s.E.History);
        Assert.Equal(0, s.P.Requests);
    }

    [Fact]
    public void ContinueFollowedByATopic_IsNotStandalone()
    {
        // The verbs are no longer "anaphoric" by themselves: "continue …" with a topic of its own is not a bare follow-up.
        Assert.False(FollowUpIntent.IsStandaloneFollowUp("Go on about the reconciliation."));
        Assert.True(Gate().Evaluate("Go on about the reconciliation.").Answerable);
    }

    // ---------------- finding 6: a trailing name without a comma ----------------

    [Theory]
    [InlineData("Go on John.")] [InlineData("Continue Sarah.")] [InlineData("Tell me more Maria.")] [InlineData("Go on Maria")]
    [InlineData("Elaborate Maria.")]
    public void TrailingOtherNameWithoutComma_IsOtherDirected(string text) =>
        Assert.Equal(UtteranceKind.OtherDirected, Gate().Evaluate(text).Kind);

    [Theory]
    [InlineData("Go on Shervin.")] [InlineData("Tell me more Shervin.")]
    public void TrailingCandidateNameWithoutComma_IsAnswered(string text) => Assert.True(Gate().Evaluate(text).Answerable, text);

    [Theory]
    [InlineData("Explain Snowflake.")] [InlineData("Describe Terraform.")]
    public void CapitalisedTopicAfterATransitiveAsk_IsNotAVocative(string text) => Assert.True(Gate().Evaluate(text).Answerable, text);

    // ---------------- findings 7 and 8: split only after a candidate-reply allowance ----------------

    [Theory]
    [InlineData("Tell me about a time you led a team.", 1500, "Walk me through it.")]
    [InlineData("How did you scale the ledger?", 1200, "Give me an example.")]
    [InlineData("Tell me about the ledger migration.", 1500, "Go deeper on the reconciliation part.")]
    [InlineData("What was your biggest failure at Teroxx?", 1200, "Why?")]
    [InlineData("What was your biggest failure at Teroxx?", 1200, "Why.")]
    [InlineData("Describe a conflict with a stakeholder.", 1200, "What happened?")]
    [InlineData("What did you build at Teroxx?", 1500, "Tell me more.")]
    public async Task QuickMultiPartAsk_StillMerges(string first, int pauseMs, string second)
    {
        var s = await Rc2EngineAcceptanceTests.StartAsync();
        s.Say(first, pauseMs, second);
        await s.RunAsync(14_000); await s.SettleAsync();
        Assert.Single(s.E.History);
        Assert.Single(s.E.Memory.Questions);
        Assert.Contains(second.TrimEnd('.', '?'), s.E.Memory.Questions[0].Original);
        Assert.Equal(0, s.E.Metrics.FollowUpTurnsSplit);
        Assert.True(s.P.Requests + s.E.Metrics.CacheAnswers <= 2, "a merged turn replaces its provisional answer at most once");
    }

    [Theory]
    [InlineData("What was your biggest failure at Teroxx?", "Why?")]
    [InlineData("What was your biggest failure at Teroxx?", "Why.")]
    [InlineData("Describe a conflict with a stakeholder.", "What happened?")]
    [InlineData("What did you build at Teroxx?", "How so?")]
    [InlineData("What did you build at Teroxx?", "Really?")]
    [InlineData("What did you build at Teroxx?", "Give me an example.")]
    public async Task FollowUpAfterTheReplyAllowance_IsItsOwnTurn(string first, string second)
    {
        var s = await Rc2EngineAcceptanceTests.StartAsync();
        s.Say(first, 2200, second);   // inside the 2.5 s continuation window, past the reply allowance
        await s.RunAsync(14_000); await s.SettleAsync();
        Assert.Equal(2, s.E.History.Count);
        Assert.Equal(2, s.E.Memory.Questions.Count);
        Assert.Equal(1, s.E.Metrics.FollowUpTurnsSplit);
    }

    [Theory]
    [InlineData("Why?")] [InlineData("Why.")] [InlineData("How so?")] [InlineData("How so.")] [InlineData("What happened?")]
    [InlineData("Really?")] [InlineData("And then?")]
    public void EllipticalAsk_IsStandaloneHoweverPunctuated(string text) => Assert.True(FollowUpIntent.IsStandaloneFollowUp(text), text);

    [Fact]
    public void ReallyWithAFallingPeriod_StaysUnanswered() => Assert.False(Gate().Evaluate("Really.").Answerable);

    // ---------------- finding 9: one notion of "follow-up" ----------------

    [Theory]
    [InlineData("Walk me through the process.")] [InlineData("Take me through your thinking.")] [InlineData("Explain how you did that.")]
    [InlineData("Help me understand that better.")] [InlineData("Show me how you would do it.")] [InlineData("Okay, so tell me more.")]
    [InlineData("Why?")] [InlineData("How so?")]
    public void FollowUpIntent_AndClassifier_Agree(string text)
    {
        Assert.True(FollowUpIntent.IsStandaloneFollowUp(text), text);
        Assert.True(QuestionClassifier.Classify(text).IsFollowUp, text);
    }

    [Theory]
    [InlineData("Walk me through the process.")] [InlineData("Take me through your thinking.")]
    public void ProcessAndThinkingFollowUps_AreFollowUpKind(string text) => Assert.Equal(UtteranceKind.FollowUp, Gate().Evaluate(text).Kind);

    [Theory]
    [InlineData("Walk me through the process.")] [InlineData("Take me through your thinking.")] [InlineData("Explain how you did that.")]
    [InlineData("Help me understand that better.")] [InlineData("Show me how you would do it.")]
    public async Task FollowUp_GetsTheFollowUpPrompt_AndNoUnrelatedPreparedAnswer(string followUp)
    {
        var s = await Rc2EngineAcceptanceTests.StartAsync();
        s.Say("What did you build at Teroxx?");
        await s.RunAsync(8000); await s.SettleAsync();
        s.Say(followUp);
        await s.RunAsync(8000); await s.SettleAsync();
        Assert.Equal(2, s.E.Memory.Questions.Count);
        var record = s.E.Memory.Questions[1];
        Assert.Equal(s.E.Memory.Questions[0].Index, record.FollowUpOf);
        var matched = record.MatchedQuestionId == null ? null : TestKnowledge.Load().Questions.First(q => q.QuestionId == record.MatchedQuestionId);
        Assert.True(matched == null || matched.Category == "FOLLOW_UP", $"grounded on {matched?.QuestionId} ({matched?.Category})");
        IReadOnlyList<ChatMessage> last;
        lock (s.P.Calls) last = s.P.Calls[^1];
        Assert.Contains(last, m => m.Content.Contains("This is a FOLLOW-UP"));
    }
}
