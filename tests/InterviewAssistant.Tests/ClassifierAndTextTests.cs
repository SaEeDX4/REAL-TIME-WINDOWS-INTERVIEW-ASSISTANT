using InterviewAssistant.Core.Intelligence;
using InterviewAssistant.Core.Knowledge;
using Xunit;

namespace InterviewAssistant.Tests;

public class ClassifierAndTextTests
{
    [Theory]
    [InlineData("Have you personally built a financial ledger before?", AnswerMode.Bridge)]
    [InlineData("Do you have experience with closed-loop reward systems?", AnswerMode.Bridge)]
    [InlineData("How would you design an internal ledger reconciliation process?", AnswerMode.Hypothetical)]
    [InlineData("How would you prevent duplicate financial transactions?", AnswerMode.Hypothetical)]
    [InlineData("Tell me about your crypto experience.", AnswerMode.Verified)]
    [InlineData("Tell me about yourself.", AnswerMode.Verified)]
    [InlineData("What is MiCA?", AnswerMode.Hypothetical)]
    public void ModeIsClassified(string q, AnswerMode expected) => Assert.Equal(expected, QuestionClassifier.Classify(q).Mode);

    [Theory]
    [InlineData("How would you reconcile the XAB ledger?", "LEDGER")]
    [InlineData("What do you know about MiCAR?", "MICAR")]
    [InlineData("How would you design VIP tiers?", "VIP")]
    [InlineData("Engineering disagrees with your priorities, what do you do?", "PRIORITIZATION")]
    public void CategoryIsClassified(string q, string cat) => Assert.Equal(cat, QuestionClassifier.Classify(q).Category);

    [Theory]
    [InlineData("Why?")]
    [InlineData("And what metric specifically?")]
    [InlineData("Can you give an example?")]
    public void FollowUpsDetected(string q) => Assert.True(QuestionClassifier.Classify(q).IsFollowUp);

    [Theory]
    [InlineData("um so how would you, uh, improve terox's ab locks token", "How would you, improve Teroxx's Abloxx token")]
    [InlineData("what about mica r and cy sec", "What about MiCA and CySEC")]
    [InlineData("explain item potency", "Explain idempotency")]
    public void TranscriptCleanupFixesDomainTerms(string raw, string expected) => Assert.Equal(expected, TextNormalizer.CleanTranscript(raw));

    [Fact]
    public void FingerprintIgnoresPunctuationAndCase()
    {
        Assert.Equal(TextNormalizer.Fingerprint("How would you improve XAB?"), TextNormalizer.Fingerprint("how would you improve xab"));
    }

    [Fact]
    public void BulletParserEmitsOnlyCompleteStableBullets()
    {
        var p = new BulletStreamParser();
        var stream = "MODE: HYPO|THETICAL\n• I'd start| with utility.\n•| Then benefits|.\n• Finally| metrics.";
        var emitted = new List<string>();
        foreach (var chunk in stream.Split('|')) emitted.AddRange(p.Push(chunk));
        Assert.Equal(new[] { "I'd start with utility.", "Then benefits." }, emitted);
        Assert.Equal("Finally metrics.", p.Pending);
        emitted.AddRange(p.Complete());
        Assert.Equal(3, emitted.Count);
        Assert.Equal(AnswerMode.Hypothetical, p.Mode);
    }

    [Fact]
    public void ValidatorFlagsFabricatedNumbersAndClaims()
    {
        var v = new FactValidator(TestKnowledge.Load().Profile);
        var r = v.Validate(new[] { "I built a ledger that processed 47% more transactions for the team.", "I'd define clear rules with Engineering and Compliance." }, AnswerMode.Hypothetical);
        Assert.Contains(r.Issues, i => i.Kind == "UNVERIFIED_NUMBER" && i.Detail.Contains("47"));
        Assert.Contains(r.Issues, i => i.Kind == "UNSUPPORTED_CLAIM");
        Assert.Equal("I'd build a ledger", FactValidator.SoftenClaim("I built a ledger"));
    }

    [Fact]
    public void ValidatorAcceptsVerifiedResumeNumbers()
    {
        var v = new FactValidator(TestKnowledge.Load().Profile);
        var r = v.Validate(new[] { "At Arzif we grew to 60,000 active traders and revenue by 60% over three years.", "That raised satisfaction by twenty-five percent and cut downtime by 20%.", "We partnered with Binance, CoinEx and KuCoin." }, AnswerMode.Verified);
        Assert.DoesNotContain(r.Issues, i => i.Kind == "UNVERIFIED_NUMBER");
    }
}
