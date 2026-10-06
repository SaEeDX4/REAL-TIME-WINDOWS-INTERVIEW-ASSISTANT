using InterviewAssistant.Core.Knowledge;

namespace InterviewAssistant.Core.Intelligence;

public enum MatchConfidence { None, Low, Medium, High }

public sealed record MatchResult(BankQuestion? Question, double Score, MatchConfidence Confidence, BankQuestion? RunnerUp, double RunnerUpScore);

/// <summary>
/// Local hybrid semantic matcher: BM25 over canonical question + variants + keywords, blended with
/// character-trigram similarity against the best individual phrasing. No external infrastructure.
/// </summary>
public sealed class QuestionMatcher
{
    private sealed class Doc
    {
        public required BankQuestion Question { get; init; }
        public required Dictionary<string, int> TermFreq { get; init; }
        public required int Length { get; init; }
        public required List<(HashSet<string> Tokens, HashSet<string> Trigrams)> Phrasings { get; init; }
    }

    private readonly List<Doc> _docs = new();
    private readonly Dictionary<string, double> _idf = new();
    private readonly double _avgLen;
    private const double K1 = 1.2, B = 0.6;

    public double HighThreshold { get; set; } = 0.62;
    public double MediumThreshold { get; set; } = 0.42;
    public double LowThreshold { get; set; } = 0.25;

    public QuestionMatcher(IEnumerable<BankQuestion> questions)
    {
        foreach (var q in questions)
        {
            var phrasings = new List<string> { q.CanonicalQuestion };
            phrasings.AddRange(q.SemanticVariants);
            var allTokens = phrasings.SelectMany(TextNormalizer.Tokenize).Concat(q.Keywords.Select(TextNormalizer.Stem)).ToList();
            var tf = allTokens.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
            _docs.Add(new Doc
            {
                Question = q,
                TermFreq = tf,
                Length = allTokens.Count,
                Phrasings = phrasings.Select(p => (new HashSet<string>(TextNormalizer.Tokenize(p)), TextNormalizer.CharTrigrams(p))).ToList(),
            });
        }
        _avgLen = _docs.Count == 0 ? 1 : _docs.Average(d => d.Length);
        var df = new Dictionary<string, int>();
        foreach (var d in _docs) foreach (var t in d.TermFreq.Keys) df[t] = df.GetValueOrDefault(t) + 1;
        foreach (var (t, n) in df) _idf[t] = Math.Log(1 + (_docs.Count - n + 0.5) / (n + 0.5));
    }

    public MatchResult Match(string question)
    {
        var qTokens = TextNormalizer.Tokenize(question);
        if (qTokens.Count == 0 || _docs.Count == 0) return new MatchResult(null, 0, MatchConfidence.None, null, 0);
        var qSet = new HashSet<string>(qTokens);
        var qTri = TextNormalizer.CharTrigrams(question);

        // Max achievable BM25 for the query (self-match) to normalise into [0,1].
        double maxBm25 = qSet.Sum(t => _idf.GetValueOrDefault(t, Math.Log(1 + _docs.Count)) * (K1 + 1) / (1 + K1));

        var scored = new List<(Doc Doc, double Score)>(_docs.Count);
        foreach (var d in _docs)
        {
            double bm25 = 0;
            foreach (var t in qSet)
            {
                if (!d.TermFreq.TryGetValue(t, out var f)) continue;
                var idf = _idf[t];
                bm25 += idf * f * (K1 + 1) / (f + K1 * (1 - B + B * d.Length / _avgLen));
            }
            var bm25Norm = Math.Min(1.0, bm25 / Math.Max(maxBm25, 1e-6));

            double bestPhr = 0;
            foreach (var (tok, tri) in d.Phrasings)
            {
                var tokSim = TextNormalizer.Jaccard(qSet, tok);
                var triSim = TextNormalizer.Jaccard(qTri, tri);
                bestPhr = Math.Max(bestPhr, 0.55 * tokSim + 0.45 * triSim);
            }
            scored.Add((d, 0.5 * bm25Norm + 0.5 * Math.Min(1.0, bestPhr * 1.35)));
        }
        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        var best = scored[0];
        var second = scored.Count > 1 ? scored[1] : (best.Doc, 0);

        var conf = best.Score >= HighThreshold ? MatchConfidence.High
                 : best.Score >= MediumThreshold ? MatchConfidence.Medium
                 : best.Score >= LowThreshold ? MatchConfidence.Low : MatchConfidence.None;
        // Ambiguity: two different intents score almost equally -> don't trust the cache blindly.
        if (conf == MatchConfidence.High && second.Item2 > 0 && best.Score - second.Item2 < 0.03) conf = MatchConfidence.Medium;

        return new MatchResult(best.Doc.Question, best.Score, conf, scored.Count > 1 ? second.Doc.Question : null, second.Item2);
    }
}
