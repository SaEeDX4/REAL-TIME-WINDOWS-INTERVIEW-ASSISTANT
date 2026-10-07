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
public enum FactKind { Summary, Role, Achievement, Skill, Education, Language, Certification, Project, Other }

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

    [JsonIgnore] public bool IsVerifiedClaim => Status == FactStatus.UserConfirmed;
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
    [JsonIgnore] public int PendingReviewCount => Facts.Count(f => f.Status is FactStatus.Source or FactStatus.AiInference);

    public void ConfirmAll(Func<ProfileFact, bool>? filter = null)
    {
        foreach (var f in Facts.Where(f => f.Status == FactStatus.Source && (filter?.Invoke(f) ?? true))) f.Status = FactStatus.UserConfirmed;
        UpdatedUtc = DateTime.UtcNow;
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
