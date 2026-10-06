using InterviewAssistant.Core.Diagnostics;
using InterviewAssistant.Core.Intelligence;
using InterviewAssistant.Core.Knowledge;
using InterviewAssistant.Core.Orchestration;
using InterviewAssistant.Core.Providers;
using Xunit;
using Xunit.Abstractions;

namespace InterviewAssistant.Tests;

public class EngineTests
{
    private readonly ITestOutputHelper _out;
    public EngineTests(ITestOutputHelper o) => _out = o;

    private sealed class Rig
    {
        public required InterviewEngine Engine;
        public required FakeTranscriber T;
        public required FakeProvider P;
        public required ManualClock Clock;
        public readonly List<AnswerView> Completed = new();
        public readonly List<(int Id, string Bullet)> Bullets = new();

        public void Run(int ms) { for (int i = 0; i < ms; i += 20) { Clock.Advance(20); Engine.Tick(); } }

        public async Task<AnswerView> Ask(string text, params string[] parts)
        {
            int before;
            lock (Completed) before = Completed.Count;
            var segs = parts.Length > 0 ? parts : new[] { text };
            foreach (var s in segs) { T.Start(); Run(1200); T.Stop(); Run(250); T.Complete(s); Run(300); }
            Run(2000);
            for (int i = 0; i < 200; i++) { lock (Completed) if (Completed.Count > before) return Completed[^1]; await Task.Delay(10); }
            throw new TimeoutException("no answer for: " + text);
        }
    }

    private static async Task<Rig> CreateAsync(bool withProvider = true)
    {
        var clock = new ManualClock();
        var t = new FakeTranscriber();
        var p = new FakeProvider();
        var e = new InterviewEngine(TestKnowledge.Load(), t, withProvider ? p : null, new EngineOptions { AutoTick = false }, clock);
        var rig = new Rig { Engine = e, T = t, P = p, Clock = clock };
        e.AnswerCompleted += a => { lock (rig.Completed) rig.Completed.Add(a); };
        e.BulletAdded += (a, b) => { lock (rig.Bullets) rig.Bullets.Add((a.Id, b)); };
        await e.StartListeningAsync();
        return rig;
    }

    [Fact]
    public async Task KnownQuestionIsServedFromCacheWithoutLlmCall()
    {
        var r = await CreateAsync();
        var a = await r.Ask("How would you prioritize improvements to the XAB rewards ecosystem?");
        Assert.Equal(AnswerSource.Cache, a.Source);
        Assert.Equal(3, a.Bullets.Count);
        Assert.StartsWith("I'd start with the customer and business outcome", a.Bullets[0]);
        Assert.Equal(0, r.P.Requests);
        var s = r.Engine.Metrics.Samples.Last();
        _out.WriteLine($"speechEnd->finalized {s.SpeechEndToFinalizedMs} ms, finalized->first bullet {s.FinalizedToFirstBulletMs} ms");
        Assert.InRange(s.SpeechEndToFinalizedMs, 200, 1200);
    }

    [Fact]
    public async Task UnexpectedQuestionStreamsFromLlmAsStableBullets()
    {
        var r = await CreateAsync();
        var a = await r.Ask("How would you price a premium subscription bundled with XAB benefits?");
        Assert.Equal(AnswerSource.Llm, a.Source);
        Assert.Equal(1, r.P.Requests);
        Assert.Equal(3, a.Bullets.Count);
        Assert.Equal("I'd start by defining the goal and the user problem clearly.", a.Bullets[0]);
        // Bullets were appended exactly once each, in order (no rewriting).
        Assert.Equal(a.Bullets, r.Bullets.Where(b => b.Id == a.Id).Select(b => b.Bullet));
        var prompt = r.P.Calls[0][1].Content;
        Assert.Contains("INTERVIEWER QUESTION", prompt);
        Assert.True(r.P.Calls[0].Sum(m => m.Content.Length) < 14000, "prompt should stay compact");
    }

    [Fact]
    public async Task MultiPartQuestionGetsOneAnswer()
    {
        var r = await CreateAsync();
        await r.Ask("", "How would you improve XAB utility and", "which metrics would you use to measure success?");
        Assert.Single(r.Completed);
        Assert.Contains("which metrics", r.Completed[0].Question);
    }

    [Fact]
    public async Task RepeatedFinalTranscriptDoesNotCreateSecondAnswer()
    {
        var r = await CreateAsync();
        await r.Ask("How would you increase XAB adoption?");
        r.T.Start(); r.Run(800); r.T.Stop(); r.Run(200); r.T.Complete("How would you increase XAB adoption"); r.Run(2500);
        await Task.Delay(100);
        Assert.Single(r.Completed);
        Assert.Equal(1, r.Engine.Metrics.DuplicatesSuppressed);
    }

    [Fact]
    public async Task LlmFailureFallsBackToPreparedAnswer()
    {
        var r = await CreateAsync();
        r.P.Throw = new ProviderException(ProviderErrorKind.Network, "offline");
        // Medium-similarity phrasing (not an exact cache hit) -> would go to LLM -> fails -> prepared fallback.
        var a = await r.Ask("Could you explain how you think about rewards cost sustainability and the ROI side?");
        _out.WriteLine($"{a.Source} {a.MatchedQuestionId} {a.MatchScore:0.00} {a.Note}");
        Assert.True(a.Source is AnswerSource.CacheFallback or AnswerSource.Cache);
        Assert.NotEmpty(a.Bullets);
    }

    [Fact]
    public async Task InvalidKeyWithNoMatchShowsActionableError()
    {
        var r = await CreateAsync();
        r.P.Throw = new ProviderException(ProviderErrorKind.InvalidApiKey, "bad key");
        var a = await r.Ask("Zebra quantum basketball orchestra?");
        Assert.Equal(AnswerSource.Error, a.Source);
        Assert.Contains("Settings", a.Note);
        Assert.Equal(EngineStatus.ApiError, r.Engine.Status);
    }

    [Fact]
    public async Task ManualQuestionWorksWithoutAudioOrProvider()
    {
        var e = new InterviewEngine(TestKnowledge.Load(), null, null, new EngineOptions { AutoTick = false });
        AnswerView? done = null;
        e.AnswerCompleted += a => done = a;
        await e.SubmitManualQuestionAsync("tell me about yourself");
        Assert.NotNull(done);
        Assert.Equal(AnswerSource.Cache, done!.Source);
        Assert.Equal(4, done.Bullets.Count);
    }

    [Fact]
    public async Task VariantRequestUsesLlmWithStyleAndFollowUpContext()
    {
        var r = await CreateAsync();
        await r.Ask("Tell me about your crypto experience.");
        await r.Engine.RequestVariantAsync(AnswerStyle.Technical);
        Assert.Equal(1, r.P.Requests);
        Assert.Contains("more technical", r.P.Calls[0][0].Content);
        Assert.Contains("RECENT INTERVIEW CONTEXT", r.P.Calls[0][1].Content);
        Assert.Contains("Arzif", r.P.Calls[0][1].Content);
    }

    [Fact]
    public async Task FabricatedClaimFromLlmIsSoftenedAndFlagged()
    {
        var r = await CreateAsync();
        r.P.Responder = _ => new[] { "MODE: HYPOTHETICAL\n", "• I built a reward ledger handling 43% more volume.\n", "• I'd measure it.\n" };
        var a = await r.Ask("How would you design a cashback program in XAB for card spending?");
        Assert.StartsWith("I'd build", a.Bullets[0]);
        Assert.Contains(a.ValidationFlags, f => f.StartsWith("UNVERIFIED_NUMBER"));
    }

    [Fact]
    public async Task NewQuestionCancelsInFlightGeneration()
    {
        var r = await CreateAsync();
        r.P.DelayMsPerDelta = 150;
        var first = r.Engine.SubmitManualQuestionAsync("How would you design a quantum-safe custody roadmap?");
        await Task.Delay(200);
        var second = r.Engine.SubmitManualQuestionAsync("How would you design an onboarding quest for new traders?");
        await Task.WhenAll(first, second);
        var answers = r.Engine.History;
        Assert.Equal("Superseded", answers[0].Note);
        Assert.Equal(3, answers[1].Bullets.Count);
    }

    [Fact]
    public async Task ReconnectStatusIsSurfacedAndRecovers()
    {
        var r = await CreateAsync();
        r.T.Raise(ConnectionStatus.Reconnecting, "drop");
        Assert.Equal(EngineStatus.Reconnecting, r.Engine.Status);
        r.T.Raise(ConnectionStatus.Connected, null);
        Assert.Equal(EngineStatus.Listening, r.Engine.Status);
        Assert.Equal(1, r.Engine.Metrics.Reconnects);
    }

    [Fact]
    public async Task PauseStopsAudioAndAnswers()
    {
        var r = await CreateAsync();
        r.Engine.Pause();
        r.Engine.OnAudioFrame(new byte[4800], -20);
        Assert.Equal(0, r.T.FramesReceived);
        r.T.Start(); r.Run(500); r.T.Stop(); r.T.Complete("How would you increase XAB adoption?"); r.Run(2000);
        await Task.Delay(50);
        Assert.Empty(r.Completed);
        await r.Engine.StartListeningAsync();
        r.Engine.OnAudioFrame(new byte[4800], -20);
        Assert.Equal(1, r.T.FramesReceived);
        await r.Engine.StopAsync();
        Assert.Equal(EngineStatus.Stopped, r.Engine.Status);
    }

    [Fact]
    public async Task NoAudioWarningAppearsAndClears()
    {
        var r = await CreateAsync();
        r.Run(21_000);
        Assert.Equal(EngineStatus.NoAudio, r.Engine.Status);
        r.Engine.OnAudioFrame(new byte[4800], -20);
        r.Run(40);
        Assert.Equal(EngineStatus.Listening, r.Engine.Status);
    }
}
