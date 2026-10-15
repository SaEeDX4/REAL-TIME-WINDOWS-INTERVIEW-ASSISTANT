using InterviewAssistant.Core.Languages;
using InterviewAssistant.Core.Live;
using InterviewAssistant.Core.Orchestration;
using InterviewAssistant.Core.Reports;
using Xunit;

namespace InterviewAssistant.Tests;

public class ReportTests
{
    private static async Task<(InterviewEngine E, InterviewReport R)> RunSessionAsync(string reportLang = "en")
    {
        var pack = await Fixtures.ShervinPackAsync();
        var s = new InterviewSimulator(pack.ToKnowledgeBase());
        var e = s.E;
        await e.StartListeningAsync();
        // Heard questions; the second cuts in right after the first answer appeared (a likely interruption).
        s.Say("Tell me about yourself."); await s.RunAsync(6000); await s.SettleAsync();
        s.Say("Tell me about your experience at Arzif Crypto Exchange."); await s.RunAsync(7000); await s.SettleAsync();
        await e.SubmitManualQuestionAsync("Have you personally worked with building or owning financial ledgers?");
        await e.SubmitManualQuestionAsync("Und wie würden Sie das mit den Stakeholdern abstimmen?");
        await e.SubmitManualQuestionAsync("<script>alert('x')</script> Why?");
        var r = ReportBuilder.Build(e.Memory, e.Knowledge, e.Metrics, "s1", DateTime.UtcNow.AddMinutes(-30), DateTime.UtcNow, reportLang, pack, reconnects: 1, profileLabel: "Shervin", targetLabel: "PO · Teroxx");
        return (e, r);
    }

    [Fact]
    public async Task ReportContainsRequiredSectionsAndHonestyLabel()
    {
        var (_, r) = await RunSessionAsync();
        Assert.Equal(5, r.Questions.Count);
        Assert.False(r.CandidateMicrophoneEnabled);
        Assert.Contains(r.GapQuestions, g => g.Contains("ledgers"));
        Assert.Contains(r.DifficultyAreas, d => d.StartsWith("Experience gap"));
        Assert.Single(r.Interruptions);
        Assert.Contains("de", r.LanguagesUsed);
        Assert.NotEmpty(r.StoriesRecommended);
        Assert.NotEmpty(r.TopicsNotCovered);
        Assert.Contains(r.TranscriptionIssues, t => t.Contains("reconnect"));
        Assert.True(r.LatencyP50Ms >= 0);
        Assert.NotEmpty(r.ThankYouThemes);
        var html = HtmlReportRenderer.Render(r);
        Assert.Contains("microphone was OFF", html);
        Assert.Contains("not an assessment", html);
        Assert.DoesNotContain("success probability", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("(Inference)", html);
    }

    [Fact]
    public async Task HtmlEscapesInterviewerContent()
    {
        var (_, r) = await RunSessionAsync();
        var html = HtmlReportRenderer.Render(r);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("default-src 'none'", html);
    }

    [Theory]
    [InlineData("fa", "rtl", "گزارش مصاحبه")]
    [InlineData("ar", "rtl", "تقرير المقابلة")]
    [InlineData("de", "ltr", "Interviewbericht")]
    [InlineData("zh", "ltr", "面试报告")]
    public async Task ReportCanBeGeneratedInAnotherLanguageWithRtl(string lang, string dir, string title)
    {
        var (_, r) = await RunSessionAsync(lang);
        var html = HtmlReportRenderer.Render(r);
        Assert.Contains($"dir=\"{dir}\"", html);
        Assert.Contains(title, html);
        Assert.Contains("Tell me about yourself.", html);  // original wording preserved
        Assert.Contains("<bdi", html);                     // mixed-direction content isolated
    }

    [Fact]
    public void ReportLabelsAreCompleteForAllTenLanguages()
    {
        foreach (var l in LanguageRegistry.All) Assert.True(ReportLabels.IsComplete(l.Code), l.Code);
    }

    [Fact]
    public async Task ReportSerializesForStorageAndExport()
    {
        var (_, r) = await RunSessionAsync();
        var json = r.ToJson();
        Assert.Contains("\"Questions\"", json);
        Assert.Contains("CandidateMicrophoneEnabled", json);
    }

    [Fact]
    public async Task StoredReportRoundTripsToIdenticalHtml()
    {
        var (_, r) = await RunSessionAsync("fa");
        var back = ReportSerializer.FromJson(r.ToJson());
        Assert.NotNull(back);
        Assert.Equal(r.Questions.Count, back!.Questions.Count);
        Assert.Equal(HtmlReportRenderer.Render(r), HtmlReportRenderer.Render(back));
        Assert.Null(ReportSerializer.FromJson("{not json"));
        Assert.Null(ReportSerializer.FromJson(""));
    }
}

public class Rc2ReportTests
{
    [Fact]
    public void HeaderNeverRepeatsTheCandidateName()
    {
        Assert.Equal("Shervin · Product Owner · Teroxx", HtmlReportRenderer.Header("Shervin", "Shervin · Product Owner · Teroxx"));
        Assert.Equal("Shervin Fallahdoust · PO · Teroxx", HtmlReportRenderer.Header("Shervin Fallahdoust", "Shervin · PO · Teroxx"));
        Assert.Equal("Maria · PO", HtmlReportRenderer.Header("", "Maria · PO"));
    }

    [Fact]
    public async Task EchoRecordsNeverCountAndCopyTextHasSeparators()
    {
        var kb = TestKnowledge.Load();
        var e = new InterviewEngine(kb, null, new FakeProvider(), new EngineOptions { AutoTick = false }, new InterviewAssistant.Core.Diagnostics.ManualClock());
        await e.SubmitManualQuestionAsync("How would you design a closed-loop internal token ledger?");
        // A record that slipped in from an older build (RC1 stored prompt echoes as questions): never counted.
        e.Memory.Add(new QuestionRecord { Original = kb.Context.BuildTranscriptionPrompt(), Category = "MICAR", Mode = "Verified" });
        var r = ReportBuilder.Build(e.Memory, kb, e.Metrics, "s", DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow, "en", null, profileLabel: "Shervin", targetLabel: "Shervin · PO · Teroxx");
        Assert.Single(r.Questions);
        Assert.DoesNotContain("MICAR", r.CategoryCounts.Keys);
        Assert.Equal(1, r.ExcludedTurns);
        var html = HtmlReportRenderer.Render(r);
        Assert.Contains("<h3>Q1. ", html);
        Assert.Contains("Category: ", html);
        Assert.Contains("Difficulty areas <span", html);
        Assert.DoesNotContain("Shervin · Shervin", html);
        var text = HtmlReportRenderer.RenderText(r);
        Assert.Contains("Q1. How would you design a closed-loop internal token ledger?", text);
        Assert.Contains("microphone was OFF", text);
    }

    [Fact]
    public void AllReportLanguagesHaveEveryLabel()
    {
        foreach (var l in ReportLabels.Languages) Assert.True(ReportLabels.IsComplete(l), l);
    }
}
