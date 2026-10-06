using InterviewAssistant.Core.Intelligence;
using InterviewAssistant.Core.Knowledge;
using InterviewAssistant.Core.Orchestration;
using InterviewAssistant.Core.Providers;
using Xunit;
using Xunit.Abstractions;

namespace InterviewAssistant.Tests;

/// <summary>
/// Optional LIVE evaluation against the real OpenAI API. Runs only when OPENAI_API_KEY is set
/// (e.g. locally or as a CI secret); otherwise it reports "skipped" and passes.
/// Sends unexpected / gap / hypothetical questions through the full engine and checks bullet format,
/// length, unsupported historical claims and unverified numbers.
/// </summary>
public class LiveEvaluationTests
{
    private readonly ITestOutputHelper _out;
    public LiveEvaluationTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public async Task LiveAnswersMeetFormatAndTruthRules()
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) { _out.WriteLine("SKIPPED: OPENAI_API_KEY not set"); return; }
        var model = Environment.GetEnvironmentVariable("IA_ANSWER_MODEL") ?? "gpt-4.1-mini";
        var kb = TestKnowledge.Load();
        using var provider = new OpenAiChatAnswerProvider(() => key, new ChatProviderOptions { Model = model });
        var engine = new InterviewEngine(kb, null, provider, new EngineOptions { AutoTick = false, UseFastCache = false });
        var validator = new FactValidator(kb.Profile);
        var questions = new[]
        {
            "How would you migrate XAB balances from on-chain to an internal ledger without losing integrity?",
            "Have you personally owned a wallet architecture before?",
            "What would you do if a whale tries to game the VIP tier rules?",
            "How would you measure whether cashback paid in XAB is worth its cost?",
            "Tell me about your experience at Arzif.",
            "If you had to cut half the roadmap tomorrow, what would you keep?",
        };
        int problems = 0;
        foreach (var q in questions)
        {
            await engine.SubmitManualQuestionAsync(q);
            var a = engine.Current!;
            var r = validator.Validate(a.Bullets, a.Mode);
            var s = engine.Metrics.Samples.Last();
            _out.WriteLine($"\nQ: {q}\n[{a.Mode}] first bullet {s.FinalizedToFirstBulletMs} ms, total {s.FinalizedToCompleteMs} ms, {r.WordCount} words");
            foreach (var b in a.Bullets) _out.WriteLine("  • " + b);
            foreach (var i in r.Issues.Concat(a.ValidationFlags.Select(f => new ValidationIssue("FLAG", f, -1)))) { _out.WriteLine("  ! " + i.Kind + " " + i.Detail); }
            if (a.Bullets.Count is < 2 or > 4) problems++;
            if (r.HasFactualIssues) problems++;
            if (q.StartsWith("Have you personally") && a.Mode != AnswerMode.Bridge) problems++;
        }
        Assert.True(problems <= 1, $"{problems} live answers violated format/truth rules");
    }
}
