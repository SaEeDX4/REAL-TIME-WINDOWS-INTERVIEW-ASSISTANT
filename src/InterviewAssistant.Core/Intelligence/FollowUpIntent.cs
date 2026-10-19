using System.Text.RegularExpressions;

namespace InterviewAssistant.Core.Intelligence;

/// <summary>What kind of follow-up a sentence is (none = not a follow-up request).</summary>
public enum FollowUpKind
{
    None,
    /// <summary>An imperative that asks the candidate to say (more): "Tell me more.", "Walk me through that.", "Go on."</summary>
    Imperative,
    /// <summary>An elliptical question: "Why?", "Why.", "How so?", "And then?", "What happened?", "Really?"</summary>
    Elliptical,
}

/// <summary>
/// Candidate-directed follow-ups recognized by their grammar, not by a list of transcripts. A real interviewer turn does
/// not need a question mark, an interrogative word or question grammar: "Tell me more.", "Go on.", "Continue.",
/// "Give me an example." and "Take me through your thinking." are complete asks. They are recognized as an ELICITATION
/// VERB in imperative position — the start of the sentence after discourse markers ("so", "okay"), optionally behind a
/// politeness frame ("please", "can you", "could you", "I'd like you to") — followed by what is asked for. The same verb
/// later in a statement ("…hard to explain in the app.") is not an ask.
/// Complement constraints keep instructions that are not asks to speak out:
///  - the movement verbs ("go on", "continue", "carry on", "keep going") are an ask only with nothing after them, an
///    addressee, "please"/"then", a separate clause after a comma ("Go on, I'm listening."), or a speaking complement
///    ("…with your answer", "…about the migration"). "Go on mute.", "Continue to the next slide.", "Keep going with the
///    exercise.", "Carry on with your code." and "Continue coding." are not;
///  - "show me/us" asks only for an explanation ("Show me how you would do it."), not for a screen or a document;
///  - an ask deferred to later or to another channel ("Clarify that in the follow-up email.") is not an ask for now.
/// English only: other languages' imperative follow-ups are not recognized by this grammar (a "?" form still is).
/// Purely textual: whether the audio carried a voice is decided separately (acoustic evidence) and both must agree.
/// </summary>
public static class FollowUpIntent
{
    // Discourse markers that may precede the ask ("Okay, so tell me more.").
    // A leading vocative ("Shervin, go on.") is allowed here; WHO is addressed is decided by UtteranceGate.AddressedTo.
    private const string Lead = @"^((and|so|okay|ok|right|now|well|alright|great|thanks|hm+|mm+)[,.]?\s+)*(\p{Lu}[\p{L}'’-]+,\s+)?((and|so|okay|ok|now)[,.]?\s+)*";
    // Politeness frames in front of the imperative.
    private const string Frame = @"((please|kindly|just)\s+|(can|could|would|will) you\s+(please\s+)?|(i|we)( would|'d|’d) (like|love) (you )?to\s+|(i|we) want you to\s+)?";
    // Elicitation verbs and the object they take (what the candidate should say). Each needs its complement where the
    // verb alone would be too vague ("tell" needs "me/us"; "give" needs "me/us/an example"; "show me" a how/what clause).
    private const string Verb =
        @"(tell (me|us)\b|walk (me|us) through\b|talk (me|us) through\b|take (me|us) through\b|run (me|us) through\b|" +
        @"talk (about|more)\b|say (more|a bit more|a little more)\b|go deeper\b|dig deeper\b|" +
        @"explain\b|elaborate\b|expand\b|describe\b|clarify\b|unpack\b|illustrate\b|spell (it|that|this) out\b|" +
        @"break (it|that|this) down\b|give (me|us)\b|give (an|another|some|a) (example|instance|detail|details)\b|" +
        @"show (me|us) (how|what|why|where|which|an? example|that|this|it)\b|" +
        @"help (me|us) understand\b|paint (me|us) a picture\b|be (more )?specific\b|go into (more )?detail\b)";
    // "Go on" / "continue" / "carry on" / "keep going": an ask to keep talking, so the complement must be about talking.
    private const string Movement = @"(go on|carry on|keep going|continue)";
    // What may follow a movement verb: nothing, please/then, an addressee (who is addressed is decided elsewhere), a separate
    // clause after a comma or dash, or a speaking complement. Anything else ("to the next slide", "with the exercise",
    // "mute", "coding") makes it an instruction about the meeting, not an ask to speak.
    private const string MovementBare = @"(\s*,?\s*(please|then)\b)?(\s*,?\s+(?-i:\p{Lu})[\p{L}'’-]+)?[\s.!?？…]*$|\s*[,;:—–]\s*\S";
    private const string MovementSpeaking =
        @"\s+(about\b|explaining\b|telling\b|talking\b|from there\b|where you (left off|were)\b|" +
        @"with (your|that|this|the) (answer|story|example|explanation|thought|thoughts|point|thinking|response|description|reply|reasoning)\b)";

    private static readonly Regex ImperativeAsk = new(Lead + Frame + Verb, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MovementAsk = new(Lead + Frame + Movement + "(" + MovementBare + "|" + MovementSpeaking + ")", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // A movement ask with no topic of its own ("Go on.", "Please continue, Shervin.", "Carry on with your answer.").
    private static readonly Regex BareMovementAsk = new(Lead + Frame + Movement + "(" + MovementBare + @"|\s+(from there\b|where you (left off|were)\b|with (your|that|this|the) \w+[\s.!?？…]*$))", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // An ask deferred to later or to another channel is not an ask to answer now.
    private static readonly Regex Deferred = new(
        @"\b(later|afterwards|after the (call|interview|meeting)|in (the|an|your) (follow-?up )?(e-?mail|form|chat|writing)|by e-?mail|offline|next time)[\s,.!?？…]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "Example?", "Details?" — a bare request noun with question intonation is an ask too (handled by "?" elsewhere);
    // without "?" only these request nouns count, and only when they are the whole sentence. A lone "More." is not one
    // (too easily a recognizer's guess for a short sound).
    private static readonly Regex BareRequest = new(@"^(an? |another |more |some )?(example|examples|detail|details|specifics)[.!…]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Asks that point back at what the candidate just said ("that", "it", "more", "further", "another", "your thinking"):
    // a follow-up, never the continuation of the previous question.
    private static readonly Regex Anaphoric = new(
        @"\b(that|this|it|more|further|another|again|deeper|why|how so|and then|then what|what happened)\b|" +
        @"\b(your|the) (thinking|reasoning|thought process|logic|process|rationale|steps)[\s.!?？…]*$",
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
        if ((ImperativeAsk.IsMatch(t) || MovementAsk.IsMatch(t) || BareRequest.IsMatch(t)) && !Deferred.IsMatch(t)) return FollowUpKind.Imperative;
        if (UtteranceGate.IsEllipticalAsk(t)) return FollowUpKind.Elliptical;
        return FollowUpKind.None;
    }

    /// <summary>A candidate-directed imperative ask anywhere a sentence starts (after discourse markers / a politeness frame).</summary>
    public static bool IsImperativeAsk(string sentence) => Classify(sentence) == FollowUpKind.Imperative;

    /// <summary>
    /// A short, self-contained follow-up that refers back to the previous answer ("Tell me more.", "Why?", "Give me an
    /// example."). Such a turn is a NEW ask when it comes after the candidate had time to reply (the turn detector's
    /// timing rule) — merging it into that question would replace the earlier answer instead of answering the follow-up.
    /// </summary>
    public static bool IsStandaloneFollowUp(string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0 || TextNormalizer.WordCount(t) > 12) return false;
        if (Regex.IsMatch(t, @"^(and|or|but|also|plus|as well as)\b", RegexOptions.IgnoreCase) && !UtteranceGate.IsEllipticalAsk(t)) return false;   // "and which metrics…" continues
        var kind = Classify(t);
        return kind == FollowUpKind.Elliptical ||
               (kind == FollowUpKind.Imperative && (Anaphoric.IsMatch(t) || EndsOnTheAsk.IsMatch(t) || BareMovementAsk.IsMatch(t)));
    }

    // Leading discourse markers and politeness words that carry no content of their own.
    private static readonly Regex Filler = new(@"^((and|so|okay|ok|right|now|well|alright|great|thanks|please|just|hm+|mm+)[,.]?\s+)+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// A one- or two-word imperative follow-up ("Continue.", "Go on.", "Elaborate.", "Okay, keep going."): so little text
    /// that a recognizer can produce it for almost any short sound, so the audio must clearly be speech (see
    /// <see cref="UtteranceGate"/>). A form asked with "?" ("Example?", "Elaborate?") is not included: rc.4 already
    /// answered those as questions, and they keep exactly that behaviour.
    /// </summary>
    public static bool IsVeryShortImperative(string text)
    {
        var t = (text ?? "").Trim();
        if (TextNormalizer.EndsWithQuestionMark(t) || Classify(t) != FollowUpKind.Imperative) return false;
        return TextNormalizer.WordCount(Filler.Replace(t, "")) <= 2;
    }
}
