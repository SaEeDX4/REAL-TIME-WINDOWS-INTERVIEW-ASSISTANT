using InterviewAssistant.Core.Intelligence;
using Xunit;
using Xunit.Abstractions;

namespace InterviewAssistant.Tests;

/// <summary>Novel phrasings NOT present in the bank. Measures honest generalisation and, critically, that wrong matches are rarely High-confidence (which would show a wrong cached answer).</summary>
public class HeldOutMatcherTests
{
    private readonly ITestOutputHelper _out;
    public HeldOutMatcherTests(ITestOutputHelper o) => _out = o;

    public static readonly (string Spoken, string ExpectedCanonical)[] Cases =
    {
        ("Could you start by giving us a short overview of who you are professionally?", "Tell me about yourself."),
        ("What made you apply for this position with us?", "Why do you want to work at Teroxx?"),
        ("What makes you a stronger candidate than the others we're interviewing?", "Why should we hire you?"),
        ("You've mostly done sales and marketing, so why product now?", "Why do you want to become a Product Owner?"),
        ("What exactly did you do at the crypto exchange?", "Tell me about your crypto experience."),
        ("How did you manage to get to sixty thousand traders?", "How did you grow Arzif to 60,000 active traders?"),
        ("If you joined, what would you focus on in your first three months?", "What would you do in your first 90 days?"),
        ("How would you get more of our clients to actually use the XAB token?", "How would you increase XAB adoption?"),
        ("Which KPIs would tell you whether the token program is working?", "What metrics would you use to measure success of XAB?"),
        ("How should we decide which reward features to build first?", "How would you prioritize improvements to the XAB rewards ecosystem?"),
        ("How would you stop the same payout being credited twice?", "How would you prevent duplicate financial transactions?"),
        ("What happens when the balances in two systems don't agree, how do you reconcile them automatically?", "How would you design an automated reconciliation process?"),
        ("How would you make sure we never exceed the token cap?", "How would you ensure token supply compliance?"),
        ("Walk me through how you'd own our internal token ledger.", "How would you approach an internal XAB ledger?"),
        ("How would you fix a wrong entry in the ledger?", "How would you handle a reversal or correction in the ledger?"),
        ("What's your understanding of the MiCA regulation and its impact here?", "What do you know about MiCA and how does it affect this product?"),
        ("Legal says your feature can't go live. What now?", "What would you do if Compliance blocks a feature?"),
        ("How do you decide what goes to the top of the backlog?", "How do you prioritize the backlog?"),
        ("The CEO walks in and wants a new feature by Friday. How do you react?", "How do you handle an urgent request from the C-level?"),
        ("Your developers push back on a requirement. How do you handle that?", "What do you do when Engineering disagrees with you?"),
        ("How do you think about paying down tech debt versus shipping new things?", "How do you balance technical debt against new features?"),
        ("How do you use Jira in your daily work?", "What is your experience with Jira and Agile?"),
        ("How would you structure the VIP levels and who qualifies?", "How would you design VIP tier governance?"),
        ("What could go wrong with a token rewards program?", "What risks do you see in a token rewards ecosystem?"),
        ("Tell me about a time you used analytics to drive a decision.", "Tell me about a time you used data to make a decision."),
        ("Talk to me about a launch you were responsible for.", "Tell me about a product launch you led."),
        ("How would you describe yourself as a manager of people?", "Tell me about your leadership style."),
        ("What would you like to ask us?", "Do you have any questions for us?"),
        ("Explain what a closed loop token economy means.", "What is a closed-loop token economy?"),
        ("How do you write acceptance criteria for a payout story?", "How would you write acceptance criteria for a reward payout feature?"),
    };

    [Fact]
    public void HeldOutParaphraseAccuracy()
    {
        var m = new QuestionMatcher(TestKnowledge.Load().Questions);
        int ok = 0, okHigh = 0, wrongHigh = 0;
        foreach (var (spoken, exp) in Cases)
        {
            var r = m.Match(TextNormalizer.CleanTranscript(spoken, TestKnowledge.Load().Context.Aliases));
            bool hit = r.Question?.CanonicalQuestion == exp;
            if (hit) { ok++; if (r.Confidence == MatchConfidence.High) okHigh++; }
            else if (r.Confidence == MatchConfidence.High) wrongHigh++;
            _out.WriteLine($"{(hit ? "OK  " : "MISS")} {r.Score:0.00} {r.Confidence,-6} '{spoken}' -> {r.Question?.CanonicalQuestion}");
        }
        _out.WriteLine($"Held-out top-1 {ok}/{Cases.Length} ({ok * 100.0 / Cases.Length:0}%), served-from-cache (High) correct {okHigh}, wrong-but-High {wrongHigh}");
        Assert.True(ok >= Cases.Length * 0.7, $"held-out accuracy too low: {ok}/{Cases.Length}");
        Assert.True(wrongHigh <= 1, $"wrong high-confidence cache hits: {wrongHigh}");
    }
}
