using System.Text;
using InterviewAssistant.Core.Knowledge;

namespace InterviewAssistant.Core.Intelligence;

public sealed record ChatMessage(string Role, string Content);

public sealed record ConversationTurn(string Question, string AnswerSummary);

/// <summary>Builds the compact LLM request: static system prompt (cache-friendly prefix) + small per-question context.</summary>
public sealed class PromptBuilder
{
    private readonly KnowledgeBase _kb;
    private readonly string _profileDigest;

    public PromptBuilder(KnowledgeBase kb)
    {
        _kb = kb;
        var p = kb.Profile;
        var sb = new StringBuilder();
        sb.AppendLine($"CANDIDATE: {p.Name} — {p.Headline}. {p.Summary}");
        foreach (var e in p.Experience) sb.AppendLine($"- {e.Title}, {e.Company} ({e.Location}, {e.Period})");
        sb.AppendLine("Education: MBA Marketing Strategy; BSc Computer Software Engineering (University of Tehran). Languages: Persian native, English advanced, Lithuanian A1.");
        sb.AppendLine("Competencies: " + string.Join("; ", p.CoreCompetencies));
        sb.AppendLine("KNOWN GAPS (never claim these as past work): " + string.Join(" ", p.KnownGaps));
        _profileDigest = sb.ToString();
    }

    /// <summary>Set per request by the engine (answer-language instruction).</summary>
    public string? LanguageRule { get; set; }
    /// <summary>Set per request by the engine: bounded rolling interview memory.</summary>
    public string? MemoryContext { get; set; }
    /// <summary>Set per request: extra instruction (e.g. avoid a story the interviewer already heard).</summary>
    public string? ExtraInstruction { get; set; }

    public IReadOnlyList<ChatMessage> Build(string question, Classification cls, RetrievedContext ctx, AnswerStyle style, IReadOnlyList<ConversationTurn> history)
    {
        var spec = AnswerStyleSpec.For(style);
        var system = _kb.SystemPromptTemplate
            .Replace("{BULLET_COUNT}", spec.BulletCount)
            .Replace("{WORDS_PER_BULLET}", spec.WordsPerBullet)
            .Replace("{TOTAL_WORDS}", spec.TotalWords)
            .Replace("{FORMAT_OVERRIDE}", spec.Override)
            .Replace("{CANDIDATE}", _kb.Context.CandidateName)
            .Replace("{ROLE}", _kb.Context.RoleTitle)
            .Replace("{AT_COMPANY}", string.IsNullOrWhiteSpace(_kb.Context.CompanyName) ? "" : " at " + _kb.Context.CompanyName)
            .Replace("{POSITIONING}", _kb.Context.Positioning.Length > 0 ? _kb.Context.Positioning : "Use the candidate's verified experience where it genuinely strengthens the answer.")
            .Replace("{DOMAIN_GUIDANCE}", _kb.Context.DomainGuidance.Length > 0 ? _kb.Context.DomainGuidance : "Give the strongest professional answer for this role.")
            .Replace("{LANGUAGE_RULE}", LanguageRule ?? "")
            + "\n\n" + _profileDigest;

        var user = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(MemoryContext))
        {
            user.AppendLine("INTERVIEW MEMORY (whole conversation so far, may span several languages):");
            user.AppendLine(MemoryContext);
            user.AppendLine();
        }
        else if (history.Count > 0)
        {
            user.AppendLine("RECENT INTERVIEW CONTEXT (oldest first):");
            foreach (var t in history) user.AppendLine($"Q: {t.Question}\nA (summary): {t.AnswerSummary}");
            user.AppendLine();
        }
        if (ctx.Stories.Count > 0)
        {
            user.AppendLine("CANDIDATE EVIDENCE (verified résumé facts; the only allowed source for past claims and numbers):");
            foreach (var s in ctx.Stories) user.AppendLine("- " + s.ToEvidenceLine());
            user.AppendLine();
        }
        if (ctx.Snippets.Count > 0)
        {
            user.AppendLine("DOMAIN / COMPANY NOTES:");
            foreach (var s in ctx.Snippets) user.AppendLine($"- ({s.Source} › {s.Heading}) {s.Text}");
            user.AppendLine();
        }
        if (ctx.Reference != null)
        {
            user.AppendLine("PREPARED REFERENCE ANSWER for a similar question (reuse if it fits; adapt to the exact question):");
            foreach (var b in ctx.Reference.ShortBullets) user.AppendLine("• " + b);
            if (!string.IsNullOrEmpty(ctx.Reference.GapWarning)) user.AppendLine("Gap note: " + ctx.Reference.GapWarning);
            user.AppendLine();
        }
        user.AppendLine($"SUGGESTED MODE: {cls.Mode.ToString().ToUpperInvariant()} (override only if clearly wrong). Category: {cls.Category}." + (cls.IsFollowUp ? " This is a FOLLOW-UP to the most recent question above." : ""));
        if (!string.IsNullOrWhiteSpace(ExtraInstruction)) user.AppendLine(ExtraInstruction);
        user.AppendLine($"INTERVIEWER QUESTION: \"{question}\"");

        return new[] { new ChatMessage("system", system), new ChatMessage("user", user.ToString()) };
    }
}
