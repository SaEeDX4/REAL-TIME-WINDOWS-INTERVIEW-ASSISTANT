using InterviewAssistant.Core.Audio;
using InterviewAssistant.Core.Intelligence;
using InterviewAssistant.Core.Orchestration;
using InterviewAssistant.Core.Turn;
using Xunit;
using Xunit.Abstractions;

namespace InterviewAssistant.Tests;

/// <summary>
/// RC5 P0: candidate-directed imperative follow-ups ("Tell me more.", "Go on.", "Walk me through that.") are real turns —
/// recognized by grammar (an elicitation verb in imperative position), heard over real, often flat, speech — while the
/// same words over a sound that is not a voice still create nothing.
/// </summary>
public class Rc5FollowUpTests
{
    private readonly ITestOutputHelper _out;
    public Rc5FollowUpTests(ITestOutputHelper o) => _out = o;

    public static readonly string[] Positives =
    {
        "Tell me more.", "Walk me through that.", "Explain further.", "Go on.", "Give me an example.", "Elaborate.",
        "Continue.", "Talk me through it.", "Can you expand on that?", "Give me more detail.", "Take me through your thinking.",
        "Walk me through the process.", "Explain how you did that.", "Give me another example.",
        "Why?", "Why.", "How so?", "Really?",
    };

    public static IEnumerable<object[]> PositiveData() => Positives.Select(p => new object[] { p });

    public static IEnumerable<object[]> FollowUpFixtures() =>
        AudioFixtures.All.Where(f => f.IsSpeech && (f.File.StartsWith("speech_fu_") || f.File.StartsWith("speech_tellmemore")))
            .Select(f => new object[] { f.File });

    private static UtteranceGate Gate() => new(new PromptEchoDetector(TestKnowledge.Load().Context.BuildTranscriptionPrompt()), new[] { "Shervin", "Fallahdoust" });

    // ---------------- text: intent classification ----------------

    [Theory]
    [MemberData(nameof(PositiveData))]
    public void Positive_IsACandidateDirectedAsk(string text)
    {
        Assert.False(TurnDetector.IsNonQuestion(text), text);
        var v = Gate().Evaluate(text);
        Assert.True(v.Answerable, $"{text}: {v.Kind} ({v.Reason})");
    }

    [Theory]
    [InlineData("Tell me more.")] [InlineData("Go on.")] [InlineData("Continue.")] [InlineData("Elaborate.")]
    [InlineData("Walk me through that.")] [InlineData("Give me another example.")] [InlineData("Okay, so tell me more.")]
    [InlineData("Please go on.")] [InlineData("Could you elaborate?")] [InlineData("Keep going.")]
    public void ImperativeFollowUp_IsItsOwnKind(string text)
    {
        Assert.Equal(UtteranceKind.FollowUp, Gate().Evaluate(text).Kind);
        Assert.True(FollowUpIntent.IsStandaloneFollowUp(text), text);
    }

    [Theory]
    // A full imperative question is a question, not a follow-up (it brings its own topic).
    [InlineData("Describe a time you disagreed with your manager.")]
    [InlineData("Give me an example of a metric you moved.")]
    [InlineData("Tell me about yourself.")]
    public void FullImperativeQuestion_IsAQuestion(string text)
    {
        Assert.Equal(UtteranceKind.Question, Gate().Evaluate(text).Kind);
        Assert.False(FollowUpIntent.IsStandaloneFollowUp(text), text);
    }

    [Theory]
    // The same verbs outside imperative position, logistics and hand-overs are not asks to the candidate.
    [InlineData("I can explain that later.")]
    [InlineData("We will continue after the break.")]
    [InlineData("Let's continue with the next topic.")]
    [InlineData("Give me a second.")]
    [InlineData("Tell me when you're ready.")]
    [InlineData("Continue sharing your screen.")]
    [InlineData("It was hard to explain in the app.")]
    public void SameVerbs_NotAnAsk(string text) => Assert.False(Gate().Evaluate(text).Answerable, text);

    [Theory]
    [InlineData("John, tell me more.")] [InlineData("Maria, go on.")] [InlineData("Go on, Maria.")] [InlineData("Sarah, walk me through that.")]
    public void Panel_FollowUpToAnotherInterviewer_IsOtherDirected(string text) =>
        Assert.Equal(UtteranceKind.OtherDirected, Gate().Evaluate(text).Kind);

    [Theory]
    [InlineData("Shervin, tell me more.")] [InlineData("Shervin, go on.")] [InlineData("Go on, Shervin.")]
    public void Panel_FollowUpToTheCandidate_IsAnswerable(string text) => Assert.True(Gate().Evaluate(text).Answerable, text);

    [Theory]
    [InlineData("Continue.")] [InlineData("Elaborate.")] [InlineData("Example.")] [InlineData("Details.")]
    public void OneWordFollowUp_IsNotDroppedByTheTurnDetector(string text) => Assert.False(TurnDetector.IsNonQuestion(text), text);

    // ---------------- audio: real (synthesized) flat speech ----------------

    [Theory]
    [MemberData(nameof(FollowUpFixtures))]
    public async Task FollowUpFixture_CreatesExactlyOneTurnHistoryItemAndRequest(string file)
    {
        var f = AudioFixtures.Get(file);
        var s = await PlayAsync(f.File, f.Text);
        Assert.Single(s.E.Memory.Questions);
        Assert.Single(s.E.History);
        Assert.Equal(1, s.E.Metrics.Questions);
        Assert.Equal(1, s.P.Requests + s.E.Metrics.CacheAnswers);   // one answer: from the model or a prepared answer
        Assert.Single(s.Started);
        Assert.Equal(0, s.E.Metrics.TotalSuppressed);
    }

    [Fact]
    public void FlatFollowUp_WasRejectedByTheRc4Rule_AndIsAcceptedByTheRunRule()
    {
        // The root cause, measured: "Go on." spoken level and falling has ≥ 65 % adjacent-hop steadiness, which the
        // rc.4 rule (no pitch tracking) called a held tone; its runs drift, so the rc.5 rule hears a voice.
        var e = RecordedStyleAudioTests.Evidence(AudioFixtures.Pcm(AudioFixtures.Get("speech_fu_goon__calln.wav")));
        _out.WriteLine(e.ToString());
        Assert.True(e.VoiceLike, e.NotVoiceReason);
        var rc4 = e with { TrackedHops = 0, RunHops = 0, FlatRunHops = 0 };
        Assert.False(rc4.VoiceLike);
        Assert.StartsWith("steady pitch", rc4.NotVoiceReason);
    }

    public static IEnumerable<object[]> NonSpeech() => AudioFixtures.All
        .Where(f => !f.IsSpeech && !RecordedStyleAudioTests.KnownVoiceLookalikes.ContainsKey(f.File)).Select(f => new object[] { f.File });

    [Theory]
    [MemberData(nameof(NonSpeech))]
    public async Task SamePhrasesOverNonSpeech_CreateNothing(string file)
    {
        foreach (var text in Positives)
        {
            var s = await PlayAsync(file, text);
            var what = $"{file} heard as '{text}'";
            Assert.True(s.E.Memory.Questions.Count == 0, what + ": a question was recorded");
            Assert.True(s.E.History.Count == 0, what + ": a history item was created");
            Assert.True(s.P.Requests == 0, what + ": an AI request was made");
            Assert.True(s.Started.Count == 0, what + ": an answer was started");
            Assert.All(s.LiveTexts, t => Assert.True(t.Length == 0, $"{what}: live text '{t}'"));
            Assert.Equal(0, s.E.Metrics.FollowUpsAccepted);
        }
    }

    /// <summary>
    /// Short clips of in-range held tones (the first 300–400 ms of a buzz or a piano note) and pulsing tones (70 ms on /
    /// 30 ms off): too few pitch-run hops for the run rule, so the rc.4 steady-pitch rule decides (rc.5 engine audit,
    /// finding 1: before the fix rc.5 answered 12 of 18 phrases over these, rc.4 none).
    /// </summary>
    public static IEnumerable<object[]> ShortNonSpeech() => new[]
    {
        new object[] { "error_low_buzz__direct.wav", "first", 300 }, new object[] { "error_low_buzz__direct.wav", "first", 400 },
        new object[] { "error_low_buzz__callq.wav", "first", 300 }, new object[] { "error_low_buzz__callq.wav", "first", 400 },
        new object[] { "error_low_buzz__calln.wav", "first", 360 }, new object[] { "error_low_buzz__calln.wav", "first", 400 },
        new object[] { "music_piano__callq.wav", "first", 360 }, new object[] { "music_piano__callq.wav", "first", 400 },
        new object[] { "error_low_buzz__direct.wav", "pulsed", 70 }, new object[] { "error_low_buzz__callq.wav", "pulsed", 70 },
        new object[] { "error_low_buzz__calln.wav", "pulsed", 70 }, new object[] { "music_band__calln.wav", "pulsed", 70 },
    };

    /// <summary>The first <paramref name="ms"/> ms of a fixture's sound ("first"), or the sound pulsed <paramref name="ms"/> ms
    /// on / (100 − ms) ms off ("pulsed"), measured from its onset.</summary>
    internal static byte[] Shape(string file, string shape, int ms)
    {
        var pcm = AudioFixtures.Pcm(AudioFixtures.Get(file));
        var s = new short[pcm.Length / 2];
        Buffer.BlockCopy(pcm, 0, s, 0, s.Length * 2);
        int on = Math.Max(0, Array.FindIndex(s, v => Math.Abs((int)v) > 300));
        short[] o;
        if (shape == "first") { o = new short[Math.Min(s.Length, on + ms * 24)]; Array.Copy(s, o, o.Length); }
        else { o = (short[])s.Clone(); for (int i = on; i < o.Length; i++) if ((i - on) / 24 % 100 >= ms) o[i] = 0; }
        var b = new byte[o.Length * 2];
        Buffer.BlockCopy(o, 0, b, 0, b.Length);
        return b;
    }

    [Theory]
    [MemberData(nameof(ShortNonSpeech))]
    public void ShortAndPulsingHeldTones_AreNotVoiceLike(string file, string shape, int ms)
    {
        var e = RecordedStyleAudioTests.Evidence(Shape(file, shape, ms));
        _out.WriteLine(e.ToString());
        Assert.False(e.VoiceLike, $"{file} {shape} {ms}: {e}");
    }

    [Theory]
    [MemberData(nameof(ShortNonSpeech))]
    public async Task SamePhrasesOverShortNonSpeech_CreateNothing(string file, string shape, int ms)
    {
        var pcm = Shape(file, shape, ms);
        foreach (var text in Positives)
        {
            var s = await PlayPcmAsync(pcm, text);
            var what = $"{file} ({shape} {ms}) heard as '{text}'";
            Assert.True(s.E.Memory.Questions.Count == 0, what + ": a question was recorded");
            Assert.True(s.E.History.Count == 0, what + ": a history item was created");
            Assert.True(s.P.Requests == 0, what + ": an AI request was made");
            Assert.True(s.Started.Count == 0, what + ": an answer was started");
            Assert.All(s.LiveTexts, t => Assert.True(t.Length == 0, $"{what}: live text '{t}'"));
            Assert.Equal(0, s.E.Metrics.FollowUpsAccepted);
        }
    }

    // ---------------- engine: follow-ups in a conversation ----------------

    [Fact]
    public async Task QuickFollowUp_AfterACompleteQuestion_IsItsOwnTurn()
    {
        // rc.4: a follow-up starting within 2.5 s of the question was merged into it ("…? Tell me more.") and replaced
        // its answer; no new history item appeared. A complete question followed by a self-contained follow-up, once the
        // candidate had time to start answering, is two asks.
        // Changed after the rc.5 engine audit (finding 7): this test used a 1.5 s pause, which is a quick multi-part
        // question ("…? Tell me more." before the candidate can have said anything) and now merges as in rc.4 — see
        // Rc5EngineAuditTests.QuickMultiPartAsk_StillMerges. 2.2 s of silence is past the candidate-reply allowance and
        // still inside the 2.5 s continuation window, so this still tests the split.
        var s = await Rc2EngineAcceptanceTests.StartAsync();
        s.Say("What did you build at Teroxx?", 2200, "Tell me more.");   // the first turn has finalized; the second starts 2.2 s later
        await s.RunAsync(14_000);
        await s.SettleAsync();
        Assert.Equal(2, s.E.Memory.Questions.Count);
        Assert.Equal(2, s.E.History.Count);
        Assert.Equal("Tell me more.", s.E.Memory.Questions[1].Original);
        Assert.Equal(1, s.E.Metrics.FollowUpTurnsSplit);
    }

    [Fact]
    public async Task UnfinishedQuestion_ThenGoOn_StillMergesIntoOneTurn()
    {
        // The continuation rule is unchanged for a question that was still open ("…and").
        var s = await Rc2EngineAcceptanceTests.StartAsync();
        s.Say("Tell me about the ledger migration and", 2000, "go on about the reconciliation.");   // reopened after the 1.7 s wait for an unfinished turn
        await s.RunAsync(14_000);
        await s.SettleAsync();
        Assert.Single(s.E.Memory.Questions);
        Assert.Contains("reconciliation", s.E.Memory.Questions[0].Original);
        Assert.Equal(0, s.E.Metrics.FollowUpTurnsSplit);
    }

    [Fact]
    public async Task FollowUp_AfterTheAnswer_IsAnsweredOnce()
    {
        var s = await Rc2EngineAcceptanceTests.StartAsync();
        s.Say("What did you build at Teroxx?");
        await s.RunAsync(12_000);
        await s.SettleAsync();
        s.Say("Tell me more.");
        await s.RunAsync(8000);
        await s.SettleAsync();
        Assert.Equal(2, s.E.Memory.Questions.Count);
        Assert.Equal(2, s.E.History.Count);
        Assert.Equal(1, s.E.Metrics.FollowUpsAccepted);
    }

    [Fact]
    public async Task Panel_JohnTellMeMore_IsNotAnswered_ShervinTellMeMore_Is()
    {
        var s = await Rc2EngineAcceptanceTests.StartAsync();
        s.Say("John, tell me more.");
        await s.RunAsync(8000);
        await s.SettleAsync();
        Assert.Empty(s.E.History);
        Assert.Equal(1, s.E.Metrics.OtherDirectedSuppressed);
        s.Say("Shervin, tell me more.");
        await s.RunAsync(8000);
        await s.SettleAsync();
        Assert.Single(s.E.History);
        Assert.Single(s.E.Memory.Questions);
    }

    private Task<InterviewSimulator> PlayAsync(string file, string? transcript) => PlayPcmAsync(AudioFixtures.Pcm(AudioFixtures.Get(file)), transcript);

    internal static async Task<InterviewSimulator> PlayPcmAsync(byte[] pcm, string? transcript)
    {
        var s = new InterviewSimulator(options: new EngineOptions { AutoTick = false });
        await s.StartAsync();
        await s.RunAsync(1000);
        s.SoundFromPcm(pcm, transcript, partials: true);
        await s.RunAsync(pcm.Length / 48 + 4500);
        await s.SettleAsync();
        return s;
    }
}
