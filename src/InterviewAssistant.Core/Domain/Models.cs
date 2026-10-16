using System.Text.Json.Serialization;
using InterviewAssistant.Core.Knowledge;

namespace InterviewAssistant.Core.Domain;

/// <summary>Where a fact came from and whether it may be used as a historical claim.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FactStatus
{
    /// <summary>Extracted from a source document; NOT yet usable as a verified claim.</summary>
    Source,
    /// <summary>User reviewed and confirmed: may be used as a historical claim in answers.</summary>
    UserConfirmed,
    /// <summary>Inferred by AI; never used as a historical claim unless the user confirms it.</summary>
    AiInference,
    /// <summary>Generated domain/hypothetical knowledge; never a historical claim.</summary>
    GeneratedKnowledge,
    /// <summary>User rejected; excluded everywhere.</summary>
    Rejected,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FactKind { Summary, Role, Achievement, Skill, Education, Language, Certification, Project, Other, Personal, Boundary }

/// <summary>Where a fact came from (provenance shown in the profile editor).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FactSource { Unknown, ResumeImport, Manual, Questionnaire, Document }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DocumentKind { Resume, JobDescription, CompanyMaterial, LinkedIn, Portfolio, Notes }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InterviewType { Unknown, HR, HiringManager, Technical, Case, Panel }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PreparationState { NotPrepared, Preparing, Prepared, Failed, Stale }

public sealed record Provenance(string DocumentId, int LineStart, int LineEnd, string Snippet);

public sealed class SourceDocument
{
    public string Id { get; init; } = Guid.NewGuid().ToString("n");
    public string FileName { get; init; } = "";
    public DocumentKind Kind { get; init; }
    public string Language { get; set; } = "und";
    public string Text { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public bool NeedsOcr { get; init; }
    public DateTime AddedUtc { get; init; } = DateTime.UtcNow;
    public List<string> SecurityFlags { get; init; } = new();
}

public sealed class ProfileFact
{
    public string Id { get; init; } = Guid.NewGuid().ToString("n")[..12];
    public FactKind Kind { get; init; }
    public string Text { get; set; } = "";
    public string Company { get; set; } = "";
    public string Title { get; set; } = "";
    public string Location { get; set; } = "";
    public string Period { get; set; } = "";
    /// <summary>Id of the Role fact this fact belongs to (achievements under a role).</summary>
    public string? ParentId { get; set; }
    public List<string> Metrics { get; set; } = new();
    public Provenance? Provenance { get; init; }
    public FactStatus Status { get; set; } = FactStatus.Source;
    public double Confidence { get; set; } = 0.8;
    public List<string> Flags { get; set; } = new();
    public string Language { get; set; } = "und";
    /// <summary>Personal/boundary facts: <see cref="PersonalCategories"/> key (hobby, motivation, …).</summary>
    public string Category { get; set; } = "";
    public FactSource Source { get; set; } = FactSource.Unknown;
    /// <summary>File name or questionnaire label the fact came from (no content).</summary>
    public string SourceName { get; set; } = "";
    public DateTime? CreatedUtc { get; set; }
    public DateTime? UpdatedUtc { get; set; }
    public DateTime? ConfirmedUtc { get; set; }

    [JsonIgnore] public bool IsVerifiedClaim => Status == FactStatus.UserConfirmed;
    /// <summary>Shown in the review list (every kind, including the résumé summary — nothing hidden can be "awaiting review").</summary>
    [JsonIgnore] public bool IsReviewable => Status != FactStatus.Rejected && Status != FactStatus.GeneratedKnowledge;
    [JsonIgnore] public bool AwaitingReview => IsReviewable && Status is FactStatus.Source or FactStatus.AiInference;

    /// <summary>Confirms (or un-confirms) the fact and records when.</summary>
    public void SetConfirmed(bool confirmed, DateTime nowUtc)
    {
        if (confirmed && Status != FactStatus.UserConfirmed) { Status = FactStatus.UserConfirmed; ConfirmedUtc = nowUtc; UpdatedUtc = nowUtc; }
        else if (!confirmed && Status == FactStatus.UserConfirmed) { Status = FactStatus.Source; ConfirmedUtc = null; UpdatedUtc = nowUtc; }
    }
}

/// <summary>Counts for the profile header, list footer and filters — ONE derivation used everywhere (no drift).</summary>
/// <param name="Confirmed">All confirmed facts: experience facts plus interview-profile answers.</param>
/// <param name="ConfirmedProfileAnswers">Confirmed interview-profile answers (personal topics and boundaries), which the
/// Experience list does not show — counts shown to the user name both parts, so adding a hobby never makes one number
/// silently disagree with another.</param>
public sealed record FactStats(int Total, int Confirmed, int ToReview, int ConfirmedProfileAnswers = 0)
{
    public int ConfirmedExperience => Confirmed - ConfirmedProfileAnswers;

    public static FactStats Of(IEnumerable<ProfileFact> facts)
    {
        var list = facts.Where(f => f.IsReviewable).ToList();
        return new FactStats(list.Count, list.Count(f => f.IsVerifiedClaim), list.Count(f => f.AwaitingReview),
            list.Count(f => f.IsVerifiedClaim && f.Kind is FactKind.Personal or FactKind.Boundary));
    }
}

/// <summary>
/// Interview-safe personal and professional-identity topics of the Candidate Knowledge Profile. Everything is optional;
/// nothing sensitive (health, religion, politics, family status, age…) is asked. Answers come ONLY from confirmed entries.
/// </summary>
public static class PersonalCategories
{
    public sealed record Category(string Key, string Section, string Label, string Prompt, bool Boundary = false);

    public static readonly IReadOnlyList<Category> All = new[]
    {
        new Category("hobby", "personal", "Hobbies", "What are your hobbies?"),
        new Category("outside_work", "personal", "Outside work", "What do you enjoy doing outside work?"),
        new Category("learning", "personal", "Currently learning", "What do you enjoy learning about at the moment?"),
        new Category("motivation", "identity", "What motivates you", "What motivates you professionally?"),
        new Category("strength", "identity", "Strength to remember", "What strength do you want interviewers to remember?"),
        new Category("weakness", "identity", "Real weakness (and how you manage it)", "What real weakness are you comfortable discussing, and how do you manage it?"),
        new Category("work_style", "identity", "Work style", "How do you prefer to work (pace, autonomy, structure)?"),
        new Category("team", "identity", "Team you work best in", "What type of team do you work best in?"),
        new Category("leadership_style", "identity", "Leadership style", "How would you describe your leadership style?"),
        new Category("communication_style", "identity", "Communication with stakeholders", "How do you communicate with stakeholders and handle disagreement?"),
        new Category("why_looking", "goals", "Why you are looking", "Why are you looking for your next role?"),
        new Category("career_goal", "goals", "Career goals", "What are your career goals for the next few years?"),
        new Category("ideal_role", "goals", "Ideal next role / company", "What does your ideal next role, product or company look like?"),
        new Category("location", "basics", "Location / time zone", "Where are you based (city, time zone)?"),
        new Category("remote", "basics", "Remote / hybrid / relocation", "What is your preference for remote, hybrid or relocation?"),
        new Category("availability", "basics", "Availability / notice period", "When could you start (notice period)?"),
        new Category("work_authorization", "basics", "Work authorization (optional)", "Optional: anything you want said about work authorization?"),
        new Category("pronunciation", "basics", "Name pronunciation", "How should your name be pronounced (optional)?"),
        new Category("avoid_topic", "boundaries", "Topics never to speculate about", "Topics the assistant must never speculate about or mention.", Boundary: true),
        new Category("disallowed_claim", "boundaries", "Claims that must never be made", "Claims that must never be made about you.", Boundary: true),
        new Category("answer_style", "preferences", "Answer style preferences", "How should suggested answers sound (simple English, more technical, vocabulary to use/avoid)?"),
    };

    public static Category? Find(string? key) => All.FirstOrDefault(c => c.Key == key);
}

public sealed class Story
{
    public string Id { get; init; } = "";
    public string Company { get; init; } = "";
    public string Role { get; init; } = "";
    public string Situation { get; init; } = "";
    public string Action { get; init; } = "";
    public string Result { get; init; } = "";
    public List<string> Metrics { get; init; } = new();
    public List<string> Skills { get; init; } = new();
    public List<string> QuestionTypes { get; init; } = new();
    /// <summary>Confirmed fact ids this story is built from (provenance chain).</summary>
    public List<string> FactIds { get; init; } = new();
}

/// <summary>A candidate (person being interviewed). One account may own several.</summary>
public sealed class CandidateProfileRecord
{
    /// <summary>Profile schema: 0/1 = résumé facts only (RC1), 2 = Candidate Knowledge Profile with provenance.</summary>
    public const int CurrentSchemaVersion = 2;
    public int SchemaVersion { get; set; }
    public string Id { get; init; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = "";
    public string PreferredName { get; set; } = "";
    public string Headline { get; set; } = "";
    public string Location { get; set; } = "";
    public string TimeZone { get; set; } = "";
    public string InterviewLanguage { get; set; } = "auto";
    public string AnswerLanguage { get; set; } = "same";
    public string SpeakingStyle { get; set; } = "professional";
    public string AnswerLength { get; set; } = "balanced";
    public string Seniority { get; set; } = "";
    public string Notes { get; set; } = "";
    public string LinkedInText { get; set; } = "";
    public List<string> PortfolioLinks { get; set; } = new();
    public List<SourceDocument> Documents { get; set; } = new();
    public List<ProfileFact> Facts { get; set; } = new();
    public List<Story> Stories { get; set; } = new();
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore] public IEnumerable<ProfileFact> ConfirmedFacts => Facts.Where(f => f.IsVerifiedClaim);
    /// <summary>Facts awaiting review — the same derivation as the list and filters (<see cref="FactStats"/>).</summary>
    [JsonIgnore] public int PendingReviewCount => FactStats.Of(Facts).ToReview;
    [JsonIgnore] public FactStats Stats => FactStats.Of(Facts);

    public void ConfirmAll(Func<ProfileFact, bool>? filter = null)
    {
        var now = DateTime.UtcNow;
        foreach (var f in Facts.Where(f => f.AwaitingReview && (filter?.Invoke(f) ?? true))) f.SetConfirmed(true, now);
        UpdatedUtc = now;
    }
}

/// <summary>A specific job interview a profile is preparing for.</summary>
public sealed class InterviewTarget
{
    public string Id { get; init; } = Guid.NewGuid().ToString("n");
    public string ProfileId { get; init; } = "";
    public string JobTitle { get; set; } = "";
    public string Company { get; set; } = "";
    public string JobDescriptionText { get; set; } = "";
    public string JobDescriptionUrl { get; set; } = "";
    public InterviewType InterviewType { get; set; } = InterviewType.Unknown;
    public DateTime? InterviewAtUtc { get; set; }
    public string ExpectedLanguage { get; set; } = "auto";
    public string AnswerLanguage { get; set; } = "same";
    public string RoleNotes { get; set; } = "";
    public List<string> CompanyUrls { get; set; } = new();
    public List<SourceDocument> Documents { get; set; } = new();
    public PreparationState Preparation { get; set; } = PreparationState.NotPrepared;
    public DateTime? PreparedUtc { get; set; }
    public string? PreparationError { get; set; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(Company) ? JobTitle : $"{JobTitle} · {Company}";
}

public sealed class RequirementMatch
{
    public string Requirement { get; init; } = "";
    public List<string> FactIds { get; init; } = new();
    public double Score { get; init; }
    public bool IsGap { get; init; }
}

/// <summary>Per-target preparation output. Converted to a KnowledgeBase for the live engine.</summary>
public sealed class PreparedPack
{
    public int SchemaVersion { get; init; } = 1;
    public string GeneratorVersion { get; init; } = "";
    public string ProfileId { get; init; } = "";
    public string TargetId { get; init; } = "";
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public TargetContext Context { get; init; } = new();
    public CandidateProfile Profile { get; init; } = new();
    public List<CandidateStory> Stories { get; init; } = new();
    public List<BankQuestion> Questions { get; init; } = new();
    public List<KnowledgeSnippet> Snippets { get; init; } = new();
    public List<RequirementMatch> MatchMap { get; init; } = new();
    public List<string> Gaps { get; init; } = new();
    public List<string> Terminology { get; init; } = new();
    public List<string> DifficultQuestions { get; init; } = new();
    public List<string> QuestionsToAsk { get; init; } = new();
    public string TruthPolicy { get; init; } = "";
    public List<string> StageLog { get; init; } = new();
    public List<string> Warnings { get; init; } = new();

    public KnowledgeBase ToKnowledgeBase()
    {
        Context.Employers = Profile.Experience.Select(e => e.Company).ToList();
        return new KnowledgeBase
        {
            Profile = Profile, Stories = Stories, Questions = Questions, Snippets = Snippets,
            SystemPromptTemplate = PromptTemplates.LiveAnswer, Context = Context, Directory = "",
        };
    }
}
