using System.Security.Cryptography;
using System.Text.RegularExpressions;
using InterviewAssistant.Core.Ingestion;
using InterviewAssistant.Core.Knowledge;
using InterviewAssistant.Core.Languages;

namespace InterviewAssistant.Core.Domain;

public sealed record IngestResult(bool Ok, string? Error, SourceDocument? Document, ParsedResume? Parsed);

/// <summary>Creates profiles from source documents, builds stories from CONFIRMED facts, and converts to engine shapes.</summary>
public static class ProfileService
{
    public static SourceDocument CreateDocument(string fileName, DocumentKind kind, string text, bool needsOcr = false)
    {
        var guarded = PromptInjectionGuard.Sanitize(text);
        var doc = new SourceDocument
        {
            FileName = UploadValidator.SanitizeFileName(fileName), Kind = kind, Text = guarded.Text, NeedsOcr = needsOcr,
            Sha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant(),
            Language = LanguageDetector.DetectOr(guarded.Text, "und", 0.4),
        };
        doc.SecurityFlags.AddRange(guarded.Flags);
        return doc;
    }

    /// <summary>Validates + extracts an uploaded file (PDF/DOCX/TXT) into a SourceDocument.</summary>
    public static (SourceDocument? Doc, string? Error) Upload(string fileName, byte[] bytes, DocumentKind kind, IUploadScanner? scanner = null)
    {
        var check = UploadValidator.Validate(fileName, bytes, scanner);
        if (!check.Ok) return (null, check.Error);
        ExtractedText text;
        try { text = DocumentTextExtractor.Extract(check.Type, bytes); }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or ArgumentException or InvalidOperationException or IOException)
        { return (null, "The document could not be read: " + ex.Message); }
        if (text.NeedsOcr) return (null, "This PDF contains only images (scanned). Please upload a text-based PDF, DOCX, or paste the text.");
        return (CreateDocument(check.SafeFileName, kind, text.Text, text.NeedsOcr), null);
    }

    /// <summary>Adds a résumé to a profile: parses facts (status = Source, awaiting user confirmation).</summary>
    public static ParsedResume AddResume(CandidateProfileRecord profile, SourceDocument doc)
    {
        profile.Documents.Add(doc);
        var parsed = ResumeParser.Parse(doc);
        if (profile.Name.Length == 0) profile.Name = parsed.Name;
        if (profile.Headline.Length == 0) profile.Headline = parsed.Headline;
        if (profile.Location.Length == 0) profile.Location = parsed.Location;
        // De-duplicate facts across multiple CV versions (same kind + normalised text).
        var existing = profile.Facts.Select(f => Key(f)).ToHashSet();
        foreach (var f in parsed.Facts) if (existing.Add(Key(f))) profile.Facts.Add(f);
        profile.UpdatedUtc = DateTime.UtcNow;
        return parsed;
    }

    private static string Key(ProfileFact f) => f.Kind + "|" + Regex.Replace(f.Text.ToLowerInvariant(), @"\W+", " ").Trim();

    /// <summary>Rebuilds the story bank from confirmed achievements (one story per achievement; nothing invented).</summary>
    public static void RebuildStories(CandidateProfileRecord profile)
    {
        profile.Stories = profile.ConfirmedFacts
            .Where(f => f.Kind is FactKind.Achievement or FactKind.Project)
            .Select((f, i) => new Story
            {
                Id = "s" + f.Id, Company = f.Company, Role = f.Title,
                Situation = f.Company.Length > 0 ? $"As {f.Title} at {f.Company}" : "",
                Action = f.Text, Result = string.Join(", ", f.Metrics),
                Metrics = f.Metrics.ToList(), Skills = StoryTopics(f.Text), QuestionTypes = StoryQuestionTypes(f.Text), FactIds = new() { f.Id },
            }).ToList();
    }

    private static readonly (string Topic, string Pattern)[] Topics =
    {
        ("growth", @"grow|grew|increas|expand|scal|revenue|sales|market share|users|customers|traders"),
        ("retention", @"retention|churn|loyal|satisfaction|nps"),
        ("leadership", @"led|lead|team|mentor|train|hired|recruit|onboard|managed"),
        ("cost", @"cost|budget|saving|reduc|negotiat|margin|efficien"),
        ("launch", @"launch|go-to-market|gtm|introduc|release|rollout|new product"),
        ("data", @"analy|data|spss|excel|insight|research|forecast"),
        ("customer", @"customer|user|ux|experience|support|service"),
        ("partnership", @"partner|alliance|co-brand|deal|contract|vendor"),
        ("reliability", @"downtime|uptime|infrastructure|reliab|stabil|incident"),
        ("process", @"process|crm|system|implement|automat|tool|workflow"),
        ("compliance", @"complian|regulat|risk|audit|legal"),
    };

    public static List<string> StoryTopics(string text) => Topics.Where(t => Regex.IsMatch(text, t.Pattern, RegexOptions.IgnoreCase)).Select(t => t.Topic).ToList();

    private static List<string> StoryQuestionTypes(string text)
    {
        var map = new Dictionary<string, string[]> {
            ["growth"] = new[] { "METRICS", "PRODUCT_STRATEGY" }, ["retention"] = new[] { "METRICS" }, ["leadership"] = new[] { "LEADERSHIP" },
            ["cost"] = new[] { "GENERAL_BUSINESS" }, ["launch"] = new[] { "PRODUCT_STRATEGY" }, ["data"] = new[] { "METRICS" },
            ["customer"] = new[] { "PRODUCT_DISCOVERY" }, ["partnership"] = new[] { "STAKEHOLDER" }, ["reliability"] = new[] { "TECHNICAL_CONCEPT" },
            ["process"] = new[] { "PRODUCT_EXECUTION" }, ["compliance"] = new[] { "COMPLIANCE" } };
        return StoryTopics(text).SelectMany(t => map[t]).Append("BEHAVIOURAL").Append("RESUME_EXPERIENCE").Distinct().ToList();
    }

    /// <summary>Engine-facing profile containing ONLY user-confirmed facts (the truth boundary for live answers).</summary>
    public static CandidateProfile ToEngineProfile(CandidateProfileRecord p, IEnumerable<string>? knownGaps = null)
    {
        var confirmed = p.ConfirmedFacts.ToList();
        var roles = confirmed.Where(f => f.Kind == FactKind.Role).ToList();
        var numbers = new HashSet<string>();
        foreach (var f in confirmed)
        {
            foreach (var m in f.Metrics.Concat(ResumeParser.ExtractMetrics(f.Text))) { numbers.Add(m); numbers.Add(Regex.Replace(m, @"[^\d.,]", "")); }
            foreach (Match m in Regex.Matches(f.Text, @"\b\d+\+?\s*(years?|months?)\b", RegexOptions.IgnoreCase)) numbers.Add(m.Value);
            foreach (Match m in Regex.Matches(f.Period, @"(19|20)\d{2}")) numbers.Add(m.Value); // confirmed employment years
        }
        var yrs = YearsOfExperience(p);
        if (yrs > 0) { numbers.Add(yrs + "+"); numbers.Add(yrs.ToString()); }
        var entities = roles.Select(r => r.Company).Concat(roles.Select(r => r.Company.Split(' ')[0]))
            .Concat(confirmed.Where(f => f.Kind == FactKind.Education).Select(f => f.Company))
            .Concat(confirmed.Where(f => f.Kind == FactKind.Skill).SelectMany(f => Regex.Matches(f.Text, @"\b[A-Z][A-Za-z0-9]+\b").Select(m => m.Value)))
            .Where(s => s.Length > 1).Distinct().ToList();
        return new CandidateProfile
        {
            Name = p.Name, Headline = p.Headline,
            Summary = string.Join(" ", confirmed.Where(f => f.Kind == FactKind.Summary).Select(f => f.Text)),
            CoreCompetencies = confirmed.Where(f => f.Kind == FactKind.Skill).Select(f => f.Text).ToList(),
            Experience = roles.Select(r => new Experience
            {
                Id = r.Id, Title = r.Title, Company = r.Company, Location = r.Location, Period = r.Period,
                Achievements = confirmed.Where(a => a.ParentId == r.Id && a.Kind == FactKind.Achievement).Select(a => a.Text).ToList(),
            }).ToList(),
            VerifiedNumbers = numbers.Where(n => n.Length > 0).ToList(),
            VerifiedEntities = entities,
            KnownGaps = knownGaps?.ToList() ?? new(),
        };
    }

    public static int YearsOfExperience(CandidateProfileRecord p)
    {
        var years = p.ConfirmedFacts.Where(f => f.Kind == FactKind.Role)
            .SelectMany(f => Regex.Matches(f.Period, @"(19|20)\d{2}").Select(m => int.Parse(m.Value))).ToList();
        return years.Count == 0 ? 0 : DateTime.UtcNow.Year - years.Min();
    }

    public static CandidateStory ToEngineStory(Story s) => new()
    {
        Id = s.Id, Company = s.Company, Role = s.Role, Situation = s.Situation, Action = s.Action, Result = s.Result,
        VerifiedNumbers = s.Metrics, Skills = s.Skills, QuestionTypes = s.QuestionTypes,
    };
}
