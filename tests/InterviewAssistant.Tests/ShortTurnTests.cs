using InterviewAssistant.Core.Audio;
using InterviewAssistant.Core.Intelligence;
using InterviewAssistant.Core.Turn;
using Xunit;

namespace InterviewAssistant.Tests;

/// <summary>
/// RC4: short real follow-ups stay answerable however the recognizer punctuates them, while acknowledgements and
/// fillers do not become questions — without a word-count floor, a question-mark requirement or a list of transcripts.
/// </summary>
public class ShortTurnTests
{
    private static UtteranceGate Gate() => new(new PromptEchoDetector("Job interview for a Product Owner role at Teroxx. Terms: FinTech, blockchain."));

    [Theory]
    [InlineData("Why?")] [InlineData("Why.")] [InlineData("Why")] [InlineData("How so?")] [InlineData("How so.")]
    [InlineData("Really?")] [InlineData("And then?")] [InlineData("And then.")] [InlineData("Tell me more.")]
    [InlineData("What happened?")] [InlineData("What happened.")] [InlineData("Metrics?")] [InlineData("Anything else?")]
    [InlineData("Anything else.")] [InlineData("Timeline?")] [InlineData("Why not?")] [InlineData("How come.")]
    public void ShortFollowUp_IsAQuestion_AndIsNotDroppedByTheTurnDetector(string text)
    {
        Assert.False(TurnDetector.IsNonQuestion(text), text);
        Assert.Equal(UtteranceKind.Question, Gate().Evaluate(text).Kind);
    }

    [Theory]
    [InlineData("Okay.")] [InlineData("Okay?")] [InlineData("Thanks.")] [InlineData("Right?")] [InlineData("Hm?")]
    [InlineData("Mm-hmm.")] [InlineData("Sorry?")] [InlineData("Great")] [InlineData("Uh")]
    public void Acknowledgement_OrFiller_IsNotAQuestionTurn(string text) => Assert.True(TurnDetector.IsNonQuestion(text), text);

    [Theory]
    [InlineData("What a day.")] [InlineData("How nice.")] [InlineData("What a great story that was.")] [InlineData("How interesting that is.")]
    public void Exclamation_StartingWithAWhWord_IsNotAQuestion(string text) => Assert.NotEqual(UtteranceKind.Question, Gate().Evaluate(text).Kind);

    [Fact]
    public void ShortFollowUp_OverAShortRealVoice_HasAcousticSupport()
    {
        // 200 ms voiced across ~4 pitch pairs is what a real half-second "Why?" measures (speech_why fixtures).
        var why = new SpeechEvidence(5, 500, 400, 200, 0, PitchPairs: 4, SteadyPairs: 1, WindowMs: 500);
        Assert.True(why.VoiceLike);
        foreach (var t in new[] { "Why?", "Why.", "How so?", "Really?" })
            Assert.True(Gate().Evaluate(t, why).Answerable, t);
    }

    [Fact]
    public void FewTonalHops_DoNotMakeShortSpeechTonal_ButAChimeIsTonal()
    {
        // "And then?" over a call: 240 ms voiced, 60 ms tonal (three hops) — measurement noise, still a voice.
        var andThen = new SpeechEvidence(7, 700, 420, 240, 60, PitchPairs: 7, SteadyPairs: 4, WindowMs: 700);
        Assert.True(andThen.VoiceLike, andThen.ToString());
        var chime = new SpeechEvidence(11, 1100, 1080, 100, 420, PitchPairs: 18, SteadyPairs: 12, WindowMs: 1100);
        Assert.False(chime.VoiceLike);
        Assert.True(chime.TooTonal);
    }

    [Fact]
    public void SteadyPitch_IsJudgedOnlyWithEnoughPairs()
    {
        var buzz = new SpeechEvidence(8, 800, 600, 400, 0, PitchPairs: 17, SteadyPairs: 14, WindowMs: 800);
        Assert.False(buzz.VoiceLike);
        Assert.True(buzz.PitchJudged);
        var shortWhy = new SpeechEvidence(5, 500, 460, 240, 0, PitchPairs: 6, SteadyPairs: 5, WindowMs: 500);
        Assert.True(shortWhy.VoiceLike);   // 83 % steady over 6 pairs is noise, not a tone
        Assert.False(shortWhy.PitchJudged);
    }
}

public class DuplicateContextTests
{
    private static UtteranceGate Gate() => new(new PromptEchoDetector("Job interview"));

    [Fact]
    public void QuestionSaidTwice_IsNotItsOwnContextLine()
    {
        var x = QuestionExtractor.Extract("How would you increase adoption of the rewards program. Our adoption is flat this quarter. So how would you increase adoption of the rewards program?", "", null, Gate());
        Assert.DoesNotContain("increase adoption", x.ContextSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("adoption is flat", x.ContextSummary);
    }

    [Fact]
    public void EarlierContextThatRepeatsTheQuestion_IsDropped_RealContextKept()
    {
        var earlier = "We launched the VIP program in March. How would you diagnose the slow adoption";
        var x = QuestionExtractor.Extract("How would you diagnose the slow adoption?", earlier, null, Gate());
        Assert.Contains("VIP program", x.ContextSummary);
        Assert.DoesNotContain("diagnose", x.ContextSummary);
    }

    [Fact]
    public void RelatedButDifferentSentence_StaysContext()
    {
        var x = QuestionExtractor.Extract("Adoption of the rewards program has been slow since launch. How would you increase adoption?", "", null, Gate());
        Assert.Contains("slow since launch", x.ContextSummary);
    }
}

/// <summary>RC4: imperative asks are recognised in imperative position only — statements that merely contain the verb are not.</summary>
public class ImperativeAskTests
{
    private static UtteranceGate Gate() => new(new PromptEchoDetector("Job interview"));

    [Theory]
    [InlineData("Describe a time you disagreed with your manager.")]
    [InlineData("So, describe your approach to discovery.")]
    [InlineData("Please share an example of a hard trade-off.")]
    [InlineData("Share an example of a feature you killed.")]
    [InlineData("Could you explain how you prioritised the backlog.")]
    [InlineData("Can you elaborate on the migration.")]
    [InlineData("I'd like you to describe your first ninety days.")]
    [InlineData("I want you to explain the trade-off.")]
    [InlineData("We would like you to walk us through the launch.")]
    [InlineData("Give me an example of a metric you moved.")]
    [InlineData("Imagine you joined us tomorrow and the release is late.")]
    [InlineData("Okay, explain the architecture to me.")]
    [InlineData("Tell me about your biggest failure.")]
    public void ImperativeAsk_IsAQuestion(string text) => Assert.Equal(UtteranceKind.Question, Gate().Evaluate(text).Kind);

    [Theory]
    [InlineData("The VIP tiers are popular but expensive and hard to explain in the app.")]
    [InlineData("Our onboarding docs describe the process in detail.")]
    [InlineData("Users often share their referral links on social media.")]
    [InlineData("That would explain the drop in retention last quarter.")]
    public void StatementContainingTheVerb_IsNotAnAsk(string text) => Assert.NotEqual(UtteranceKind.Question, Gate().Evaluate(text).Kind);
}

/// <summary>RC4 review: the syllable estimate must not over-count silent endings, and fast real speech stays "Strong".</summary>
public class SyllableEstimateTests
{
    [Theory]
    [InlineData("makes", 1)] [InlineData("used", 1)] [InlineData("asked", 1)] [InlineData("times", 1)]
    [InlineData("wanted", 2)] [InlineData("boxes", 2)] [InlineData("wishes", 2)] [InlineData("product", 2)]
    [InlineData("What is your experience with FinTech products?", 12)]
    public void Syllables_AreALowerBound(string text, int max) => Assert.True(SpeechPlausibility.Syllables(text) <= max, $"{text}: {SpeechPlausibility.Syllables(text)}");

    [Theory]
    [InlineData("Which interview stage?")]
    [InlineData("Why product?")]
    [InlineData("And the wallet?")]
    public void FastShortRealQuestion_IsStrong(string text)
    {
        // ~110 ms voiced per syllable incl. clipped edges: 340 ms voiced, a judged moving pitch.
        var fast = new SpeechEvidence(12, 1200, 600, 340, 0, PitchPairs: 12, SteadyPairs: 3, WindowMs: 1200);
        Assert.Equal(AcousticSupport.Strong, SpeechPlausibility.Assess(text, fast));
    }

    [Fact]
    public void ContextHallucinationOverAShortVoiceLikeSound_IsStillRejected()
    {
        var knock = new SpeechEvidence(19, 1900, 620, 360, 40, PitchPairs: 11, SteadyPairs: 2, WindowMs: 1900);
        var support = SpeechPlausibility.Assess("What is your experience with FinTech products?", knock);
        Assert.True(support is AcousticSupport.Weak or AcousticSupport.Implausible, support.ToString());
        var gate = new UtteranceGate(new PromptEchoDetector("Job interview for a Product Owner role. Terms: FinTech, blockchain."));
        Assert.False(gate.Evaluate("What is your experience with FinTech products?", knock).Answerable);
    }
}
