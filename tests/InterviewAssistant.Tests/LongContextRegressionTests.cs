using InterviewAssistant.Core.Orchestration;
using Xunit;
using Xunit.Abstractions;

namespace InterviewAssistant.Tests;

/// <summary>
/// RC4 regression guard for long-context extraction while the acoustic gate got stricter: 30 s, 60 s and 2 min of
/// interviewer background (one segment per sentence group, natural pauses), then a multi-part question, a rephrase, an
/// interruption or a continuation. Exactly one answer, the right core ask, and the constraints kept as context.
/// </summary>
public class LongContextRegressionTests
{
    private readonly ITestOutputHelper _out;
    public LongContextRegressionTests(ITestOutputHelper o) => _out = o;

    private static readonly string[] Background =
    {
        "Our reward program grew quickly over the last two years across three markets.",
        "Most of the growth came from the cashback tiers and the referral campaign.",
        "Engineering capacity is limited to one squad for the next two quarters.",
        "Compliance is worried about how the token rewards are presented to retail users.",
        "Retention after the first month dropped from forty to around thirty percent.",
        "The VIP tiers are popular but expensive and hard to explain in the app.",
        "Marketing wants a new campaign before the end of the year.",
        "Support tickets about missing rewards doubled since the summer release.",
    };

    /// <summary>Schedules ~<paramref name="seconds"/> of background speech: sentences in segments of three, 1.2 s apart.</summary>
    private static void SayBackground(InterviewSimulator s, int seconds, out int spokenMs)
    {
        var parts = new List<object>();
        int ms = 0, i = 0;
        while (ms < seconds * 1000)
        {
            var sentence = Background[i++ % Background.Length];
            parts.Add(sentence);
            var dur = Math.Max(600, Math.Max(sentence.Split(' ').Length * s.MsPerWord, Core.Intelligence.SpeechPlausibility.Syllables(sentence) * s.MsPerSyllable));
            var pause = i % 3 == 0 ? 1200 : 250;   // a breath inside a segment, a real pause between groups
            parts.Add(pause);
            ms += dur + pause;
        }
        spokenMs = ms;
        s.Say(parts.ToArray());
    }

    private async Task<InterviewSimulator> RunAsync(int seconds, params object[] question)
    {
        var s = new InterviewSimulator(options: new EngineOptions { AutoTick = false, UseFastCache = false });
        s.E.Log += l => _out.WriteLine("  engine: " + l);
        await s.StartAsync();
        SayBackground(s, seconds, out var ms);
        await s.RunAsync(ms);
        s.Say(question);
        await s.RunAsync(question.OfType<string>().Sum(p => p.Split(' ').Length) * 400 + question.OfType<int>().Sum() + 6000);
        await s.SettleAsync();
        foreach (var st in s.Started) _out.WriteLine($"  started #{st.Id} turn {st.TurnId} replaces {st.ReplacesAnswerId}: '{st.Question}'");
        return s;
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    public async Task Background_ThenTwoPartQuestion_OneAnswerWithBothPartsAndTheConstraints(int seconds)
    {
        var s = await RunAsync(seconds, "So if you owned this product, what would you change first, and how would you measure whether it worked?");
        var a = Assert.Single(s.E.History);
        _out.WriteLine($"{seconds}s: '{a.Question}' | context '{a.ContextSummary}' | requests {s.P.Requests}");
        Assert.Contains("what would you change first", a.Question, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("how would you measure", a.Question, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, a.SubQuestions.Count);
        Assert.Equal(1, s.P.Requests);
        var prompt = s.P.Calls.Single()[1].Content;
        Assert.Contains("Engineering capacity is limited", prompt);
        Assert.DoesNotContain("what would you change first", a.ContextSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(120)]
    public async Task Background_ThenRephrasedAsk_TheRestatedAskWins(int seconds)
    {
        var s = await RunAsync(seconds, "How would you rebuild the whole rewards roadmap for next year across every market — sorry, let me put it differently: what would you cut first if capacity halved?");
        Assert.Equal("What would you cut first if capacity halved?", Assert.Single(s.E.History).Question);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    public async Task Background_InterruptedByAnAside_StillOneAnswerForTheRealQuestion(int seconds)
    {
        var s = await RunAsync(seconds, "Sorry, one second, my colleague just joined.", 2000, "Okay. Given all that, how would you win back the users who stopped after the first month?");
        var a = Assert.Single(s.E.History);
        Assert.Contains("how would you win back the users", a.Question, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("colleague", a.Question);
        Assert.Equal(1, s.P.Requests);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public async Task Background_QuestionThenQuickContinuation_IsOneQuestion(int seconds)
    {
        var s = await RunAsync(seconds, "How would you increase retention", 700, "and which metrics would you track to prove it?");
        var a = Assert.Single(s.E.History);
        Assert.Contains("increase retention", a.Question);
        Assert.Contains("which metrics", a.Question);
    }
}
