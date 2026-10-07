using System.Diagnostics;
using System.Text.Json;
using InterviewAssistant.Core.Intelligence;
using Xunit;
using Xunit.Abstractions;

namespace InterviewAssistant.Tests;

public class MatcherTests
{
    private readonly ITestOutputHelper _out;
    public MatcherTests(ITestOutputHelper o) => _out = o;

    private static List<(string Utterance, string? Expected, string Kind)> LoadTests()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestKnowledge.Dir, "test_questions.json")));
        return doc.RootElement.GetProperty("tests").EnumerateArray()
            .Select(t => (t.GetProperty("utterance").GetString()!, t.GetProperty("expected_question_id").GetString(), t.GetProperty("kind").GetString()!))
            .ToList();
    }

    [Fact]
    public void CanonicalQuestionsMatchThemselvesWithHighConfidence()
    {
        var kb = TestKnowledge.Load();
        var m = new QuestionMatcher(kb.Questions);
        foreach (var q in kb.Questions)
        {
            var r = m.Match(q.CanonicalQuestion);
            Assert.Equal(q.QuestionId, r.Question?.QuestionId);
            Assert.True(r.Confidence >= MatchConfidence.Medium, $"{q.QuestionId} {r.Score:0.00}");
        }
    }

    [Fact]
    public void SpokenParaphrasesMatchTop1()
    {
        var kb = TestKnowledge.Load();
        var m = new QuestionMatcher(kb.Questions);
        var tests = LoadTests().Where(t => t.Kind == "paraphrase").ToList();
        int ok = 0, high = 0, wrongHigh = 0;
        foreach (var (u, exp, _) in tests)
        {
            var r = m.Match(TextNormalizer.CleanTranscript(u, TestKnowledge.Load().Context.Aliases));
            if (r.Question?.QuestionId == exp) { ok++; if (r.Confidence == MatchConfidence.High) high++; }
            else
            {
                if (r.Confidence == MatchConfidence.High) wrongHigh++;
                _out.WriteLine($"MISS '{u}' -> {r.Question?.CanonicalQuestion} ({r.Score:0.00} {r.Confidence}) expected {exp}");
            }
        }
        var acc = ok / (double)tests.Count;
        _out.WriteLine($"Top-1 accuracy {acc:P1} ({ok}/{tests.Count}); high-confidence hits {high}; wrong-but-high {wrongHigh}");
        Assert.True(acc >= 0.9, $"accuracy {acc:P1}");
        Assert.True(wrongHigh <= 2, $"wrong high-confidence matches: {wrongHigh}");
    }

    [Fact]
    public void UnexpectedQuestionsAreNotServedFromCache()
    {
        var kb = TestKnowledge.Load();
        var m = new QuestionMatcher(kb.Questions);
        var tests = LoadTests().Where(t => t.Kind == "unexpected").ToList();
        int high = 0;
        foreach (var (u, _, _) in tests)
        {
            var r = m.Match(u);
            _out.WriteLine($"'{u}' -> {r.Question?.CanonicalQuestion} {r.Score:0.00} {r.Confidence}");
            if (r.Confidence == MatchConfidence.High) high++;
        }
        Assert.True(high <= 1, $"{high} unexpected questions hit the cache with high confidence");
    }

    [Theory]
    [InlineData("How would you prioritize improvements to the XAB rewards ecosystem?", "How would you prioritize improvements to the XAB rewards ecosystem?")]
    [InlineData("So tell me a little bit about yourself.", "Tell me about yourself.")]
    [InlineData("how would you stop duplicate transactions in the ledger", "How would you prevent duplicate financial transactions?")]
    [InlineData("What's your experience with Jira?", "What is your experience with Jira and Agile?")]
    [InlineData("why teroxx", "Why do you want to work at Teroxx?")]
    public void KeyQuestionsMatch(string spoken, string expected)
    {
        var kb = TestKnowledge.Load();
        var r = new QuestionMatcher(kb.Questions).Match(TextNormalizer.CleanTranscript(spoken, TestKnowledge.Load().Context.Aliases));
        Assert.Equal(expected, r.Question?.CanonicalQuestion);
    }

    [Fact]
    public void MatchingIsFast()
    {
        var kb = TestKnowledge.Load();
        var m = new QuestionMatcher(kb.Questions);
        m.Match("warm up");
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 200; i++) m.Match("How would you design VIP tier governance for XAB holders and measure it?");
        var perMatch = sw.Elapsed.TotalMilliseconds / 200;
        _out.WriteLine($"{perMatch:0.000} ms per match");
        Assert.True(perMatch < 15);
    }
}
