using System.Text;
using System.Text.RegularExpressions;
using InterviewAssistant.Core.Knowledge;

namespace InterviewAssistant.Core.Intelligence;

/// <summary>
/// The actual ask hidden inside an interviewer turn. Long turns often start with background and end with the question
/// ("Our reward program grew quickly… Engineering has limited capacity. So if you owned this product, what would you
/// change first and how would you measure whether it worked?"). Classification, matching and the answer focus on
/// <see cref="CoreQuestion"/>; the context keeps the constraints the answer must respect. No chain-of-thought, only fields.
/// </summary>
public sealed record ExtractedQuestion(
    string Original,
    string CoreQuestion,
    IReadOnlyList<string> SubQuestions,
    string ContextSummary,
    string PromptContext,
    IReadOnlyList<string> Constraints,
    IReadOnlyList<string> Entities,
    double Confidence,
    bool DirectedToCandidate = true,
    string? SpeakerLabel = null)
{
    /// <summary>Typed question or single-sentence turn: the text is the question.</summary>
    public static ExtractedQuestion Plain(string q) => new(q, q, Array.Empty<string>(), "", "", Array.Empty<string>(), Array.Empty<string>(), 1.0);
    public bool HasContext => ContextSummary.Length > 0;
}

public static class QuestionExtractor
{
    public const int MaxContextChars = 900, MaxSummaryChars = 220, MaxCoreWords = 80;

    private static readonly Regex SentenceSplit = new(@"(?<=[.?!…])\s+(?=[\p{Lu}\p{Lo}""“'(\d])|(?<=[?])\s+|\s+[—–]\s+(?=[\p{L}])|;\s+", RegexOptions.Compiled);
    private static readonly Regex RestartCue = new(@"\b(sorry,? let me (rephrase|put (it|that) (differently|another way)|ask (it|that) differently)|let me rephrase|let me put (it|that) (differently|another way)|what i('m| am) (really )?asking is|in other words|to put it (differently|another way)|actually,? let me ask)\b[\s,:—-]*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Discourse = new(@"^((so|okay|ok|well|right|alright|now|then|anyway)[,.]?\s+)+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ConstraintCue = new(@"\b(limited|limit|constraint|capacity|budget|deadline|quarter|weeks?|months?|only|can't|cannot|won't|must|regulat|complian|legal|risk|worried|concern|pressure|blocked|dependency|headcount|cost|small team|no (budget|team|data))\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AskStart = new(@"\b(so,? )?(if you|what|how|why|which|would you|could you|can you|do you|tell me|walk me|talk me|describe|explain|give me)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SubSplit = new(@"(?:,\s*|\s+)(?:and|also|plus)\s+(?=(?:how|what|which|why|when|who|would|could|can|do|did|where)\b)|(?<=\?)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Splits a transcript into sentences (also on dashes/semicolons used as pauses by the recognizer).</summary>
    public static List<string> Sentences(string text)
    {
        var parts = SentenceSplit.Split(text.Trim()).Select(p => p.Trim()).Where(p => p.Length > 0 && Regex.IsMatch(p, @"[\p{L}\p{N}]")).ToList();
        return parts.Count == 0 ? new List<string> { text.Trim() } : parts;
    }

    /// <param name="text">The finalized turn (cleaned).</param>
    /// <param name="earlierContext">Context statements from the previous turns (already bounded), oldest first.</param>
    public static ExtractedQuestion Extract(string text, string earlierContext, TargetContext? ctx, UtteranceGate gate)
    {
        var original = text.Trim();
        var working = original;
        string restartPrefix = "";
        // "…prioritise the backlog — sorry, let me put it differently: what would you cut first?" → the restated ask wins.
        var restarts = RestartCue.Matches(working);
        if (restarts.Count > 0)
        {
            var last = restarts[^1];
            var after = working[(last.Index + last.Length)..].Trim();
            if (TextNormalizer.WordCount(after) >= 3) { restartPrefix = working[..last.Index].Trim(); working = Capitalize(after); }
        }

        var sentences = Sentences(working);
        int lastQ = -1;
        for (int i = sentences.Count - 1; i >= 0; i--)
            if (UtteranceGate.IsQuestionSentence(sentences[i]) && gate.AddressedTo(sentences[i]) == UtteranceGate.Addressee.Candidate) { lastQ = i; break; }

        int first = lastQ, endIdx = lastQ;
        double confidence;
        if (lastQ >= 0)
        {
            confidence = sentences[lastQ].EndsWith('?') ? 1.0 : 0.85;
            // Adjacent question sentences belong to the same ask ("What would you change first? And how would you measure it?").
            for (int i = lastQ - 1; i >= 0; i--)
            {
                if (UtteranceGate.IsQuestionSentence(sentences[i]) && gate.AddressedTo(sentences[i]) == UtteranceGate.Addressee.Candidate) { first = i; continue; }
                if (TextNormalizer.WordCount(sentences[i]) <= 4 && i - 1 >= 0 && UtteranceGate.IsQuestionSentence(sentences[i - 1])) continue; // "Right." between parts
                break;
            }
        }
        else
        {
            // No explicit question: an implicit prompt is the last sentence.
            first = endIdx = sentences.Count - 1;
            confidence = 0.6;
        }

        var coreSentences = sentences.Skip(first).Take(endIdx - first + 1).ToList();
        var core = string.Join(" ", coreSentences);
        // A single very long sentence without punctuation: start at the first ask cue in its final part.
        if (TextNormalizer.WordCount(core) > MaxCoreWords)
        {
            var words = core.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var tailStart = string.Join(" ", words.Take(words.Length - MaxCoreWords)).Length;
            var m = AskStart.Matches(core).FirstOrDefault(x => x.Index >= tailStart) ?? AskStart.Matches(core).LastOrDefault();
            if (m != null && m.Index > 0) { core = Capitalize(core[m.Index..]); confidence = Math.Min(confidence, 0.75); }
            else { core = string.Join(" ", words.TakeLast(MaxCoreWords)); confidence = Math.Min(confidence, 0.6); }
        }
        core = Capitalize(Discourse.Replace(core, "").Trim());

        var contextSentences = new List<string>();
        if (restartPrefix.Length > 0) contextSentences.AddRange(Sentences(restartPrefix).Where(s => !UtteranceGate.IsQuestionSentence(s)));
        contextSentences.AddRange(sentences.Take(first).Where(s => gate.AddressedTo(s) == UtteranceGate.Addressee.Candidate));
        // A sentence that only restates the ask (the interviewer said it twice, or a revised segment repeated it) is the
        // question, not context: it must never show up again as its own context line.
        contextSentences.RemoveAll(c => RestatesAsk(c, core));
        var inTurn = string.Join(" ", contextSentences);
        var earlier = string.Join(" ", Sentences(earlierContext).Where(c => earlierContext.Trim().Length > 0 && !RestatesAsk(c, core)));
        var allContext = Join(earlier, inTurn);

        var subs = SubSplit.Split(core).Where(p => p.Length > 0)
            .Select(p => Capitalize(p.Trim().TrimEnd(',')))
            .Where(p => TextNormalizer.WordCount(p) >= 3).ToList();
        if (subs.Count < 2) subs.Clear();

        var allSentences = Sentences(allContext);
        var constraints = allSentences.Where(s => ConstraintCue.IsMatch(s)).Select(s => Trim(s, 160)).TakeLast(4).ToList();
        var entities = ctx == null ? new List<string>() :
            new[] { ctx.CompanyName }.Concat(ctx.Products).Concat(ctx.Vocabulary).Where(e => e.Length > 1)
                .Where(e => Regex.IsMatch(original, @"(^|[^\p{L}\p{N}])" + Regex.Escape(e) + @"($|[^\p{L}\p{N}])", RegexOptions.IgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();

        return new ExtractedQuestion(original, core, subs, Summary(allSentences, constraints), Tail(allContext, MaxContextChars), constraints, entities, confidence);
    }

    /// <summary>Short, readable context line for the live window: constraint sentences first, then the most recent ones.</summary>
    private static string Summary(List<string> sentences, List<string> constraints)
    {
        if (sentences.Count == 0 || sentences.All(s => s.Length == 0)) return "";
        var picked = new List<string>();
        // Most recent constraints first (closest to the ask), then the most recent other sentences.
        foreach (var s in Enumerable.Reverse(constraints).Concat(Enumerable.Reverse(sentences)))
        {
            if (picked.Contains(s)) continue;
            if (picked.Sum(p => p.Length) + s.Length > MaxSummaryChars && picked.Count > 0) break;
            picked.Add(s);
        }
        var ordered = sentences.Where(picked.Contains).Distinct().ToList();
        var text = string.Join(" ", ordered.Count > 0 ? ordered : picked);
        return Trim(text, MaxSummaryChars);
    }

    /// <summary>The sentence says (nearly) the same as the core question: same content words, or a subset of them.</summary>
    internal static bool RestatesAsk(string sentence, string core)
    {
        var a = TextNormalizer.Tokenize(sentence).ToHashSet();
        var b = TextNormalizer.Tokenize(core).ToHashSet();
        if (a.Count == 0 || b.Count == 0) return string.Equals(sentence.Trim().TrimEnd('?', '.', '!'), core.Trim().TrimEnd('?', '.', '!'), StringComparison.OrdinalIgnoreCase);
        if (a.Count >= 2 && a.IsSubsetOf(b)) return true;
        return TextNormalizer.Jaccard(a, b) >= 0.75;
    }

    private static string Join(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : a.TrimEnd() + " " + b.TrimStart();
    private static string Tail(string s, int max) => s.Length <= max ? s : "…" + s[^max..].TrimStart();
    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

/// <summary>
/// Bounded buffer of interviewer context statements awaiting a question (by turn id, so a merged/continued turn
/// replaces its own entry). Older than <see cref="MaxAgeMs"/> or beyond <see cref="MaxChars"/> is dropped.
/// </summary>
public sealed class PendingContext
{
    private readonly List<(int TurnId, string Text, long AtMs)> _items = new();
    public int MaxChars { get; init; } = 1500;
    public long MaxAgeMs { get; init; } = 120_000;

    public void Add(int turnId, string text, long nowMs)
    {
        lock (_items)
        {
            _items.RemoveAll(i => i.TurnId == turnId && turnId > 0);
            _items.Add((turnId, text, nowMs));
            while (_items.Sum(i => i.Text.Length) > MaxChars && _items.Count > 1) _items.RemoveAt(0);
        }
    }

    public void Remove(int turnId) { lock (_items) _items.RemoveAll(i => i.TurnId == turnId); }

    /// <summary>Returns and clears the fresh context (oldest first).</summary>
    public string Take(long nowMs)
    {
        lock (_items)
        {
            var text = string.Join(" ", _items.Where(i => nowMs - i.AtMs <= MaxAgeMs).Select(i => i.Text));
            _items.Clear();
            return text.Length > MaxChars ? text[^MaxChars..] : text;
        }
    }

    public int Count { get { lock (_items) return _items.Count; } }
    public void Clear() { lock (_items) _items.Clear(); }
}
