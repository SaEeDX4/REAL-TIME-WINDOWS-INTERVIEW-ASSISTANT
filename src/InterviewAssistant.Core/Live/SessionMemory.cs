using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using InterviewAssistant.Core.Intelligence;
using InterviewAssistant.Core.Knowledge;

namespace InterviewAssistant.Core.Live;

public sealed class QuestionRecord
{
    public int Index { get; set; }
    public string Original { get; set; } = "";
    public string DetectedLanguage { get; set; } = "und";
    public string AnswerLanguage { get; set; } = "en";
    public string Category { get; set; } = "";
    public string Mode { get; set; } = "";
    /// <summary>Language-independent intent signature: category + canonical bank id (if matched) + entities.</summary>
    public string IntentKey { get; set; } = "";
    public string? MatchedQuestionId { get; set; }
    public List<string> Entities { get; set; } = new();
    public List<string> AnswerBullets { get; set; } = new();
    public List<string> CoachKeywords { get; set; } = new();
    public List<string> StoryIds { get; set; } = new();
    public List<string> Metrics { get; set; } = new();
    public int? FollowUpOf { get; set; }
    public bool ReferencesEarlierAnswer { get; set; }
    public string Source { get; set; } = "";
    public string Presentation { get; set; } = "Answer";
    public long FirstBulletLatencyMs { get; set; } = -1;
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    public bool Truncated { get; set; }
}

public sealed class InterruptionEvent
{
    public int AfterQuestionIndex { get; set; }
    public double Confidence { get; set; }
    public long ElapsedMs { get; set; }
    public long ExpectedMs { get; set; }
    public string Note { get; set; } = "";
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Bounded memory of the whole interview as ONE conversation. Language-independent: intent keys, categories,
/// entities and story ids are stored separately from the original wording, so an English question and a German
/// follow-up share context. Old records are compacted (answer text dropped) instead of growing forever, and a
/// rolling summary keeps the prompt size constant. Serializable so it survives disconnects (encrypted by the store).
/// </summary>
public sealed class SessionMemory
{
    public List<QuestionRecord> Questions { get; set; } = new();
    public List<InterruptionEvent> Interruptions { get; set; } = new();
    public Dictionary<string, int> StoryUse { get; set; } = new();
    public Dictionary<string, int> StoryLastUsedAt { get; set; } = new();
    public Dictionary<string, int> MetricUse { get; set; } = new();
    public Dictionary<string, int> TopicCounts { get; set; } = new();
    public HashSet<string> CompanyTopics { get; set; } = new();
    public List<string> UnresolvedTopics { get; set; } = new();
    public string RollingSummary { get; set; } = "";
    public int MaxDetailedRecords { get; set; } = 12;
    public int MaxRecords { get; set; } = 400;

    private static readonly Regex AlreadyMentioned = new(
        @"(you (already|just) (mentioned|said|told|talked about)|as you (said|mentioned)|you mentioned (that|earlier)|you've mentioned|apart from|other than|besides (that|the)|another example|a different example" +
        @"|sie haben (bereits|schon) (erwähnt|gesagt)|ein anderes beispiel|vous avez (déjà )?(mentionné|dit|parlé)|un autre exemple|ya (mencion|dij)|otro ejemplo" +
        @"|voc[êe] j[áa] (mencionou|disse)|outro exemplo|hai gi[àa] (menzionato|detto)|un altro esempio|قبلاً (گفتید|اشاره کردید)|مثال دیگری|ذكرت|مثال آخر|你(已经)?提到|另一个例子|आपने (पहले )?बताया|दूसरा उदाहरण)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool ReferencesEarlier(string question) => AlreadyMentioned.IsMatch(question);

    public QuestionRecord? Last => Questions.LastOrDefault();

    public QuestionRecord Add(QuestionRecord r, IEnumerable<string>? storyMetrics = null)
    {
        r.Index = Questions.Count == 0 ? 1 : Questions[^1].Index + 1;
        Questions.Add(r);
        foreach (var s in r.StoryIds) { StoryUse[s] = StoryUse.GetValueOrDefault(s) + 1; StoryLastUsedAt[s] = r.Index; }
        foreach (var m in r.Metrics.Concat(storyMetrics ?? Enumerable.Empty<string>()).Distinct()) MetricUse[m] = MetricUse.GetValueOrDefault(m) + 1;
        if (r.Category.Length > 0) TopicCounts[r.Category] = TopicCounts.GetValueOrDefault(r.Category) + 1;
        Compact();
        return r;
    }

    /// <summary>Records answer content after it has been shown (bullets arrive after the record is created).</summary>
    public void CompleteAnswer(QuestionRecord r, IEnumerable<string> bullets, IEnumerable<string> storyIds, IEnumerable<string> metricsInAnswer)
    {
        r.AnswerBullets = bullets.ToList();
        foreach (var s in storyIds.Except(r.StoryIds)) { r.StoryIds.Add(s); StoryUse[s] = StoryUse.GetValueOrDefault(s) + 1; StoryLastUsedAt[s] = r.Index; }
        foreach (var m in metricsInAnswer.Except(r.Metrics)) { r.Metrics.Add(m); MetricUse[m] = MetricUse.GetValueOrDefault(m) + 1; }
        Compact();
    }

    /// <summary>Stories used within the last <paramref name="window"/> questions (recency) or more than once overall.</summary>
    public HashSet<string> RecentlyUsedStories(int window = 3)
    {
        var cut = (Last?.Index ?? 0) - window;
        return StoryLastUsedAt.Where(kv => kv.Value > cut || StoryUse.GetValueOrDefault(kv.Key) >= 2).Select(kv => kv.Key).ToHashSet();
    }

    public IEnumerable<string> OverusedMetrics(int threshold = 2) => MetricUse.Where(kv => kv.Value >= threshold).Select(kv => kv.Key);

    /// <summary>Penalty for re-using a story: higher when used recently/often. Used to rank evidence.</summary>
    public double StoryPenalty(string storyId)
    {
        if (!StoryUse.TryGetValue(storyId, out var n)) return 0;
        var age = (Last?.Index ?? 0) - StoryLastUsedAt.GetValueOrDefault(storyId);
        return n * 1.0 + (age <= 2 ? 2.0 : age <= 5 ? 1.0 : 0.2);
    }

    /// <summary>Compact, bounded context for the prompt: rolling summary + last few exchanges + anti-repetition hints.</summary>
    public string BuildPromptContext(int recent = 4)
    {
        var sb = new StringBuilder();
        if (RollingSummary.Length > 0) sb.AppendLine("Earlier in this interview: " + RollingSummary);
        foreach (var q in Questions.TakeLast(recent))
        {
            sb.Append($"Q{q.Index}{(q.DetectedLanguage != "und" ? $" [{q.DetectedLanguage}]" : "")}: {Trim(q.Original, 30)}");
            if (q.AnswerBullets.Count > 0) sb.Append(" → suggested: " + Trim(string.Join(" ", q.AnswerBullets), 35));
            sb.AppendLine();
        }
        var used = StoryUse.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();
        if (used.Count > 0) sb.AppendLine("Stories already used (prefer a different verified story if one fits): " + string.Join(", ", used));
        var metrics = OverusedMetrics(1).ToList();
        if (metrics.Count > 0) sb.AppendLine("Metrics already mentioned (do not repeat unless asked): " + string.Join(", ", metrics.Take(12)));
        if (Interruptions.Count > 0) sb.AppendLine($"Interviewer pace: {Interruptions.Count} fast-follow/interruption events — keep answers tight.");
        return sb.ToString().TrimEnd();
    }

    private void Compact()
    {
        var detailed = Questions.Where(q => !q.Truncated).ToList();
        if (detailed.Count > MaxDetailedRecords)
        {
            foreach (var q in detailed.Take(detailed.Count - MaxDetailedRecords))
            {
                q.Truncated = true;
                q.AnswerBullets = q.AnswerBullets.Take(1).Select(b => Trim(b, 12)).ToList();
            }
            var topics = TopicCounts.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Key.ToLowerInvariant()}×{kv.Value}");
            RollingSummary = $"{Questions.Count(q => q.Truncated)} earlier questions; topics: {string.Join(", ", topics)}" +
                             (CompanyTopics.Count > 0 ? "; company topics: " + string.Join(", ", CompanyTopics.Take(8)) : "") + ".";
        }
        if (Questions.Count > MaxRecords) Questions.RemoveRange(0, Questions.Count - MaxRecords);
        if (Interruptions.Count > 200) Interruptions.RemoveRange(0, Interruptions.Count - 200);
    }

    private static string Trim(string s, int words) { var w = s.Split(' ', StringSplitOptions.RemoveEmptyEntries); return w.Length <= words ? s : string.Join(' ', w.Take(words)) + "…"; }

    public string ToJson() => JsonSerializer.Serialize(this);
    public static SessionMemory FromJson(string json) => JsonSerializer.Deserialize<SessionMemory>(json) ?? new SessionMemory();

    /// <summary>Language-independent intent key (bank id when matched, else category + sorted content tokens).</summary>
    public static string IntentKeyFor(string question, string category, BankQuestion? match) =>
        match != null ? "Q:" + match.QuestionId : category + ":" + TextNormalizer.Fingerprint(question);
}
