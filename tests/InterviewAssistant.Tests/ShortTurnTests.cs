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
