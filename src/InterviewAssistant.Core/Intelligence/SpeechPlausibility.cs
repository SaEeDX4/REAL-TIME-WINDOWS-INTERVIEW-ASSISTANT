using System.Text.RegularExpressions;
using InterviewAssistant.Core.Audio;

namespace InterviewAssistant.Core.Intelligence;

/// <summary>How well the audio supports a transcript (structured, logged as metadata only — never the text).</summary>
public enum AcousticSupport
{
    /// <summary>No audio was analysed for this turn (tests, capture not running): acoustic layers abstain.</summary>
    Unknown,
    /// <summary>The audio is not a human voice (beep, chime, alert tone, click, knock, instrumental music).</summary>
    None,
    /// <summary>A voice was heard, but far too little of it for the words in the transcript.</summary>
    Implausible,
    /// <summary>A voice was heard and could have said the transcript, with little margin.</summary>
    Weak,
    /// <summary>Clearly enough voiced speech for the transcript.</summary>
    Strong,
}

/// <summary>
/// Consistency between a transcript and the audio it came from. A recognizer given a short non-speech sound can return a
/// perfectly formed, context-flavoured interview question; what it cannot do is make a 0.6 s chime contain the ~12
/// syllables of "What is your experience with FinTech products?". Every syllable a person says has a voiced vowel nucleus
/// (typically 80–200 ms; eSpeak fixtures ≈ 140 ms), so the voiced time bounds the syllables a transcript may contain:
///  - at least <see cref="MinVoicedMsPerSyllable"/> ms voiced per syllable (≈ 3× faster than fast speech), and no more than
///    <see cref="MaxSyllablesPerActiveSecond"/> syllables per second of sound — otherwise the transcript is impossible
///    for the audio (<see cref="AcousticSupport.Implausible"/>);
///  - below <see cref="StrongVoicedMsPerSyllable"/> ms per syllable (after the same slack) the support is only
///    <see cref="AcousticSupport.Weak"/>.
/// The syllable count is a deliberate under-estimate (vowel groups for Latin scripts; one per word or per CJK character
/// otherwise), so real speech is never judged impossible because of counting.
/// </summary>
public static class SpeechPlausibility
{
    public const int MinVoicedMsPerSyllable = 35;
    public const int StrongVoicedMsPerSyllable = 70;
    public const double MaxSyllablesPerActiveSecond = 9;
    /// <summary>Slack for the start/end of an utterance clipped by the recognizer's segmentation.</summary>
    public const int SlackSyllables = 2;

    public static AcousticSupport Assess(string transcript, SpeechEvidence evidence)
    {
        if (evidence.Unknown) return AcousticSupport.Unknown;
        if (!evidence.VoiceLike) return AcousticSupport.None;
        if (evidence.Incomplete) return AcousticSupport.Unknown;   // a voice was heard, but not the whole window
        int syllables = Syllables(transcript);
        int budget = Math.Max(0, syllables - SlackSyllables);
        if (evidence.VoicedMs < budget * MinVoicedMsPerSyllable) return AcousticSupport.Implausible;
        if (budget > MaxSyllablesPerActiveSecond * evidence.ActiveMs / 1000.0) return AcousticSupport.Implausible;
        // The same slack as above: a fast talker, or an utterance whose edge fell outside the window, is still "Strong".
        return evidence.VoicedMs >= budget * StrongVoicedMsPerSyllable ? AcousticSupport.Strong : AcousticSupport.Weak;
    }

    private static readonly Regex Word = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);
    private static readonly Regex VowelGroup = new(@"[aeiouyàáâãäåæèéêëìíîïòóôõöøùúûüýÿœ]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Lower-bound syllable estimate (never over-counts real speech).</summary>
    public static int Syllables(string text)
    {
        int total = 0;
        foreach (Match m in Word.Matches(text ?? ""))
        {
            var w = m.Value;
            int cjk = w.Count(c => c >= '぀' && c <= '鿿' || c >= '가' && c <= '힯');
            if (cjk > 0) { total += cjk; continue; }
            if (w.All(c => c < 0x250))   // Latin script: vowel groups, silent final "e" not counted
            {
                var groups = VowelGroup.Matches(w).Count;
                var lw = w.ToLowerInvariant();
                if (groups > 1 && lw.EndsWith('e') && !lw.EndsWith("le")) groups--;                       // make, use
                else if (groups > 1 && lw.Length > 3 && lw.EndsWith("es") && !"sxzcg".Contains(lw[^3]) && !lw.EndsWith("hes")) groups--;   // makes, times (not boxes, wishes)
                else if (groups > 1 && lw.Length > 3 && lw.EndsWith("ed") && !"td".Contains(lw[^3])) groups--;   // used, asked (not wanted, ended)
                total += Math.Max(1, groups);
            }
            else total += 1;          // other scripts: one per word (an under-estimate)
        }
        return total;
    }
}
