using System.Text.Json;
using InterviewAssistant.Core.Diagnostics;
using InterviewAssistant.Core.Domain;
using InterviewAssistant.Core.Intelligence;
using InterviewAssistant.Core.Knowledge;
using InterviewAssistant.Core.Live;

namespace InterviewAssistant.Core.Reports;

public sealed class ReportQuestion
{
    public int Index { get; init; }
    public string OriginalWording { get; init; } = "";
    public string DetectedLanguage { get; init; } = "und";
    public string AnswerLanguage { get; init; } = "en";
    public string Category { get; init; } = "";
    public string Mode { get; init; } = "";
    public List<string> SuggestionsShown { get; init; } = new();
    public string Presentation { get; init; } = "Answer";
    public string Source { get; init; } = "";
    public long FirstBulletMs { get; init; }
    public int? FollowUpOf { get; init; }
    public bool ReferencesEarlier { get; init; }
}

/// <summary>
/// Post-interview report. Observations (what the app saw) are kept separate from inferences (labelled).
/// With the candidate microphone OFF (default) the report never claims to know what the candidate said.
/// </summary>
public sealed class InterviewReport
{
    public string SessionId { get; init; } = "";
    public string ProfileLabel { get; init; } = "";
    public string TargetLabel { get; init; } = "";
    public DateTime StartedUtc { get; init; }
    public DateTime EndedUtc { get; init; }
    public string ReportLanguage { get; init; } = "en";
    public bool CandidateMicrophoneEnabled { get; init; }
    public List<ReportQuestion> Questions { get; init; } = new();
    public Dictionary<string, int> CategoryCounts { get; init; } = new();
    public List<string> LanguagesUsed { get; init; } = new();
    public List<string> DifficultyAreas { get; init; } = new();
    public List<(string Label, int Count)> StoriesRecommended { get; init; } = new();
    public List<(string Metric, int Count)> MetricsRecommended { get; init; } = new();
    public List<string> RepeatedTopics { get; init; } = new();
    public List<string> TopicsNotCovered { get; init; } = new();
    public List<string> GapQuestions { get; init; } = new();
    public List<InterruptionEvent> Interruptions { get; init; } = new();
    public long LatencyP50Ms { get; init; } = -1;
    public long LatencyP95Ms { get; init; } = -1;
    public List<string> TranscriptionIssues { get; init; } = new();
    public List<string> StrengthsOfPreparation { get; init; } = new();
    public List<string> PreparationWeaknesses { get; init; } = new();
    public List<string> FollowUpPreparation { get; init; } = new();
    public List<string> ThankYouThemes { get; init; } = new();
    public List<string> UnusedRelevantStories { get; init; } = new();

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true });
}

public static class ReportBuilder
{
    public static InterviewReport Build(SessionMemory memory, KnowledgeBase kb, SessionMetrics? metrics, string sessionId, DateTime startedUtc, DateTime endedUtc,
        string reportLanguage = "en", PreparedPack? pack = null, int reconnects = 0, int duplicatesSuppressed = 0, string profileLabel = "", string targetLabel = "")
    {
        var qs = memory.Questions.Select(q => new ReportQuestion
        {
            Index = q.Index, OriginalWording = q.Original, DetectedLanguage = q.DetectedLanguage, AnswerLanguage = q.AnswerLanguage, Category = q.Category, Mode = q.Mode,
            SuggestionsShown = q.AnswerBullets.Count > 0 ? q.AnswerBullets.ToList() : q.CoachKeywords.ToList(), Presentation = q.Presentation, Source = q.Source,
            FirstBulletMs = q.FirstBulletLatencyMs, FollowUpOf = q.FollowUpOf, ReferencesEarlier = q.ReferencesEarlierAnswer,
        }).ToList();

        var storyLabel = kb.Stories.ToDictionary(s => s.Id, s => $"{s.Company}: {Short(s.Action, 10)}");
        var stories = memory.StoryUse.OrderByDescending(kv => kv.Value).Select(kv => (storyLabel.GetValueOrDefault(kv.Key, kv.Key), kv.Value)).ToList();
        var askedText = string.Join(" ", memory.Questions.Select(q => q.Original)).ToLowerInvariant();
        var askedTokens = new HashSet<string>(TextNormalizer.Tokenize(askedText));

        var notCovered = new List<string>();
        if (pack != null)
            foreach (var m in pack.MatchMap)
            {
                var key = TextNormalizer.Tokenize(m.Requirement).Where(t => t.Length > 4).Take(4).ToList();
                if (key.Count > 0 && !key.Any(askedTokens.Contains)) notCovered.Add(Short(m.Requirement, 12));
            }

        var gapQs = memory.Questions.Where(q => q.Mode == "Bridge" || q.Category == "GAP_EXPERIENCE").Select(q => q.Original).ToList();
        var difficult = new List<string>();
        difficult.AddRange(gapQs.Select(g => "Experience gap: " + Short(g, 14)));
        difficult.AddRange(memory.Questions.Where(q => q.Source is "Error" or "CacheFallback").Select(q => "No live AI answer available: " + Short(q.Original, 12)));
        difficult.AddRange(memory.Questions.Where(q => q.ReferencesEarlierAnswer).Select(q => "Interviewer referred back to an earlier answer: " + Short(q.Original, 12)));

        var latencies = memory.Questions.Select(q => q.FirstBulletLatencyMs).Where(l => l >= 0).ToList();
        var issues = new List<string>();
        var undetected = memory.Questions.Count(q => q.DetectedLanguage == "und");
        if (undetected > 0) issues.Add($"{undetected} question(s) were too short to detect the language reliably.");
        var veryShort = memory.Questions.Count(q => TextNormalizer.WordCount(q.Original) <= 3);
        if (veryShort > 0) issues.Add($"{veryShort} very short question(s) — possibly partial transcription or follow-ups.");
        if (reconnects > 0) issues.Add($"{reconnects} transcription reconnect(s) during the session.");
        if (duplicatesSuppressed > 0) issues.Add($"{duplicatesSuppressed} duplicate transcript(s) suppressed.");

        var prepared = memory.Questions.Count(q => q.Source == "Cache");
        var strengths = new List<string>();
        if (memory.Questions.Count > 0) strengths.Add($"{prepared} of {memory.Questions.Count} questions matched prepared answers instantly.");
        if (stories.Count > 0) strengths.Add($"{stories.Count} different verified stories were suggested.");
        if (memory.Questions.Count > 0 && memory.StoryUse.Values.DefaultIfEmpty(0).Max() <= 2) strengths.Add("No story was suggested more than twice (low repetition).");
        var weaknesses = new List<string>();
        var unprepared = memory.Questions.Where(q => q.MatchedQuestionId == null || q.Source == "Llm").Select(q => q.Category).GroupBy(c => c).Where(g => g.Count() >= 2).Select(g => g.Key.ToLowerInvariant()).ToList();
        if (unprepared.Count > 0) weaknesses.Add("Categories that needed live AI repeatedly (consider adding to preparation): " + string.Join(", ", unprepared));
        if (gapQs.Count > 0) weaknesses.Add($"{gapQs.Count} question(s) touched experience the profile does not show.");
        var overused = memory.StoryUse.Where(kv => kv.Value >= 3).Select(kv => storyLabel.GetValueOrDefault(kv.Key, kv.Key)).ToList();
        if (overused.Count > 0) weaknesses.Add("Stories suggested 3+ times: " + string.Join("; ", overused));

        var followUp = gapQs.Select(g => "Prepare a concise bridge + example for: " + Short(g, 12)).Concat(notCovered.Take(5).Select(n => "Not asked but in the job description: " + n)).ToList();
        var themes = memory.TopicCounts.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"Topic discussed: {kv.Key.ToLowerInvariant().Replace('_', ' ')}").ToList();
        if (kb.Context.CompanyName.Length > 0) themes.Insert(0, $"Thank them for the conversation about {kb.Context.RoleTitle} at {kb.Context.CompanyName}.");
        if (memory.CompanyTopics.Count > 0) themes.Add("Reference what you learned about: " + string.Join(", ", memory.CompanyTopics.Take(3)));
        var unused = kb.Stories.Where(s => !memory.StoryUse.ContainsKey(s.Id)).Take(5).Select(s => storyLabel[s.Id]).ToList();

        return new InterviewReport
        {
            SessionId = sessionId, ProfileLabel = profileLabel, TargetLabel = targetLabel, StartedUtc = startedUtc, EndedUtc = endedUtc, ReportLanguage = reportLanguage,
            CandidateMicrophoneEnabled = false, Questions = qs,
            CategoryCounts = memory.Questions.GroupBy(q => q.Category).ToDictionary(g => g.Key, g => g.Count()),
            LanguagesUsed = memory.Questions.Select(q => q.DetectedLanguage).Where(l => l != "und").Distinct().ToList(),
            DifficultyAreas = difficult, StoriesRecommended = stories,
            MetricsRecommended = memory.MetricUse.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).ToList(),
            RepeatedTopics = memory.TopicCounts.Where(kv => kv.Value >= 3).Select(kv => $"{kv.Key.ToLowerInvariant().Replace('_', ' ')} ×{kv.Value}").ToList(),
            TopicsNotCovered = notCovered, GapQuestions = gapQs, Interruptions = memory.Interruptions.ToList(),
            LatencyP50Ms = SessionMetrics.Percentile(latencies, 50), LatencyP95Ms = SessionMetrics.Percentile(latencies, 95),
            TranscriptionIssues = issues, StrengthsOfPreparation = strengths, PreparationWeaknesses = weaknesses, FollowUpPreparation = followUp,
            ThankYouThemes = themes, UnusedRelevantStories = unused,
        };
    }

    private static string Short(string s, int words) { var w = s.Split(' ', StringSplitOptions.RemoveEmptyEntries); return w.Length <= words ? s.TrimEnd('.') : string.Join(' ', w.Take(words)) + "…"; }
}
