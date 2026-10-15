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
}

public sealed record UtteranceVerdict(UtteranceKind Kind, string Reason)
{
    public bool Answerable => Kind is UtteranceKind.Question or UtteranceKind.ImplicitPrompt;
}

/// <summary>
/// Utterance validity layer between the turn detector and the answer engine (defense in depth against answering
/// things that are not interview questions for this candidate):
///  1. recognizer artefacts → Noise;  2. acoustic evidence (no human speech in the audio) → ShortAcousticEvent/NonSpeech;
///  3. prompt echo;  4. per sentence: logistics, addressed to someone else, question/request, implicit prompt, context.
/// A question does not need a question mark ("Tell me about yourself.", "Walk me through that."). The gate never
/// rejects a question for mentioning target terms; it rejects the structure of the prompt.
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
        return v.Kind is UtteranceKind.Noise or UtteranceKind.PromptEcho or UtteranceKind.NonSpeech or UtteranceKind.ShortAcousticEvent ? v : null;
    }

    public UtteranceVerdict Evaluate(string text, SpeechEvidence evidence = default)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0 || Artefact.IsMatch(t)) return new(UtteranceKind.Noise, "recognizer artefact");
        var sentences = QuestionExtractor.Sentences(t);
        bool clearQuestion = sentences.Any(s => IsQuestionSentence(s) && !Logistics.IsMatch(s) && AddressedTo(s) != Addressee.Other)
                             && TextNormalizer.WordCount(t) >= 6;

        if (!evidence.Unknown && !evidence.HasSpeech)
        {
            var echoUnderNoise = _echo.Check(t, evidence);
            // Acoustic veto: no human speech in the audio. A long, clearly formed question is the one exception (the
            // acoustic layer could be wrong about unusual audio); recognizer hallucinations on noise are prompt echoes
            // and short phrases, which never pass.
            if (echoUnderNoise.IsEcho) return new(UtteranceKind.PromptEcho, "prompt echo without speech: " + echoUnderNoise.Reason);
            if (!clearQuestion)
                return evidence.IsShortNonSpeechEvent
                    ? new(UtteranceKind.ShortAcousticEvent, "short non-speech sound: " + evidence)
                    : new(UtteranceKind.NonSpeech, "no speech in audio: " + evidence);
        }

        var echo = _echo.Check(t, evidence);
        if (echo.IsEcho) return new(UtteranceKind.PromptEcho, "prompt echo: " + echo.Reason);

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
        if (anyQuestion) return new(UtteranceKind.Question, "question");
        if (anyImplicit) return new(UtteranceKind.ImplicitPrompt, "statement inviting the candidate to respond");
        if (anyOther) return new(UtteranceKind.OtherDirected, "addressed to another participant");
        if (anyLogistics) return new(UtteranceKind.Logistics, "meeting logistics");
        return new(UtteranceKind.Context, "statement without a question");
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
