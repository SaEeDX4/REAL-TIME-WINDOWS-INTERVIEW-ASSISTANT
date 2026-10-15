using System.Runtime.CompilerServices;
using InterviewAssistant.Core.Diagnostics;
using InterviewAssistant.Core.Intelligence;
using InterviewAssistant.Core.Orchestration;
using InterviewAssistant.Core.Providers;
using Xunit;
using Xunit.Abstractions;

namespace InterviewAssistant.Tests;

[CollectionDefinition("Soak", DisableParallelization = true)]
public sealed class SoakCollection { }

/// <summary>AI provider whose latency is measured on the engine's virtual clock (first token, then per bullet).</summary>
public sealed class VirtualLatencyProvider(ManualClock clock) : IAnswerProvider
{
    public string Name => "virtual-latency";
    public int FirstTokenMs { get; set; } = 700;
    public int PerBulletMs { get; set; } = 250;
    public int Requests;
    public ProviderException? Throw { get; set; }

    private async Task WaitUntil(long t, CancellationToken ct) { while (clock.NowMs < t) await Task.Delay(1, ct); }

    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, int maxTokens, [EnumeratorCancellation] CancellationToken ct)
    {
        Interlocked.Increment(ref Requests);
        if (Throw != null) throw Throw;
        var start = clock.NowMs;
        await WaitUntil(start + FirstTokenMs, ct);
        yield return "MODE: HYPOTHETICAL\n• I'd start with the user problem and the business goal, ";
        yield return "then agree success measures.\n";
        await WaitUntil(clock.NowMs + PerBulletMs, ct);
        yield return "• I'd prioritise by value, risk and effort together with Engineering and Compliance.\n";
        await WaitUntil(clock.NowMs + PerBulletMs, ct);
        yield return "• I'd ship in small steps and track activation, retention and support tickets.\n";
    }
}

/// <summary>
/// Long simulated interviews on a virtual clock with realistic VAD timing (see <see cref="InterviewSimulator"/>):
/// 100+ questions mixing prepared and novel questions, multi-part questions with natural pauses, fragmented and
/// revised transcripts, acknowledgements, a reconnect, pause/resume with a typed question, AI provider failures and
/// alias terminology. Reports latency percentiles and checks one-question → one-answer invariants and bounded memory.
/// </summary>
[Collection("Soak")]
public class SoakTests
{
    private readonly ITestOutputHelper _out;
    public SoakTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public async Task LongSimulatedInterviewKeepsOneAnswerPerQuestionAndReportsLatency()
    {
        var kb = TestKnowledge.Load();
        VirtualLatencyProvider provider = null!;
        var s = new InterviewSimulator(kb, provider: c => provider = new VirtualLatencyProvider(c));
        var sim = s;
        var e = s.E;
        await e.StartListeningAsync();

        var rnd = new Random(7);
        var novel = new[]
        {
            "How would you price a premium tier bundle for frequent traders?", "What's your view on gamification in financial products?",
            "How would you explain the ledger to a non-technical CEO?", "How would you onboard a new market partner in Dubai?",
            "How would you reduce support tickets about delayed rewards?", "How would you decide between building and buying a KYC vendor?",
        };
        int asked = 0, multiPart = 0, acknowledgements = 0, typedWhilePaused = 0, aliasQuestions = 0;
        GC.Collect(); GC.WaitForPendingFinalizers();
        long memStart = GC.GetTotalMemory(true);

        for (int i = 1; i <= 105; i++)
        {
            asked++;
            if (i == 60) { sim.T.Raise(ConnectionStatus.Reconnecting, "simulated drop"); await s.RunAsync(1500); sim.T.Raise(ConnectionStatus.Connected, null); }
            if (i is 80 or 81) provider.Throw = new ProviderException(ProviderErrorKind.ServiceUnavailable, "503") { Code = "ai_unavailable" };
            if (i == 82) provider.Throw = null;
            if (i == 90)
            {
                e.Pause(); await s.RunAsync(4 * 60_000 / 10);   // paused (compressed): no listening
                var typed = e.SubmitManualQuestionAsync("How would you design a referral bonus that does not attract fraud?");
                while (!typed.IsCompleted) await s.RunAsync(20);   // virtual time runs while the AI answers
                await typed;
                typedWhilePaused++; asked++;
                await e.StartListeningAsync();
            }

            var roll = rnd.NextDouble();
            if (i % 15 == 0)
            {
                aliasQuestions++;
                sim.Say("How would you increase adoption of the ab locks token on terox?");
            }
            else if (roll < 0.25)
            {
                multiPart++;
                var pause = new[] { 300, 500, 800, 1000 }[rnd.Next(4)];
                sim.Say(novel[rnd.Next(novel.Length)].TrimEnd('?') + " for VIP clients?", pause, "And which metrics would you track to prove it is working?");
            }
            else if (roll < 0.55) sim.Say(novel[rnd.Next(novel.Length)]);
            else sim.Say(kb.Questions[rnd.Next(kb.Questions.Count)].SemanticVariants[0] + "?");

            await s.RunAsync(9000);
            if (rnd.NextDouble() < 0.2) { acknowledgements++; sim.Say("Okay, great."); await s.RunAsync(2500); }
            await s.RunAsync(15_000);   // candidate answers; interviewer silent
            await s.SettleAsync();
        }
        await e.StopAsync();
        GC.Collect(); GC.WaitForPendingFinalizers();
        long memEnd = GC.GetTotalMemory(true);

        var m = e.Metrics;
        var samples = m.Samples;
        string P(IEnumerable<long> xs) => $"p50 {SessionMetrics.Percentile(xs, 50)} / p95 {SessionMetrics.Percentile(xs, 95)} / max {xs.Where(x => x >= 0).DefaultIfEmpty(-1).Max()} ms";
        var cache = samples.Where(x => x.Source is "Cache").ToList();
        var llm = samples.Where(x => x.Source is "Llm").ToList();
        _out.WriteLine($"questions {asked} (multi-part {multiPart}, typed while paused {typedWhilePaused}, alias {aliasQuestions}), acknowledgements {acknowledgements}");
        _out.WriteLine($"answers: memory {e.Memory.Questions.Count}, cache {m.CacheAnswers}, llm {m.LlmAnswers}, fallback {m.FallbackAnswers}, api errors {m.ApiErrors}, merged turns {m.TurnsMerged}, AI requests {provider.Requests}, duplicates {m.DuplicatesSuppressed}, reconnects {m.Reconnects}");
        _out.WriteLine($"speech_stopped event → question finalized: {P(samples.Where(x => x.SpeechEndToFinalizedMs >= 0).Select(x => x.SpeechEndToFinalizedMs))} (add the 400 ms VAD window for real silence → finalized)");
        _out.WriteLine($"prepared answer: finalized → first bullet {P(cache.Select(x => x.FinalizedToFirstBulletMs))}");
        _out.WriteLine($"AI answer (700 ms simulated first token): finalized → first token {P(llm.Select(x => x.FinalizedToFirstTokenMs))}; → first bullet {P(llm.Select(x => x.FinalizedToFirstBulletMs))}; → complete {P(llm.Select(x => x.FinalizedToCompleteMs))}");
        _out.WriteLine($"managed memory start {memStart / 1024} KB end {memEnd / 1024} KB; history {e.History.Count}; samples {samples.Count}");

        Assert.True(asked >= 100);
        // One question → one record (multi-part merged). The only exception is the same question repeated within the
        // duplicate window (the random script can re-ask a bank question), which is suppressed on purpose.
        Assert.True(m.DuplicatesSuppressed <= 3, $"duplicates {m.DuplicatesSuppressed}");
        Assert.Equal(asked - m.DuplicatesSuppressed, e.Memory.Questions.Count);
        Assert.Equal(1, m.Reconnects);
        Assert.True(provider.Requests <= asked, $"{provider.Requests} AI requests for {asked} questions");
        Assert.True(m.FallbackAnswers + m.ApiErrors >= 2);              // provider failures were handled, not crashed
        Assert.All(e.Memory.Questions.Where(q => q.Original.Contains("token on", StringComparison.Ordinal)), q => Assert.Contains("Abloxx", q.Original));
        Assert.True(e.History.Count <= e.Options.HistoryCapacity);
        Assert.True(samples.Count <= m.Capacity);
        Assert.True(memEnd - memStart < 8 * 1024 * 1024, $"memory grew {(memEnd - memStart) / 1024} KB");
        // Latency budgets on the virtual clock (network/model time is simulated, not guaranteed in production).
        Assert.True(SessionMetrics.Percentile(samples.Where(x => x.SpeechEndToFinalizedMs >= 0).Select(x => x.SpeechEndToFinalizedMs), 95) <= 1700);
        Assert.True(SessionMetrics.Percentile(cache.Select(x => x.FinalizedToFirstBulletMs), 95) <= 60);
        Assert.True(SessionMetrics.Percentile(llm.Select(x => x.FinalizedToFirstBulletMs), 95) <= 700 + 300 + 200);
    }

    /// <summary>
    /// RC2 production-shaped soak: 110+ heard questions on a virtual clock interleaved with the noise that broke RC1 —
    /// Windows beeps and screenshot shutters "transcribed" as the recognizer's own prompt, remarks addressed to another
    /// panel member, variants on earlier history items, AI outages, a transcription reconnect and three pause/resume
    /// cycles with typed questions. Invariants: no suppressed turn reaches memory, history or the report; one heard
    /// question → one record; variants never add entries; the session stays one continuous session; bounded memory.
    /// </summary>
    [Fact]
    public async Task Rc2SoakWithBeepsPanelVariantsOutagesAndPausesKeepsOnlyRealQuestions()
    {
        var kb = TestKnowledge.Load();
        var echo = kb.Context.BuildTranscriptionPrompt();
        VirtualLatencyProvider provider = null!;
        var s = new InterviewSimulator(kb, provider: c => provider = new VirtualLatencyProvider(c));
        var e = s.E;
        await e.StartListeningAsync();
        var rnd = new Random(11);
        var subjects = new[] { "the rewards ledger", "VIP onboarding", "the referral programme", "card cashback", "the Dubai launch", "the KYC vendor",
                               "the support backlog", "token utility", "the loyalty tiers", "the partner API", "fraud monitoring",
                               "the mobile wallet", "merchant payouts", "the savings product", "chargebacks", "the pricing page" };   // 160 distinct questions
        var asks = new[] { "How would you prioritise work on {0}?", "What metrics would you use to judge {0}?", "How would you explain {0} to the CEO?",
                           "What risks do you see with {0}?", "How would you test a change to {0}?", "Who would you involve first for {0}?", "How would you cut the scope of {0}?",
                           "What would you do in your first month on {0}?", "How would you handle pushback from Compliance on {0}?", "How would you measure adoption of {0}?" };
        int heard = 0, typed = 0, beeps = 0, shutters = 0, panel = 0, variants = 0, pauses = 0;
        var usedQuestions = new HashSet<string>();
        string NextQuestion()
        {
            while (true)
            {
                var q = string.Format(asks[rnd.Next(asks.Length)], subjects[rnd.Next(subjects.Length)]);
                if (usedQuestions.Add(q)) return q;
            }
        }
        GC.Collect(); GC.WaitForPendingFinalizers();
        long memStart = GC.GetTotalMemory(true);

        for (int i = 1; i <= 112; i++)
        {
            if (i == 70) { s.T.Raise(ConnectionStatus.Reconnecting, "simulated drop"); await s.RunAsync(1500); s.T.Raise(ConnectionStatus.Connected, null); }
            if (i is 40 or 41) provider.Throw = new ProviderException(ProviderErrorKind.ServiceUnavailable, "503") { Code = "ai_unavailable" };
            if (i == 42) provider.Throw = null;
            if (i is 30 or 65 or 95)
            {
                pauses++;
                e.Pause();
                s.Sound(InterviewSimulator.SoundKind.Beep, echo);            // a beep while paused is never even heard
                await s.RunAsync(20_000);
                var t = e.SubmitManualQuestionAsync(NextQuestion());
                while (!t.IsCompleted) await s.RunAsync(20);
                await t; typed++;
                await e.StartListeningAsync();
            }
            if (i % 6 == 0) { beeps++; s.Sound(InterviewSimulator.SoundKind.Beep, echo); await s.RunAsync(3000); }
            if (i % 9 == 0) { shutters++; s.Sound(InterviewSimulator.SoundKind.Shutter, "Thank you."); await s.RunAsync(3000); }
            if (i % 11 == 0) { panel++; s.Say(new[] { "John, what do you think about that?", "What's your view on the rollout, Sarah?" }[panel % 2]); await s.RunAsync(6000); await s.SettleAsync(); }

            heard++;
            if (rnd.NextDouble() < 0.2) s.Say(NextQuestion().TrimEnd('?') + " for VIP clients?", 600, "And which metrics would you track to prove it is working?");
            else s.Say(NextQuestion());
            await s.RunAsync(9000);
            await s.RunAsync(15_000);   // candidate answers; interviewer silent
            await s.SettleAsync();

            if (i % 10 == 0 && e.History.Count > 3)
            {
                var before = e.History.Count;
                var target = e.History[rnd.Next(e.History.Count - 1)];
                var vt = e.RequestVariantAsync(target.Id, new[] { AnswerStyle.VeryShort, AnswerStyle.Technical, AnswerStyle.Example }[variants % 3]);
                while (!vt.IsCompleted) await s.RunAsync(20);
                await vt; variants++;
                Assert.Equal(before, e.History.Count);
            }
        }
        await e.StopAsync();
        GC.Collect(); GC.WaitForPendingFinalizers();
        long memEnd = GC.GetTotalMemory(true);

        var m = e.Metrics;
        var questions = e.Memory.Questions;
        _out.WriteLine($"heard {heard}, typed {typed}, beeps {beeps + pauses} (paused {pauses}), shutters {shutters}, panel remarks {panel}, variants {variants}, pauses {pauses}");
        _out.WriteLine($"records {questions.Count}, history {e.History.Count}, duplicates {m.DuplicatesSuppressed}, merged {m.TurnsMerged}, AI requests {provider.Requests}, fallback {m.FallbackAnswers}, api errors {m.ApiErrors}");
        _out.WriteLine("suppressed: " + string.Join(", ", m.Suppression.Select(kv => $"{kv.Key} {kv.Value}")));
        _out.WriteLine($"managed memory start {memStart / 1024} KB end {memEnd / 1024} KB");

        Assert.True(heard + typed >= 100);
        Assert.Equal(heard + typed - m.DuplicatesSuppressed, questions.Count);   // one real question → one record
        Assert.Equal(questions.Count, e.Metrics.Questions);
        Assert.Equal(Math.Min(questions.Count, e.Options.HistoryCapacity), e.History.Count);   // variants add nothing
        Assert.Equal(variants, m.Variants);
        Assert.Equal(panel, m.OtherDirectedSuppressed);
        Assert.True(m.TotalSuppressed >= beeps + shutters, string.Join(", ", m.Suppression));
        Assert.DoesNotContain(questions, q => q.Original.Contains("Terms:", StringComparison.Ordinal) || q.Original.StartsWith("Thank you", StringComparison.Ordinal)
                                              || q.Original.Contains("John,", StringComparison.Ordinal) || q.Original.Contains("Sarah", StringComparison.Ordinal));
        Assert.Equal(typed, questions.Count(q => q.Typed));
        Assert.Equal(1, m.Reconnects);
        Assert.True(provider.Requests <= heard + typed + variants, $"{provider.Requests} AI requests");
        Assert.True(m.FallbackAnswers + m.ApiErrors >= 2);

        var report = InterviewAssistant.Core.Reports.ReportBuilder.Build(e.Memory, kb, m, "soak", DateTime.UtcNow.AddHours(-1), DateTime.UtcNow);
        Assert.Equal(questions.Count, report.Questions.Count);
        Assert.Equal(m.TotalSuppressed, report.ExcludedTurns);           // disclosed as "not counted", never listed as questions
        Assert.True(memEnd - memStart < 8 * 1024 * 1024, $"memory grew {(memEnd - memStart) / 1024} KB");
    }
}
