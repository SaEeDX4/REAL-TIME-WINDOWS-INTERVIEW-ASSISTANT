using InterviewAssistant.Core.Intelligence;

namespace InterviewAssistant.Core.Turn;

/// <summary>
/// Prevents a second answer for the same question caused by transcript revisions, repeated finals or
/// punctuation/word corrections. Compares token-set fingerprints within a time window. Bounded memory.
/// </summary>
public sealed class DuplicateGuard
{
    private readonly LinkedList<(HashSet<string> Tokens, long AtMs, string Text)> _recent = new();
    public int Capacity { get; init; } = 12;
    public long WindowMs { get; init; } = 30_000; // revisions arrive within seconds; a re-ask minutes later deserves an answer
    public double SimilarityThreshold { get; init; } = 0.8;
    public int SuppressedCount { get; private set; }

    public bool IsDuplicate(string question, long nowMs)
    {
        var tokens = new HashSet<string>(TextNormalizer.Fingerprint(question).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        foreach (var r in _recent)
        {
            if (nowMs - r.AtMs > WindowMs) continue;
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

    public void Register(string question, long nowMs)
    {
        var tokens = new HashSet<string>(TextNormalizer.Fingerprint(question).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        _recent.AddFirst((tokens, nowMs, question));
        while (_recent.Count > Capacity) _recent.RemoveLast();
    }

    public void Clear() => _recent.Clear();
}
