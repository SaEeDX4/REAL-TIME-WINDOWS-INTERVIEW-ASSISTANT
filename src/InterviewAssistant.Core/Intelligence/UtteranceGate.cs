using System.Text.RegularExpressions;
using InterviewAssistant.Core.Audio;

namespace InterviewAssistant.Core.Intelligence;

/// <summary>What a finalized interviewer turn is. Only <see cref="Question"/> and <see cref="ImplicitPrompt"/> get answers.</summary>
public enum UtteranceKind
{
    /// <summary>A question or request to the candidate ("Why?", "Tell me about yourself.").</summary>
    Question,
    /// <summary>A statement that invites the candidate to talk ("I see you led growth at Arzif.").</summary>
    ImplicitPrompt,
    /// <summary>Background before the actual ask: kept and attached to the next question.</summary>
    Context,
    /// <summary>Meeting logistics ("Can you hear me?", "Let me share my screen").</summary>
    Logistics,
    /// <summary>Addressed to another interviewer ("John, what do you think?").</summary>
    OtherDirected,
    /// <summary>The recognizer reproduced its own vocabulary prompt (typical after a beep).</summary>
    PromptEcho,
    /// <summary>The audio for this turn contained no human speech.</summary>
    NonSpeech,
    /// <summary>A short non-speech sound (beep, chime, click, screenshot sound).</summary>
    ShortAcousticEvent,
    /// <summary>Recognizer artefacts ("Thanks for watching", "♪", "...").</summary>
    Noise,
    /// <summary>A voice was heard, but far too little of it to have said these words (recognizer filled in text).</summary>
    ImplausibleTranscript,
    /// <summary>An interview-flavoured sentence built from the interview's own vocabulary over too little speech.</summary>
    ContextHallucination,
}

public sealed record UtteranceVerdict(UtteranceKind Kind, string Reason, AcousticSupport Support = AcousticSupport.Unknown)
{
    public bool Answerable => Kind is UtteranceKind.Question or UtteranceKind.ImplicitPrompt;
    /// <summary>Rejected because the audio or its consistency with the text says it was not interviewer speech.</summary>
    public bool NotSpeech => Kind is UtteranceKind.Noise or UtteranceKind.PromptEcho or UtteranceKind.NonSpeech or UtteranceKind.ShortAcousticEvent
        or UtteranceKind.ImplausibleTranscript or UtteranceKind.ContextHallucination;
}

/// <summary>
/// Utterance validity layer between the turn detector and the answer engine (defense in depth against answering
/// things that are not interview questions for this candidate). Text alone can never prove speech — a recognizer given a
/// chime may return a perfectly formed interview question — so the audio is judged first and the text must fit it:
///  1. recognizer artefacts → Noise;
///  2. the audio is not a voice (no voiced sound; narrow-band, steadily held or jumping pitch) → ShortAcousticEvent /
///     NonSpeech, whatever the text says (no exception for long, well-formed questions);
///  3. the voice heard could not have said these words (syllables vs voiced time and duration) → ImplausibleTranscript;
///  4. weak support and a sentence built from the interview's own vocabulary prompt → ContextHallucination;
///  5. prompt echo;  6. per sentence: logistics, addressed to someone else, question/request, implicit prompt, context.
/// Short real follow-ups ("Why?", "How so?") pass: they need little voiced time, and a real voice provides it. When no audio
/// was analysed (tests, capture not running) the acoustic steps abstain. A question does not need a question mark, and
/// mentioning target terms with a clearly heard voice is normal; only the combination with weak audio is suspicious.
/// </summary>
public sealed class UtteranceGate
{
    private readonly PromptEchoDetector _echo;
    private readonly HashSet<string> _candidateNames;
    private readonly HashSet<string> _knownTerms;

    public UtteranceGate(PromptEchoDetector echo, IEnumerable<string>? candidateNames = null, IEnumerable<string>? knownTerms = null)
    {
        _echo = echo;
        _candidateNames = new HashSet<string>((candidateNames ?? Array.Empty<string>())
            .SelectMany(n => (n ?? "").Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries))
            .Where(n => n.Length > 1 && !n.Equals("candidate", StringComparison.OrdinalIgnoreCase) && !n.Equals("the", StringComparison.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);
        _knownTerms = new HashSet<string>((knownTerms ?? Array.Empty<string>()).Where(t => t.Length > 1), StringComparer.OrdinalIgnoreCase);
    }

    public PromptEchoDetector Echo => _echo;

    private static readonly Regex Artefact = new(
        @"^\W*$|^(thanks?|thank you) for watching|^(please )?(like and )?subscribe|^subtitles? (by|from)|^transcribed by|^\[?\(?(music|silence|applause|laughter|inaudible|noise|blank_audio)\)?\]?\.?$|^[♪♫\s.]+$|^(you|bye|bye-bye|\.+)\.?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Logistics = new(
        @"\b(can you hear me|can everyone hear|do you hear me|can you see (my|the) screen|are you able to see|is my (audio|video|screen|mic)|you('re| are) (on )?mute|you('re| are) muted|you('re| are) breaking up|my connection|your connection|(let me|i('ll| will)) share (my )?screen|stop sharing|shall we (start|begin|get started)|are you ready( to (start|begin))?|give me (a |one )?(second|sec|moment|minute)|one (second|sec|moment)|bear with me|sorry about that|sorry,? (you go|go ahead|after you|i interrupted)|no,? (you go|go ahead)|you first|after you|let's (move on|wrap up|get started|take a (short )?break)|we('ll| will) (move on|come back)|let me (check|find|pull up|open)|i('ll| will) (be )?tak(e|ing) notes|we have (\d+|a few|about \d+|around \d+) minutes|(is|has) everyone (here|joined|ready)|let's wait for|we('re| are) waiting for|(i|we) can('t| not) hear (you|anything))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex QuestionStart = new(
        @"^(and |but |so |okay,? |ok,? |now,? |then,? |also,? )?(how|what|what's|why|when|where|which|who|whom|whose|can|could|would|will|do|does|did|have|has|is|are|was|were|should|shall|may|might|any)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Request = new(
        @"\b(tell (me|us)|walk (me|us)|talk (me|us) through|take (me|us) through|run (me|us) through|describe|explain|give (me|us)|share (with|an?|your|some)|elaborate|expand on|go deeper|help (me|us) understand|let's (talk|hear) about|i'?d (like|love) (to )?(hear|know|understand|learn)|i('m| am) (curious|interested) (about|to|in)|i want to (hear|know|understand)|what about|how about|thoughts on|your (view|take|thoughts|opinion) on|convince (me|us)|pitch (me|us)|sell (me|us)|imagine|suppose|say you|let's say|please)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Elliptical follow-ups that are complete questions on their own even when a recognizer writes them with a falling
    /// period ("Why.", "How so.", "What happened.", "And then."). A closed set of English interrogative forms — not a list
    /// of transcripts — so a lone "Why" stays answerable while "What a day." does not.
    /// </summary>
    private static readonly Regex EllipticalFollowUp = new(
        @"^((and|so|but|okay|ok|right|hm+|mm+)[,.]?\s+)?(why( not| so| exactly| is that| was that)?|how( so| come| exactly| do you mean)?|what happened( next| then| after that)?|what then|then what|and then( what)?|what else|anything else|like what|such as|for example|in what way|meaning|what do you mean|how did that go|what was the (result|outcome|impact))[.!…]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Exclamative = new(
        @"^((and|so|oh|well|wow)[,.]?\s+)?(what an? |how (very |really )?(nice|great|good|interesting|cool|lovely|wonderful|funny|true|kind|exciting|impressive|fascinating|amazing)\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ConditionalAsk = new(@"\bif you\b.*\b(what|how|which|would you|will you|do you)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Implicit = new(
        @"\b(i see (that )?you|i noticed (that )?you|i saw (that )?you|i read (that )?you|looking at your|according to your|your (cv|resume|résumé|profile|background|linkedin) (says|shows|mentions|lists)|it says (here )?(that )?you)\b|^(so,? )?you('ve| have)? (were|worked|led|managed|built|spent|joined|moved|mentioned|said)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HandOver = new(
        @"(?i:\b(do|did) you have any (more |other |further |follow-?up )?questions for (him|her|them)\b|\b(over to you|your turn|want to (ask|jump in|add|take (this|that|over))|anything to add))\s*,?\s*(?<name>[A-Z][\p{L}'-]+)?",
        RegexOptions.Compiled);

    // Capitalised sentence openers that are not names.
    private static readonly HashSet<string> NotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "so","okay","ok","now","well","right","great","thanks","thank","yes","yeah","no","and","but","also","actually","sure","perfect","alright","then","next",
        "first","second","finally","look","listen","sorry","again","imagine","suppose","honestly","interesting","excellent","good","cool","fine","hmm","oh",
        "ah","hey","hi","hello","today","here","basically","lastly","overall","ultimately","additionally","otherwise","instead","meanwhile","personally",
        "practically","technically","realistically","ideally","generally","typically","usually","normally","currently","recently","previously","specifically",
        "briefly","quickly","simply","clearly","obviously","fortunately","unfortunately","interestingly","importantly","similarly","however","therefore",
        "anyway","besides","indeed","nice","wonderful","understood","got","fair","true","exactly","absolutely","definitely","totally","agreed","amazing",
        "brilliant","lovely","awesome","sounds","makes","mm","uh","um","please","quick","last","one","two","three","in","on","for","with","given","based",
        "speaking","talking","regarding","going","moving","coming","switching","shifting","building","thinking","looking","what","how","why","which",
        // Topics that are often fronted ("Kubernetes, have you used it?") and are not names.
        "python","java","javascript","typescript","kubernetes","docker","react","angular","azure","aws","amazon","google","microsoft","apple","meta",
        "salesforce","excel","tableau","figma","confluence","jira","notion","slack","zoom","teams","asana","linear","stripe","paddle","ethereum",
        "bitcoin","solana","binance","coinbase","scrum","agile","kanban","safe","lean","sql","postgres","kafka","terraform","linux","windows","android",
        "ios","europe","america","asia","london","berlin","dubai","vilnius","lithuania","germany","compliance","engineering","marketing","sales","finance",
        "legal","product","design","security","operations","support","data","growth","pricing","retention","onboarding","blockchain","crypto",
    };

    private static readonly Regex LeadingVocative = new(@"^(?<name>[A-Z][\p{L}'-]{1,20})(\s+[A-Z][\p{L}'-]{1,20})?\s*[,—–:]\s*(?<rest>.+)$", RegexOptions.Compiled);
    private static readonly Regex TrailingVocative = new(@",\s*(?<name>[A-Z][\p{L}'-]{1,20})\s*[?.!]?\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Segment-level check (before a segment joins a turn): recognizer artefacts, prompt echoes and segments whose audio
    /// held no speech are dropped, so a beep's echo can never be glued onto the real question that follows it.
    /// Returns null when the segment is fine.
    /// </summary>
    public UtteranceVerdict? EvaluateSegment(string text, SpeechEvidence evidence)
    {
        var v = Evaluate(text, evidence);
        return v.NotSpeech ? v : null;
    }

    public UtteranceVerdict Evaluate(string text, SpeechEvidence evidence = default)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0 || Artefact.IsMatch(t)) return new(UtteranceKind.Noise, "recognizer artefact");
        var sentences = QuestionExtractor.Sentences(t);
        var support = SpeechPlausibility.Assess(t, evidence);
        var echo = _echo.Check(t, evidence);

        switch (support)
        {
            case AcousticSupport.None:
                if (echo.IsEcho) return new(UtteranceKind.PromptEcho, "prompt echo without speech: " + echo.Reason, support);
                return evidence.ActiveMs <= SpeechEvidence.ShortEventMs
                    ? new(UtteranceKind.ShortAcousticEvent, $"short non-speech sound ({evidence.NotVoiceReason}): {evidence}", support)
                    : new(UtteranceKind.NonSpeech, $"no voice in audio ({evidence.NotVoiceReason}): {evidence}", support);
            case AcousticSupport.Implausible:
                return new(UtteranceKind.ImplausibleTranscript, $"{SpeechPlausibility.Syllables(t)} syllables cannot fit the speech heard: {evidence}", support);
            case AcousticSupport.Weak when _echo.ContextOverlap(t) > 0:
                return new(UtteranceKind.ContextHallucination, $"interview-vocabulary sentence over little speech ({SpeechPlausibility.Syllables(t)} syllables): {evidence}", support);
        }

        if (echo.IsEcho) return new(UtteranceKind.PromptEcho, "prompt echo: " + echo.Reason, support);

        bool anyQuestion = false, anyOther = false, anyLogistics = false, anyImplicit = false;
        foreach (var s in sentences)
        {
            if (Logistics.IsMatch(s) && !HasRealAskBeyondLogistics(s)) { anyLogistics = true; continue; }
            var to = AddressedTo(s);
            if (!IsQuestionSentence(s))
            {
                if (to == Addressee.Other) anyOther = true;
                else if (Implicit.IsMatch(s)) anyImplicit = true;
                continue;
            }
            if (to == Addressee.Other) { anyOther = true; continue; }
            anyQuestion = true;
        }
        if (anyQuestion) return new(UtteranceKind.Question, "question", support);
        if (anyImplicit) return new(UtteranceKind.ImplicitPrompt, "statement inviting the candidate to respond", support);
        if (anyOther) return new(UtteranceKind.OtherDirected, "addressed to another participant", support);
        if (anyLogistics) return new(UtteranceKind.Logistics, "meeting logistics", support);
        return new(UtteranceKind.Context, "statement without a question", support);
    }

    /// <summary>"Sorry about that — so how would you prioritise…?" contains a real ask after the logistics phrase.</summary>
    private static bool HasRealAskBeyondLogistics(string s)
    {
        var rest = Logistics.Replace(s, " ");
        return TextNormalizer.WordCount(rest) >= 4 && (QuestionStart.IsMatch(rest.TrimStart(' ', ',', '.', '—', '-')) || Request.IsMatch(rest) || rest.Contains('?') && TextNormalizer.WordCount(rest) >= 4);
    }

    public static bool IsQuestionSentence(string s)
    {
        var t = s.Trim();
        if (t.Length == 0) return false;
        if (t.EndsWith('?')) return true;
        if (EllipticalFollowUp.IsMatch(t)) return true;
        if (Exclamative.IsMatch(t)) return false;   // "What a day." / "How interesting that is." are reactions, not asks
        return QuestionStart.IsMatch(t) && TextNormalizer.WordCount(t) >= 3 || Request.IsMatch(t) || ConditionalAsk.IsMatch(t);
    }

    public enum Addressee { Candidate, Other }

    /// <summary>
    /// Who a sentence is addressed to. A name in vocative position that is not the candidate's ("John, what do you
    /// think?", "What's your view, Sarah?") or a hand-over between interviewers means someone else. Without a name the
    /// candidate is assumed (one-to-one interviews are the norm).
    /// </summary>
    public Addressee AddressedTo(string sentence)
    {
        var s = Regex.Replace(sentence.Trim(), @"^((okay|ok|so|right|and|well|now|alright|great|thanks|thank you|perfect)[,.]?\s+)+", "", RegexOptions.IgnoreCase);
        if (s.Length > 0) s = char.ToUpperInvariant(s[0]) + s[1..];
        var h = HandOver.Match(s);
        if (h.Success)
        {
            if (Regex.IsMatch(h.Value, @"questions for (him|her|them)", RegexOptions.IgnoreCase)) return Addressee.Other;
            var hn = h.Groups["name"].Value;
            if (hn.Length > 0 && IsOtherName(hn)) return Addressee.Other;
        }
        var lead = LeadingVocative.Match(s);
        if (lead.Success && IsOtherName(lead.Groups["name"].Value) && IsQuestionSentence(lead.Groups["rest"].Value)) return Addressee.Other;
        var trail = TrailingVocative.Match(s);
        if (trail.Success && IsOtherName(trail.Groups["name"].Value)) return Addressee.Other;
        if (_candidateNames.Count > 0 && Regex.IsMatch(s, @"\b(he|she)('s| is| was| has| did| does)\b", RegexOptions.IgnoreCase) &&
            Regex.IsMatch(s, @"\b(what do you think|do you agree|anything to add)\b", RegexOptions.IgnoreCase))
            return Addressee.Other; // discussing the candidate in the third person with a colleague
        return Addressee.Candidate;
    }

    private bool IsOtherName(string name) =>
        name.Length > 1 && !NotNames.Contains(name) && !_candidateNames.Contains(name) && !_knownTerms.Contains(name) && char.IsUpper(name[0]);
}
