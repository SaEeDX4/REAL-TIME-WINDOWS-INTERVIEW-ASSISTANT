using InterviewAssistant.Core.Knowledge;

namespace InterviewAssistant.Tests;

public static class TestKnowledge
{
    private static readonly Lazy<KnowledgeBase> Kb = new(() => KnowledgeBase.Load(Path.Combine(AppContext.BaseDirectory, "knowledge")));
    public static KnowledgeBase Load() => Kb.Value;
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "knowledge");
}
