using InterviewAssistant.Core.Intelligence;
using InterviewAssistant.Core.Orchestration;
using InterviewAssistant.Core.Turn;
using Xunit;

namespace InterviewAssistant.Tests;

/// <summary>
/// RC4 review finding: recognizers write "？" (Chinese) and "؟" (Arabic, Persian), and word counts only see Latin
/// letters — a whole Chinese or Arabic question looked like a one-word turn without "?" and was dropped silently.
/// </summary>
public class NonLatinQuestionTests
{
    [Theory]
    [InlineData("你为什么离开上一家公司？")]
    [InlineData("为什么？")]
    [InlineData("لماذا تركت وظيفتك السابقة؟")]
    [InlineData("چرا شغل قبلی خود را ترک کردید؟")]
    [InlineData("Warum haben Sie sich bei uns beworben?")]
    public void QuestionMarkOfAnyScript_IsAQuestion(string text)
    {
        Assert.False(TurnDetector.IsNonQuestion(text), text);
        Assert.True(UtteranceGate.IsQuestionSentence(text), text);
        Assert.True(TextNormalizer.EndsWithQuestionMark(text));
    }

    [Theory]
    [InlineData("你为什么离开上一家公司？")]
    [InlineData("لماذا تركت وظيفتك السابقة؟")]
    [InlineData("چرا شغل قبلی خود را ترک کردید؟")]
    public async Task Engine_AnswersANonLatinQuestion(string question)
    {
        var s = new InterviewSimulator(options: new EngineOptions { AutoTick = false });
        await s.StartAsync();
        s.Say(question);
        await s.RunAsync(8000);
        await s.SettleAsync();
        var a = Assert.Single(s.E.History);
        Assert.Equal(question, a.Question);
    }

    [Fact]
    public void ChineseContextThenQuestion_SplitsIntoSentences()
    {
        var gate = new UtteranceGate(new PromptEchoDetector("Job interview"));
        var x = QuestionExtractor.Extract("我们的团队很小，预算有限。你会先做什么？", "", null, gate);
        Assert.Equal("你会先做什么？", x.CoreQuestion);
        Assert.Contains("预算有限", x.ContextSummary);
    }
}
