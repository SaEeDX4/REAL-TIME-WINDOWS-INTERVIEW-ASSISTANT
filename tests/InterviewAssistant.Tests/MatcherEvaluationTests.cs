using InterviewAssistant.Core.Intelligence;
using Xunit;
using Xunit.Abstractions;

namespace InterviewAssistant.Tests;

/// <summary>
/// Larger held-out matcher evaluation (phrasings not in the bank) across Product Owner, crypto, XAB, rewards, VIP,
/// ledger, compliance, prioritization, stakeholders, Agile/Jira, metrics, technical, behavioural, background,
/// hypothetical and direct-experience questions — plus NOVEL questions that must NOT be served from the cache.
/// A false high-confidence match shows a wrong prepared answer instantly, so it is the metric that matters most.
/// </summary>
public class MatcherEvaluationTests
{
    private readonly ITestOutputHelper _out;
    public MatcherEvaluationTests(ITestOutputHelper o) => _out = o;

    public static readonly (string Spoken, string? Expected, string Area)[] Cases =
    {
        ("Give us a quick rundown of your career so far.", "Tell me about yourself.", "background"),
        ("Why are you interested in joining Teroxx specifically?", "Why do you want to work at Teroxx?", "motivation"),
        ("What drew you to product ownership after sales and marketing?", "Why do you want to become a Product Owner?", "background"),
        ("Walk me through what you did in the crypto space.", "Tell me about your crypto experience.", "crypto"),
        ("How did Arzif reach sixty thousand active traders?", "How did you grow Arzif to 60,000 active traders?", "behavioural"),
        ("Share an example of when you improved customer retention.", "Tell me about a time you improved retention.", "behavioural"),
        ("Describe a partnership deal you closed.", "Tell me about a partnership you negotiated.", "behavioural"),
        ("What does your day look like in your current job?", "What do you do in your current role?", "background"),
        ("What are your plans for the first three months here?", "What would you do in your first 90 days?", "XAB"),
        ("What would you tackle first in this role?", "What would be your first priorities in this role?", "XAB"),
        ("How can we make XAB more useful for our clients?", "How would you improve XAB utility?", "XAB"),
        ("How would you get more customers to use XAB?", "How would you increase XAB adoption?", "XAB"),
        ("Which numbers would show that XAB is succeeding?", "What metrics would you use to measure success of XAB?", "metrics"),
        ("How would you build a booster for rewards?", "How would you design a reward booster?", "rewards"),
        ("How do you keep a rewards program from becoming just a cost center?", "How do you make sure rewards are sustainable and not just a cost?", "rewards"),
        ("How would you set up the rules for VIP levels?", "How would you design VIP tier governance?", "VIP"),
        ("How would you get VIP clients more engaged?", "How would you increase engagement of VIP clients?", "VIP"),
        ("What could go wrong with token rewards?", "What risks do you see in a token rewards ecosystem?", "rewards"),
        ("How do you prevent double payouts in the system?", "How would you prevent duplicate financial transactions?", "ledger"),
        ("How would you automate reconciliation between systems?", "How would you design an automated reconciliation process?", "ledger"),
        ("Explain double-entry bookkeeping and why a ledger needs it.", "What is double-entry accounting and why does it matter for a ledger?", "ledger"),
        ("How would you correct a wrong ledger entry?", "How would you handle a reversal or correction in the ledger?", "ledger"),
        ("Which states should a ledger transaction go through?", "What are the transaction states you would define?", "ledger"),
        ("What is a closed loop token economy?", "What is a closed-loop token economy?", "technical"),
        ("Custodial versus non-custodial wallets, what's the difference?", "What is the difference between a custodial and non-custodial wallet?", "technical"),
        ("How is staking different from your rewards scheme?", "What is staking and how is it different from your rewards program?", "technical"),
        ("How does MiCA affect what we're building?", "What do you know about MiCA and how does it affect this product?", "compliance"),
        ("Compliance says no to your feature. What next?", "What would you do if Compliance blocks a feature?", "compliance"),
        ("How do KYC rules shape the product?", "How do KYC and AML affect product design?", "compliance"),
        ("How do you decide the order of the backlog?", "How do you prioritize the backlog?", "prioritization"),
        ("How do you balance tech debt with new features?", "How do you balance technical debt against new features?", "prioritization"),
        ("What makes a user story good?", "How do you write a good user story?", "agile"),
        ("What's your definition of done?", "What is your Definition of Ready and Definition of Done?", "agile"),
        ("How do you run backlog refinement and sprint planning?", "How do you run sprint planning and refinement?", "agile"),
        ("Engineers push back on your plan. How do you respond?", "What do you do when Engineering disagrees with you?", "stakeholder"),
        ("Two departments want conflicting priorities. What do you do?", "Two stakeholders want opposite priorities. How do you decide?", "stakeholder"),
        ("Marketing wants to ship before the developers are ready. How do you handle it?", "Marketing wants to launch before Engineering is ready. What do you do?", "stakeholder"),
        ("How would you lower churn?", "How would you reduce churn?", "metrics"),
        ("How would you test a new reward with an experiment?", "How would you run an experiment to test a new reward?", "metrics"),
        ("Have you personally built a financial ledger before?", "Have you personally built or owned a financial ledger?", "direct-experience"),
        ("Have you worked hands-on with MiCA?", "Have you worked directly with MiCA?", "direct-experience"),
        ("What's your biggest weakness?", "What is your biggest weakness?", "background"),
        ("Any questions for us before we wrap up?", "Do you have any questions for us?", "general"),
        // Novel: nothing prepared — must fall back to AI (never a confident wrong cache hit).
        ("How would you price a premium subscription for institutional clients?", null, "novel"),
        ("What do you think about gamification in banking apps?", null, "novel"),
        ("How would you explain the ledger architecture to the board?", null, "novel"),
        ("How would you evaluate a KYC vendor build versus buy decision?", null, "novel"),
        ("How would you handle a security incident involving leaked API keys?", null, "novel"),
        ("What would you do if a competitor copied our token program?", null, "novel"),
        ("How would you design a referral bonus that does not attract fraud?", null, "novel"),
        ("How would you localize the app for the Turkish market?", null, "novel"),
        ("How would you run a customer advisory board?", null, "novel"),
        ("What's your opinion on stablecoin regulation in the US?", null, "novel"),
        ("How would you measure the success of a mobile push notification campaign?", null, "novel"),
        ("How would you migrate users from one wallet provider to another?", null, "novel"),
    };

    [Fact]
    public void HeldOutParaphrasesAndNovelQuestions()
    {
        var kb = TestKnowledge.Load();
        var m = new QuestionMatcher(kb.Questions);
        int known = 0, top1 = 0, falseHigh = 0, novelTotal = 0, novelFallback = 0, knownFallback = 0;
        foreach (var (spoken, expected, area) in Cases)
        {
            var r = m.Match(TextNormalizer.CleanTranscript(spoken, kb.Context.Aliases));
            var high = r.Confidence == MatchConfidence.High;
            if (expected == null)
            {
                novelTotal++;
                if (!high) novelFallback++; else falseHigh++;
                _out.WriteLine($"{(high ? "FALSE-HIGH" : "ok-AI     ")} {r.Score:0.00} {r.Confidence,-6} [{area}] '{spoken}' -> {r.Question?.CanonicalQuestion}");
                continue;
            }
            known++;
            var hit = r.Question?.CanonicalQuestion == expected;
            if (hit) top1++;
            else if (high) falseHigh++;
            if (!high) knownFallback++;
            _out.WriteLine($"{(hit ? "OK        " : high ? "WRONG-HIGH" : "miss      ")} {r.Score:0.00} {r.Confidence,-6} [{area}] '{spoken}' -> {r.Question?.CanonicalQuestion}");
        }
        _out.WriteLine($"Known: top-1 {top1}/{known} ({top1 * 100.0 / known:0}%), AI fallback rate {knownFallback * 100.0 / known:0}%");
        _out.WriteLine($"Novel: correctly sent to AI {novelFallback}/{novelTotal}");
        _out.WriteLine($"False high-confidence matches (wrong cached answer would be shown): {falseHigh}");

        Assert.True(falseHigh <= 1, $"false high-confidence matches: {falseHigh}");
        Assert.Equal(novelTotal, novelFallback);                       // novel questions are never served a wrong prepared answer
        Assert.True(top1 >= known * 0.7, $"top-1 accuracy too low: {top1}/{known}");
    }
}
