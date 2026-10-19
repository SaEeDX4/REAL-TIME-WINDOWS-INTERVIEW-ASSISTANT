using InterviewAssistant.Core.Intelligence;

namespace InterviewAssistant.Core.Turn;

/// <summary>
/// Prevents a second answer for the same question caused by transcript revisions, repeated finals or
/// punctuation/word corrections. Compares token-set fingerprints within a time window. Bounded memory.
/// A follow-up ("Tell me more.", "Why?") has no topic of its own: it asks about the answer before it. Its entry is therefore
/// scoped by the last question that had been ANSWERED when it was asked: the same words after another completed answer
/// (including the answer to the first "Tell me more.") are a new ask, while the same transcript echoed for the same turn —
/// before anything new was answered — still has the same scope and is suppressed.
/// </summary>
public sealed class DuplicateGuard
{
    private readonly LinkedList<(HashSet<string> Tokens, long AtMs, string Text, int? Scope)> _recent = new();
    public int Capacity { get; init; } = 12;
    public long WindowMs { get; init; } = 30_000; // revisions arrive within seconds; a re-ask minutes later deserves an answer
    public double SimilarityThreshold { get; init; } = 0.8;
    public int SuppressedCount { get; private set; }

    /// <param name="scope">For a follow-up: the last answered question it follows (e.g. that record's index); null otherwise.
    /// A scoped query only matches entries with the same scope; an unscoped one matches every entry, as before.</param>
    public bool IsDuplicate(string question, long nowMs, int? scope = null)
    {
        var tokens = new HashSet<string>(TextNormalizer.Fingerprint(question).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        foreach (var r in _recent)
        {
            if (nowMs - r.AtMs > WindowMs) continue;
            if (scope != null && r.Scope != scope) continue;
            var sim = TextNormalizer.Jaccard(tokens, r.Tokens);
            // Also treat "same question + one extra word" as duplicate (containment).
            var containment = tokens.Count == 0 ? 0 : (double)tokens.Count(r.Tokens.Contains) / Math.Max(tokens.Count, r.Tokens.Count);
            if (sim >= SimilarityThreshold || (containment >= 0.85 && Math.Abs(tokens.Count - r.Tokens.Count) <= 1))
            {
                SuppressedCount++;
                return true;
            }
        }
        return false;
    }

    public void Register(string question, long nowMs, int? scope = null)
    {
        var tokens = new HashSet<string>(TextNormalizer.Fingerprint(question).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        _recent.AddFirst((tokens, nowMs, question, scope));
        while (_recent.Count > Capacity) _recent.RemoveLast();
    }

    public void Clear() => _recent.Clear();
}
