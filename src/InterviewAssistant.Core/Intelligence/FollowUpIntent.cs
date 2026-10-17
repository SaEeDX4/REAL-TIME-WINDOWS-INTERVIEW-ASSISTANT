using System.Text.RegularExpressions;

namespace InterviewAssistant.Core.Intelligence;

/// <summary>What kind of follow-up a sentence is (none = not a follow-up request).</summary>
public enum FollowUpKind
{
    None,
    /// <summary>An imperative that asks the candidate to say (more): "Tell me more.", "Walk me through that.", "Go on."</summary>
    Imperative,
    /// <summary>An elliptical question: "Why?", "How so.", "And then?", "What happened?"</summary>
    Elliptical,
}

/// <summary>
/// Candidate-directed follow-ups recognized by their grammar, not by a list of transcripts. A real interviewer turn does
/// not need a question mark, an interrogative word or question grammar: "Tell me more.", "Go on.", "Continue.",
/// "Give me an example." and "Take me through your thinking." are complete asks. They are recognized as an ELICITATION
/// VERB in imperative position — the start of the sentence after discourse markers ("so", "okay"), optionally behind a
/// politeness frame ("please", "can you", "could you", "I'd like you to") — followed by what is asked for. The same verb
/// later in a statement ("…hard to explain in the app.") is not an ask.
/// Purely textual: whether the audio carried a voice is decided separately (acoustic evidence) and both must agree.
/// </summary>
public static class FollowUpIntent
{
    // Discourse markers that may precede the ask ("Okay, so tell me more.").
    // A leading vocative ("Shervin, go on.") is allowed here; WHO is addressed is decided by UtteranceGate.AddressedTo.
    private const string Lead = @"^((and|so|okay|ok|right|now|well|alright|great|thanks|hm+|mm+)[,.]?\s+)*(\p{Lu}[\p{L}'’-]+,\s+)?((and|so|okay|ok|now)[,.]?\s+)*";
    // Politeness frames in front of the imperative.
    private const string Frame = @"((please|kindly|just)\s+|(can|could|would|will) you\s+(please\s+)?|(i|we)( would|'d|’d) (like|love) (you )?to\s+|(i|we) want you to\s+|feel free to\s+)?";
    // Elicitation verbs and the object they take (what the candidate should say). Each needs its complement where the
    // verb alone would be too vague ("tell" needs "me/us"; "give" needs "me/us/an example").
    private const string Verb =
        @"(tell (me|us)\b|walk (me|us) through\b|talk (me|us) through\b|take (me|us) through\b|run (me|us) through\b|" +
        @"talk (about|more)\b|say (more|a bit more|a little more)\b|go on\b|carry on\b|keep going\b|continue\b|go deeper\b|dig deeper\b|" +
        @"explain\b|elaborate\b|expand\b|describe\b|clarify\b|unpack\b|illustrate\b|spell (it|that|this) out\b|" +
        @"break (it|that|this) down\b|give (me|us)\b|give (an|another|some|a) (example|instance|detail|details)\b|share\b|show (me|us)\b|" +
        @"help (me|us) understand\b|paint (me|us) a picture\b|be (more )?specific\b|go into (more )?detail\b)";

    private static readonly Regex ImperativeAsk = new(Lead + Frame + Verb, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "Example?", "Details?", "More?" — a bare request noun with question intonation is an ask too (handled by "?" elsewhere);
    // without "?" only these request nouns count, and only when they are the whole sentence.
    private static readonly Regex BareRequest = new(@"^(an? |another |more |some )?(example|examples|detail|details|specifics|more)[.!…]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Asks that point back at what the candidate just said ("that", "it", "more", "further", "another", "go on"): a
    // follow-up, never the continuation of the previous question.
    private static readonly Regex Anaphoric = new(
        @"\b(that|this|it|more|further|another|again|deeper|go on|carry on|keep going|continue|why|how so|and then|then what|what happened)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // …or end on the ask itself, with no new topic after it ("Elaborate.", "Give me an example.") — unlike "Give me an
    // example of a metric you moved.", which is a new question.
    private static readonly Regex EndsOnTheAsk = new(
        @"\b(example|examples|detail|details|specifics|elaborate|expand|explain|clarify|unpack|illustrate|specific)[\s.!?？…]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static FollowUpKind Classify(string sentence)
    {
        var t = (sentence ?? "").Trim();
        if (t.Length == 0) return FollowUpKind.None;
        if (ImperativeAsk.IsMatch(t) || BareRequest.IsMatch(t)) return FollowUpKind.Imperative;
        if (UtteranceGate.IsEllipticalFollowUp(t)) return FollowUpKind.Elliptical;
        return FollowUpKind.None;
    }

    /// <summary>A candidate-directed imperative ask anywhere a sentence starts (after discourse markers / a politeness frame).</summary>
    public static bool IsImperativeAsk(string sentence) => Classify(sentence) == FollowUpKind.Imperative;

    /// <summary>
    /// A short, self-contained follow-up that refers back to the previous answer ("Tell me more.", "Why?", "Give me an
    /// example."). Such a turn is a NEW ask even when it follows the previous question quickly — merging it into that
    /// question would replace the earlier answer instead of answering the follow-up.
    /// </summary>
    public static bool IsStandaloneFollowUp(string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0 || TextNormalizer.WordCount(t) > 12) return false;
        if (Regex.IsMatch(t, @"^(and|or|but|also|plus|as well as)\b", RegexOptions.IgnoreCase) && !UtteranceGate.IsEllipticalFollowUp(t)) return false;   // "and which metrics…" continues
        var kind = Classify(t);
        return kind == FollowUpKind.Elliptical || (kind == FollowUpKind.Imperative && (Anaphoric.IsMatch(t) || EndsOnTheAsk.IsMatch(t)));
    }
}
