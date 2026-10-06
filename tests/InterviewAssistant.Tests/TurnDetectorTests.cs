using InterviewAssistant.Core.Diagnostics;
using InterviewAssistant.Core.Turn;
using Xunit;

namespace InterviewAssistant.Tests;

public class TurnDetectorTests
{
    private static (TurnDetector Td, ManualClock Clock, List<FinalizedTurn> Out) Create()
    {
        var clock = new ManualClock();
        var td = new TurnDetector(clock);
        var list = new List<FinalizedTurn>();
        td.TurnFinalized += list.Add;
        td.Start();
        return (td, clock, list);
    }

    private static void Run(TurnDetector td, ManualClock c, int ms) { for (int i = 0; i < ms; i += 20) { c.Advance(20); td.Tick(); } }

    private static void Segment(TurnDetector td, ManualClock c, string id, string text, int speakMs = 1500, int transcriptLatencyMs = 300)
    {
        td.OnSpeechStarted();
        Run(td, c, speakMs);
        td.OnSpeechStopped();
        Run(td, c, transcriptLatencyMs);
        td.OnSegmentCompleted(id, text);
    }

    [Fact]
    public void ClearQuestionFinalizesQuicklyAfterSpeechEnds()
    {
        var (td, c, outp) = Create();
        Segment(td, c, "a", "How would you increase XAB adoption?");
        var stoppedAt = c.NowMs - 300;
        Run(td, c, 1500);
        Assert.Single(outp);
        var latency = outp[0].FinalizedMs - stoppedAt;
        Assert.InRange(latency, 300, 700);
    }

    [Fact]
    public void MultiPartQuestionWithPauseIsOneQuestion()
    {
        var (td, c, outp) = Create();
        Segment(td, c, "a", "How would you improve XAB utility and");
        Run(td, c, 600); // short thinking pause by interviewer (< incomplete threshold)
        Segment(td, c, "b", "which metrics would you use to measure success?");
        Run(td, c, 2000);
        Assert.Single(outp);
        Assert.Equal("How would you improve XAB utility and which metrics would you use to measure success?", outp[0].Text);
    }

    [Fact]
    public void ResumedSpeechDuringSettlingCancelsFinalization()
    {
        var (td, c, outp) = Create();
        Segment(td, c, "a", "Tell me about your crypto background.");
        Run(td, c, 200); // within settle window
        Segment(td, c, "b", "And specifically your experience at Arzif.");
        Run(td, c, 2000);
        Assert.Single(outp);
        Assert.Contains("Arzif", outp[0].Text);
        Assert.Contains("crypto background", outp[0].Text);
    }

    [Fact]
    public void RevisedTranscriptForSameItemDoesNotDuplicate()
    {
        var (td, c, outp) = Create();
        Segment(td, c, "a", "How would you prioritize the backlog");
        td.OnSegmentCompleted("a", "How would you prioritize the backlog?");
        Run(td, c, 2000);
        Assert.Single(outp);
        Assert.Equal("How would you prioritize the backlog?", outp[0].Text);
    }

    [Fact]
    public void PleasantriesNeverTriggerAnswers()
    {
        var (td, c, outp) = Create();
        Segment(td, c, "a", "Okay, great, thank you.");
        Run(td, c, 2000);
        Assert.Empty(outp);
        Assert.Equal(TurnState.Listening, td.State);
    }

    [Fact]
    public void MissingTranscriptDoesNotHangForever()
    {
        var (td, c, outp) = Create();
        Segment(td, c, "a", "What would you do in your first ninety days");
        td.OnSpeechStarted(); Run(td, c, 500); td.OnSpeechStopped(); // segment b transcript never arrives
        Run(td, c, 5000);
        Assert.Single(outp);
        Assert.Equal(TurnState.Listening, td.State);
    }

    [Theory]
    [InlineData("How would you prioritize?", 450)]
    [InlineData("How would you prioritize the backlog and", 1700)]
    [InlineData("I'd like to understand your background,", 1700)]
    [InlineData("Let's talk about the ledger", 750)]
    public void AdaptiveDelay(string text, int expected) => Assert.Equal(expected, new TurnDetector(new ManualClock()).AdaptiveDelayMs(text));

    [Fact]
    public void DuplicateGuardSuppressesRevisionsButNotNewQuestions()
    {
        var g = new DuplicateGuard();
        g.Register("How would you increase XAB adoption?", 0);
        Assert.True(g.IsDuplicate("how would you increase xab adoption", 1000));
        Assert.True(g.IsDuplicate("So how would you increase XAB adoption?", 2000));
        Assert.False(g.IsDuplicate("How would you measure XAB retention by cohort?", 3000));
        Assert.False(g.IsDuplicate("How would you increase XAB adoption?", 60_000)); // outside window
    }
}
