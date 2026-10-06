using InterviewAssistant.Core.Orchestration;
using Xunit;
using Xunit.Abstractions;

namespace InterviewAssistant.Tests;

/// <summary>
/// Simulated 60-minute interview on a virtual clock: continuous audio frames (100 ms), interviewer turns with
/// multi-part pauses, small talk, transcript revisions, a mid-session reconnect. Verifies bounded memory,
/// one answer per question, no duplicates, bounded LLM requests and bounded history/metrics.
/// </summary>
public class SoakTests
{
    private readonly ITestOutputHelper _out;
    public SoakTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public async Task SixtyMinuteSimulatedInterviewIsStable()
    {
        var kb = TestKnowledge.Load();
        var clock = new InterviewAssistant.Core.Diagnostics.ManualClock();
        var t = new FakeTranscriber();
        var p = new FakeProvider();
        var e = new InterviewEngine(kb, t, p, new EngineOptions { AutoTick = false }, clock);
        int answers = 0;
        e.AnswerCompleted += _ => Interlocked.Increment(ref answers);
        await e.StartListeningAsync();

        var frame = new byte[4800];
        var rnd = new Random(42);
        long simulatedMs = 0, endMs = 60 * 60 * 1000;
        int questionsAsked = 0, smallTalk = 0;
        var unexpected = new[] { "How would you price a premium tier bundle?", "What's your view on gamification in finance?", "How would you explain the ledger to a CEO?" };
        GC.Collect(); GC.WaitForPendingFinalizers();
        long memStart = GC.GetTotalMemory(true);

        void Advance(int ms)
        {
            for (int i = 0; i < ms; i += 20)
            {
                clock.Advance(20); simulatedMs += 20;
                if (simulatedMs % 100 == 0) e.OnAudioFrame(frame, -30);
                e.Tick();
            }
        }

        while (simulatedMs < endMs)
        {
            questionsAsked++;
            string q = rnd.NextDouble() < 0.25 ? unexpected[rnd.Next(unexpected.Length)] + " (" + questionsAsked + ")" : kb.Questions[rnd.Next(kb.Questions.Count)].SemanticVariants[0] + " item " + (1000 + questionsAsked);
            if (rnd.NextDouble() < 0.3)
            {
                var words = q.Split(' ');
                var cut = Math.Max(2, words.Length / 2);
                t.Start(); Advance(1500); t.Stop(); Advance(300); t.Complete(string.Join(' ', words[..cut]) + " and"); Advance(600);
                t.Start(); Advance(1500); t.Stop(); Advance(300); t.Complete(string.Join(' ', words[cut..]) + "?");
            }
            else { t.Start(); Advance(2500); t.Stop(); Advance(300); var id = t.Complete(q); t.Complete(q + "?", id); }
            Advance(2500);
            if (questionsAsked == 100) { t.Raise(InterviewAssistant.Core.Providers.ConnectionStatus.Reconnecting, "simulated drop"); Advance(2000); t.Raise(InterviewAssistant.Core.Providers.ConnectionStatus.Connected, null); }
            if (rnd.NextDouble() < 0.2) { smallTalk++; t.Start(); Advance(700); t.Stop(); Advance(300); t.Complete("Okay, great."); }
            Advance(20_000); // candidate speaks (interviewer silent)
            if (questionsAsked % 50 == 0) await Task.Delay(20);
        }
        await Task.Delay(300);
        await e.StopAsync();
        GC.Collect(); GC.WaitForPendingFinalizers();
        long memEnd = GC.GetTotalMemory(true);

        var m = e.Metrics;
        _out.WriteLine($"Simulated {simulatedMs / 60000} min, questions {questionsAsked}, answers {answers}, small talk {smallTalk}, cache {m.CacheAnswers}, llm {m.LlmAnswers}, llm requests {p.Requests}, duplicates {m.DuplicatesSuppressed}, reconnects {m.Reconnects}, frames {t.FramesReceived}");
        _out.WriteLine($"Managed memory start {memStart / 1024} KB, end {memEnd / 1024} KB; history {e.History.Count}; samples {m.Samples.Count}");
        var fin = m.Samples.Select(s => s.SpeechEndToFinalizedMs).ToList();
        _out.WriteLine($"speech-end->finalized p50 {InterviewAssistant.Core.Diagnostics.SessionMetrics.Percentile(fin, 50)} ms p95 {InterviewAssistant.Core.Diagnostics.SessionMetrics.Percentile(fin, 95)} ms");

        Assert.True(questionsAsked > 100);
        Assert.Equal(1, m.Reconnects);
        Assert.Equal(questionsAsked, answers);                       // exactly one answer per question
        Assert.True(p.Requests <= questionsAsked);                    // never more than one LLM call per question
        Assert.True(e.History.Count <= e.Options.HistoryCapacity);    // bounded
        Assert.True(m.Samples.Count <= m.Capacity);
        Assert.True(memEnd - memStart < 8 * 1024 * 1024, $"memory grew {(memEnd - memStart) / 1024} KB");
        Assert.InRange(t.FramesReceived, 35_000L, 37_000L);
    }
}
