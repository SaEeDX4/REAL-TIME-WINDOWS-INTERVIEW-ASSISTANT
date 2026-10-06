using InterviewAssistant.Core.Intelligence;

namespace InterviewAssistant.Core.Turn;

/// <summary>Small rolling context (last N Q/A summaries) for follow-up interpretation. Bounded.</summary>
public sealed class ConversationMemory
{
    private readonly LinkedList<ConversationTurn> _turns = new();
    public int Capacity { get; init; } = 5;

    public void Add(string question, IEnumerable<string> answerBullets)
    {
        var summary = string.Join(" ", answerBullets.Take(3).Select(b => b.Length > 110 ? b[..110] + "…" : b));
        _turns.AddLast(new ConversationTurn(question, summary));
        while (_turns.Count > Capacity) _turns.RemoveFirst();
    }

    public IReadOnlyList<ConversationTurn> Snapshot() => _turns.ToList();
    public void Clear() => _turns.Clear();
    public int Count => _turns.Count;
}
