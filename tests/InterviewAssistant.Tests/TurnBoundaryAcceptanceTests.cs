using InterviewAssistant.Core.Orchestration;
using InterviewAssistant.Core.Providers;
using Xunit;
using Xunit.Abstractions;

namespace InterviewAssistant.Tests;

/// <summary>
/// Turn finalization under realistic server-VAD timing (see <see cref="InterviewSimulator"/>).
/// Contract: one interviewer question → one history entry → one visible answer → at most one AI request
/// (for pauses up to ~1 s); longer continuations are merged into the same entry.
/// </summary>
public class TurnBoundaryAcceptanceTests
{
    private readonly ITestOutputHelper _out;
    public TurnBoundaryAcceptanceTests(ITestOutputHelper o) => _out = o;

    private const string PartA = "How would you increase XAB utility and user retention?";
    private const string PartB = "And which metrics would you track to prove that the strategy is working?";

    private static async Task<InterviewSimulator> Sim()
    {
        var s = new InterviewSimulator();
        await s.StartAsync();
        return s;
    }

    [Fact]
    public async Task SingleQuestionIsOneTurnAndFinalizesQuickly()
    {
        var s = await Sim();
        s.Say("Tell me about your crypto experience.");
        await s.RunAsync(6000); await s.SettleAsync();
        var a = Assert.Single(s.Visible);
        Assert.Single(s.E.History);
        Assert.Equal("Tell me about your crypto experience.", a.Question);
        var sample = s.E.Metrics.Samples.Single();
        _out.WriteLine($"speech_stopped event → finalized {sample.SpeechEndToFinalizedMs} ms (+{s.VadSilenceMs} ms VAD window from real silence)");
        Assert.InRange(sample.SpeechEndToFinalizedMs, 0, 700);
    }

    /// <summary>Acceptance C: the real failing case — two parts with a natural pause must be ONE question and ONE answer.</summary>
    [Theory]
    [InlineData(300)]
    [InlineData(500)]
    [InlineData(800)]
    [InlineData(1000)]
    public async Task TwoPartQuestionWithNaturalPauseIsOneQuestionOneAnswer(int pauseMs)
    {
        var s = await Sim();
        s.Say(PartA, pauseMs, PartB);
        await s.RunAsync(12_000); await s.SettleAsync();

        var history = s.E.History;
        Assert.Single(history);                                         // not two history entries
        var a = Assert.Single(s.Visible);                               // one answer on screen
        Assert.Contains("XAB utility", a.Question);
        Assert.Contains("which metrics", a.Question);
        Assert.True(s.P.Requests <= 1, $"{s.P.Requests} AI requests for one question (pause {pauseMs} ms)");
        Assert.Single(s.E.Memory.Questions);                            // report/memory also see one question
        Assert.Equal(1, s.E.Metrics.Questions);
        _out.WriteLine($"pause {pauseMs} ms → '{a.Question}' ({a.Source}, merged {s.E.Metrics.TurnsMerged}, requests {s.P.Requests})");
    }

    [Theory]
    [InlineData(1400)]
    [InlineData(2000)]
    public async Task LongerContinuationIsStillMergedIntoTheSameEntry(int pauseMs)
    {
        var s = await Sim();
        s.Say("How would you improve onboarding for new traders?", pauseMs, "And what would you measure in the first month?");
        await s.RunAsync(14_000); await s.SettleAsync();
        Assert.Single(s.E.History);
        var a = Assert.Single(s.Visible);
        Assert.Contains("first month", a.Question);
        Assert.Contains("onboarding", a.Question);
        Assert.True(s.P.Requests <= 2);   // at most one provisional request that was cancelled
        Assert.All(s.Completed.Where(c => c.Superseded), c => Assert.DoesNotContain(c, s.E.History)); // replaced, not listed
    }

    [Fact]
    public async Task CompletedQuestionFollowedByADistinctQuestionLaterIsTwoTurns()
    {
        var s = await Sim();
        s.Say("How do you prioritize the backlog?");
        await s.RunAsync(7000);                                          // ~4 s of silence after the question: the turn really ended
        Assert.Single(s.Visible);
        s.Say("What is a closed-loop token economy?");
        await s.RunAsync(6000); await s.SettleAsync();
        Assert.Equal(2, s.E.History.Count);
        Assert.Equal(2, s.Visible.Count);
        Assert.Equal(0, s.E.Metrics.TurnsMerged);
    }

    [Fact]
    public async Task InterviewerRestatingTheQuestionReplacesTheWording()
    {
        var s = await Sim();
        s.Say("How would you increase retention?", 900, "Sorry, let me rephrase: how would you increase retention for VIP clients specifically?");
        await s.RunAsync(14_000); await s.SettleAsync();
        var a = Assert.Single(s.Visible);
        Assert.Single(s.E.History);
        Assert.StartsWith("How would you increase retention for VIP clients", a.Question, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sorry", a.Question);
    }

    [Fact]
    public async Task AcknowledgementDuringContinuationWindowDoesNotMergeOrBlockTheAnswer()
    {
        var s = await Sim();
        s.Say("How would you design a fee discount feature using XAB tokens for frequent traders?", 900, "Okay.");
        await s.RunAsync(12_000); await s.SettleAsync();
        var a = Assert.Single(s.Visible);
        Assert.DoesNotContain("Okay", a.Question);
        Assert.NotEmpty(a.SnapshotBullets());                           // the held AI request was released and answered
        Assert.Equal(0, s.E.Metrics.TurnsMerged);
    }

    [Fact]
    public async Task FragmentedDeltasAndDuplicateSegmentsGiveOneAnswer()
    {
        var s = await Sim();
        s.T.Start(); await s.RunAsync(2000); s.T.Stop(); await s.RunAsync(200);
        foreach (var piece in new[] { "How ", "would ", "you ", "handle ", "an ", "unrealistic ", "deadline" }) s.T.Partial("seg1", piece);
        s.T.Complete("How would you handle an unrealistic deadline", "seg1");
        s.T.Complete("How would you handle an unrealistic deadline?", "seg1");   // revised final for the same item
        s.T.Complete("How would you handle an unrealistic deadline?", "seg1b"); // same text re-delivered with a new id
        await s.RunAsync(5000); await s.SettleAsync();
        var a = Assert.Single(s.Visible);
        Assert.Equal("How would you handle an unrealistic deadline?", a.Question);
    }

    [Fact]
    public async Task FollowUpAfterTheCandidateAnsweredIsANewLinkedQuestion()
    {
        var s = await Sim();
        s.Say("Tell me about a time you improved retention.");
        await s.RunAsync(20_000);                                        // candidate answers (interviewer silent)
        s.Say("Can you give a concrete example?");
        await s.RunAsync(6000); await s.SettleAsync();
        Assert.Equal(2, s.Visible.Count);
        Assert.Equal(1, s.E.Memory.Questions[1].FollowUpOf);
    }

    [Fact]
    public async Task NewQuestionDuringAStreamingAnswerSupersedesItWithoutDuplicates()
    {
        // Slow AI measured on the VIRTUAL clock (deterministic on any OS timer resolution): 3 s to first token, 3 s per bullet.
        var s = new InterviewSimulator(provider: c => new VirtualLatencyProvider(c) { FirstTokenMs = 3000, PerBulletMs = 3000 });
        await s.StartAsync();
        s.Say("How would you price a premium tier bundle for VIP clients?");
        await s.RunAsync(8500);                                          // finalized ~4.8 s; answer still streaming
        Assert.False(s.E.Current!.IsComplete);
        s.Say("What's your view on gamification in finance?");
        await s.RunAsync(8000); await s.SettleAsync(20_000);
        Assert.Equal(2, s.E.History.Count);
        Assert.Contains("gamification", s.E.Current!.Question);
        Assert.True(s.E.Current.IsComplete);
        Assert.Equal(0, s.E.Metrics.DuplicatesSuppressed);
    }

    [Fact]
    public async Task ReconnectRightAfterTheQuestionStillAnswersItOnce()
    {
        var s = await Sim();
        s.Say("How would you write acceptance criteria for a reward payout feature?");
        await s.RunAsync(3800);                                          // transcript arrives, settle pending
        s.T.Raise(ConnectionStatus.Reconnecting, "network drop");
        await s.RunAsync(1500);
        s.T.Raise(ConnectionStatus.Connected, null);
        await s.RunAsync(4000); await s.SettleAsync();
        var a = Assert.Single(s.Visible);
        Assert.Contains("acceptance criteria", a.Question);
        Assert.Equal(1, s.E.Metrics.Reconnects);
    }
}
