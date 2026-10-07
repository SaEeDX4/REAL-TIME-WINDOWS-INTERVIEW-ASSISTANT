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
        var clock = new InterviewAssistant.Core.Diagnostics.ManualClock();
        var e = new InterviewEngine(pack.ToKnowledgeBase(), null, new FakeProvider(), new EngineOptions { AutoTick = false }, clock);
        await e.SubmitManualQuestionAsync("Tell me about yourself.");
        clock.Advance(1500); e.OnInterviewerSpeechStarted();
        await e.SubmitManualQuestionAsync("Tell me about your experience at Arzif Crypto Exchange.");
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
}
