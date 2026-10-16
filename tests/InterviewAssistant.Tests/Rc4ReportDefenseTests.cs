using InterviewAssistant.Core.Audio;
using InterviewAssistant.Core.Intelligence;
using InterviewAssistant.Core.Live;
using InterviewAssistant.Core.Orchestration;
using InterviewAssistant.Core.Reports;
using Xunit;

namespace InterviewAssistant.Tests;

/// <summary>
/// RC4 report defence in depth: the engine is the first line (non-speech never becomes a question), the report is the
/// second — a heard question whose recorded audio evidence fails the acoustic checks is listed separately for review and
/// excluded from every statistic. Never silently removed, and real questions are never touched.
/// </summary>
public class Rc4ReportDefenseTests
{
    // What the RC3 failure looked like: a low alert tone (steady pitch) heard as an interview question.
    private static readonly SpeechEvidence AlertTone = new(18, 1800, 600, 400, 0, PitchPairs: 17, SteadyPairs: 14, JumpPairs: 2, WindowMs: 1800);
    private static readonly SpeechEvidence RealQuestion = new(34, 3400, 2100, 1600, 0, PitchPairs: 48, SteadyPairs: 14, JumpPairs: 15, WindowMs: 3400);

    private static InterviewReport Build(SessionMemory m) =>
        ReportBuilder.Build(m, TestKnowledge.Load(), null, "s", DateTime.UtcNow.AddMinutes(-20), DateTime.UtcNow, "en");

    private static QuestionRecord Heard(string text, SpeechEvidence e, string category = "BEHAVIORAL") =>
        new() { Original = text, Category = category, Mode = "Verified", Source = "Llm", FirstBulletLatencyMs = 900, Heard = HeardEvidence.From(e, SpeechPlausibility.Assess(text, e)) };

    [Fact]
    public void HeardQuestionWithoutAVoice_IsListedSeparately_AndExcludedFromStatistics()
    {
        var m = new SessionMemory();
        m.Add(Heard("What would you do in your first ninety days?", RealQuestion, "PLANNING"));
        m.Add(Heard("What is your experience with FinTech products?", AlertTone, "FINTECH"));
        m.Add(Heard("How do you prioritise a backlog?", RealQuestion, "PRIORITIZATION"));
        var r = Build(m);

        Assert.Equal(2, r.Questions.Count);
        Assert.DoesNotContain(r.Questions, q => q.OriginalWording.Contains("FinTech"));
        var u = Assert.Single(r.UncertainQuestions);
        Assert.Equal(2, u.Index);                                    // keeps its live number: the gap in Q1, Q3 is explained
        Assert.Equal("the audio did not behave like a voice", u.UncertainReason);
        Assert.DoesNotContain("FINTECH", r.CategoryCounts.Keys);   // analytics are not contaminated
        Assert.Equal(2, r.CategoryCounts.Values.Sum());

        var html = HtmlReportRenderer.Render(r);
        Assert.Contains("Possibly not interviewer speech (not counted) (1)", html);
        Assert.Contains("Q2. <bdi dir=\"auto\">What is your experience with FinTech products?</bdi>", html);
        Assert.Contains("Questions asked (2)", html);
        var text = HtmlReportRenderer.RenderText(r);
        Assert.Contains("Possibly not interviewer speech (not counted) (1)", text);
    }

    [Fact]
    public void ContextHallucinationOverAShortVoice_IsListedAsSuch()
    {
        // A voice-like knock (360 ms voiced) cannot have said a 13-syllable interview question.
        var knock = new SpeechEvidence(19, 1900, 620, 360, 40, PitchPairs: 11, SteadyPairs: 2, JumpPairs: 6, WindowMs: 1900);
        var m = new SessionMemory();
        m.Add(Heard("What is your experience with FinTech products?", knock, "FINTECH"));
        var r = Build(m);
        Assert.Empty(r.Questions);
        Assert.Single(r.UncertainQuestions);
        Assert.NotEqual("", r.UncertainQuestions[0].UncertainReason);
    }

    [Theory]
    [InlineData("Why?")]
    [InlineData("Why.")]
    [InlineData("How so?")]
    [InlineData("Metrics?")]
    public void ShortRealFollowUp_StaysAQuestion(string text)
    {
        var why = new SpeechEvidence(15, 1500, 400, 200, 0, PitchPairs: 4, SteadyPairs: 1, WindowMs: 1500);
        var m = new SessionMemory();
        m.Add(Heard(text, why));
        var r = Build(m);
        Assert.Single(r.Questions);
        Assert.Empty(r.UncertainQuestions);
    }

    [Fact]
    public void TypedAndLegacyRecords_AreNeverReclassified()
    {
        var m = new SessionMemory();
        m.Add(new QuestionRecord { Original = "What is your experience with FinTech products?", Category = "FINTECH", Typed = true });
        m.Add(new QuestionRecord { Original = "Tell me about a conflict in your team.", Category = "BEHAVIORAL" });   // rc.3 record: no evidence kept
        var r = Build(m);
        Assert.Equal(2, r.Questions.Count);
        Assert.Empty(r.UncertainQuestions);
    }

    [Fact]
    public void UnheardSegment_AbstainsRatherThanFlagging()
    {
        // Evidence lost in a reconnect is "unheard": the acoustic checks abstain and the question stays.
        var m = new SessionMemory();
        m.Add(Heard("How would you design a closed-loop internal token ledger?", SpeechEvidence.Unheard));
        Assert.Single(Build(m).Questions);
    }

    [Fact]
    public void HeardEvidence_RoundTripsThroughJson()
    {
        var m = new SessionMemory();
        m.Add(Heard("What would you do in your first ninety days?", RealQuestion));
        var json = System.Text.Json.JsonSerializer.Serialize(m.Questions[0]);
        Assert.DoesNotContain("What would you do", json.Replace("What would you do in your first ninety days?", ""));   // only the wording itself, no audio
        var back = System.Text.Json.JsonSerializer.Deserialize<QuestionRecord>(json)!;
        Assert.Equal(RealQuestion, back.Heard!.ToEvidence());
        Assert.Equal("Strong", back.Heard.Support);
    }

    [Fact]
    public async Task Engine_RecordsAcousticProvenance_ForHeardQuestionsOnly()
    {
        var s = new InterviewSimulator(options: new EngineOptions { AutoTick = false });
        await s.StartAsync();
        s.Say("What would you do in your first ninety days?");
        await s.RunAsync(6000);
        await s.SettleAsync();
        await s.E.SubmitManualQuestionAsync("How do you handle conflicting stakeholder priorities?");
        await s.SettleAsync();
        var heard = Assert.Single(s.E.Memory.Questions, q => !q.Typed);
        Assert.NotNull(heard.Heard);
        Assert.True(heard.Heard!.VoicedMs > 0);
        Assert.Equal("Strong", heard.Heard.Support);
        Assert.Null(Assert.Single(s.E.Memory.Questions, q => q.Typed).Heard);
        var r = ReportBuilder.Build(s.E.Memory, TestKnowledge.Load(), s.E.Metrics, "s", DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow, "en");
        Assert.Equal(2, r.Questions.Count);
        Assert.Empty(r.UncertainQuestions);
    }
}
