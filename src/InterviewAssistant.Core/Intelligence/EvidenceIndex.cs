using System.Text.RegularExpressions;
using InterviewAssistant.Core.Knowledge;

namespace InterviewAssistant.Core.Intelligence;

/// <summary>
/// The candidate's confirmed history (profile roles/achievements/summary/skills and verified stories) as searchable
/// evidence. It is the truth boundary for first-person past claims: a claim is "supported" only when one confirmed
/// evidence item covers most of its content words. Built per knowledge base — never shared between users.
/// </summary>
public sealed class EvidenceIndex
{
    private readonly List<HashSet<string>> _items = new();

    // Words that carry no topic on their own in claims/questions ("built a system", "led the project").
    private static readonly HashSet<string> Weak = new(StringComparer.Ordinal)
    {
        "built", "build", "implement", "lead", "led", "manag", "manage", "deliver", "launch", "design", "own", "creat", "create", "develop",
        "ran", "run", "responsible", "personally", "actually", "ever", "before", "previously", "experience", "work", "worked", "done",
        "system", "project", "team", "company", "role", "process", "part", "thing", "way", "end", "full", "whole", "directly", "hands-on",
        "practical", "direct", "used", "use", "one", "new", "exact", "own", "set", "up", "past", "previou", "job", "here", "time",
    };

    public EvidenceIndex(CandidateProfile profile, IEnumerable<CandidateStory> stories)
    {
        foreach (var e in profile.Experience)
        {
            Add(e.Title + " " + e.Company);
            foreach (var a in e.Achievements) Add(e.Company + " " + a);
        }
        if (!string.IsNullOrWhiteSpace(profile.Summary)) Add(profile.Summary);
        foreach (var c in profile.CoreCompetencies) Add(c);
        foreach (var s in stories) Add(s.Company + " " + s.Situation + " " + s.Action + " " + s.Result + " " + string.Join(' ', s.Skills));
    }

    public static EvidenceIndex For(KnowledgeBase kb) => new(kb.Profile, kb.Stories);

    public int Count => _items.Count;

    private void Add(string text)
    {
        var t = Topic(text);
        if (t.Count > 0) _items.Add(t);
    }

    /// <summary>Content words of a question/claim (stemmed, without question/claim scaffolding).</summary>
    public static HashSet<string> Topic(string text) =>
        new(TextNormalizer.Tokenize(text).Where(w => w.Length > 2 && !Weak.Contains(w) && !NumberWord.IsMatch(w)), StringComparer.Ordinal);

    // Numbers (digits or words) are checked separately by the validator's verified-number rule, not as topic words.
    private static readonly Regex NumberWord = new(@"^(\d[\d.,]*%?|percent|thousand|million|billion|hundred|(one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|fifteen|eighteen|twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety)(-\w+)?)$", RegexOptions.Compiled);

    /// <summary>
    /// True when one evidence item covers at least <paramref name="coverage"/> of the topic words (min. one word,
    /// two when the topic has three or more words). Empty topics are never "supported".
    /// </summary>
    public bool Supports(string text, double coverage = 0.6)
    {
        var topic = Topic(text);
        if (topic.Count == 0) return false;
        var need = Math.Max(topic.Count >= 3 ? 2 : 1, (int)Math.Ceiling(topic.Count * coverage));
        return _items.Any(item => topic.Count(item.Contains) >= need);
    }
}
