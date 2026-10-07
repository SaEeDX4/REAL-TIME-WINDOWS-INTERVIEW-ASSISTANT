using InterviewAssistant.Core.Knowledge;

namespace InterviewAssistant.Core.Intelligence;

public sealed record RetrievedContext(IReadOnlyList<CandidateStory> Stories, IReadOnlyList<KnowledgeSnippet> Snippets, BankQuestion? Reference);

/// <summary>Selects a compact, relevant slice of knowledge for one question (never the whole CV/whitepaper).</summary>
public sealed class ContextRetriever
{
    private readonly KnowledgeBase _kb;
    private readonly List<(KnowledgeSnippet Snippet, HashSet<string> Tokens)> _snippetIndex;
    private readonly Dictionary<string, CandidateStory> _storyById;

    public ContextRetriever(KnowledgeBase kb)
    {
        _kb = kb;
        _snippetIndex = kb.Snippets.Select(s => (s, new HashSet<string>(TextNormalizer.Tokenize(s.Heading + " " + s.Text)))).ToList();
        _storyById = kb.Stories.ToDictionary(s => s.Id);
    }

    public RetrievedContext Retrieve(string question, Classification cls, MatchResult match, int maxStories = 3, int maxSnippets = 6, Func<string, double>? storyPenalty = null)
    {
        var qTokens = new HashSet<string>(TextNormalizer.Tokenize(question));

        // Stories: evidence attached to the matched bank question first, then by category, then lexical overlap.
        var stories = new List<CandidateStory>();
        if (match.Question != null && match.Confidence >= MatchConfidence.Medium)
            foreach (var id in match.Question.CandidateEvidence)
                if (_storyById.TryGetValue(id, out var s) && !stories.Contains(s) && (storyPenalty?.Invoke(id) ?? 0) < 2) stories.Add(s);

        var ranked = _kb.Stories
            .Select(s => (Story: s, Score:
                (s.QuestionTypes.Contains(cls.Category) ? 2.0 : 0) +
                qTokens.Count(t => TextNormalizer.Tokenize(s.Company + " " + s.Action + " " + s.Result + " " + string.Join(' ', s.Skills)).Contains(t))
                - (storyPenalty?.Invoke(s.Id) ?? 0) * 0.8))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Story);
        foreach (var s in ranked) { if (stories.Count >= maxStories) break; if (!stories.Contains(s)) stories.Add(s); }
        if (stories.Count == 0 && cls.Mode != AnswerMode.Hypothetical) stories.AddRange(_kb.Stories.OrderBy(s => storyPenalty?.Invoke(s.Id) ?? 0).Take(2)); // relevance order, least-used first

        var snippets = _snippetIndex
            .Select(x => (x.Snippet, Score: TextNormalizer.Jaccard(qTokens, x.Tokens) + qTokens.Count(x.Tokens.Contains) * 0.05))
            .Where(x => x.Score > 0.04)
            .OrderByDescending(x => x.Score)
            .Take(maxSnippets)
            .Select(x => x.Snippet)
            .ToList();

        var reference = match.Confidence >= MatchConfidence.Medium ? match.Question : null;
        return new RetrievedContext(stories, snippets, reference);
    }
}
