using System.Text.Json.Serialization;

namespace InterviewAssistant.Core.Knowledge;

public enum AnswerMode { Verified, Hypothetical, Bridge }

public sealed class BankQuestion
{
    [JsonPropertyName("question_id")] public string QuestionId { get; init; } = "";
    [JsonPropertyName("canonical_question")] public string CanonicalQuestion { get; init; } = "";
    [JsonPropertyName("category")] public string Category { get; init; } = "";
    [JsonPropertyName("intent")] public string Intent { get; init; } = "";
    [JsonPropertyName("keywords")] public List<string> Keywords { get; init; } = new();
    [JsonPropertyName("semantic_variants")] public List<string> SemanticVariants { get; init; } = new();
    [JsonPropertyName("answer_mode")] public string AnswerModeRaw { get; init; } = "HYPOTHETICAL";
    [JsonPropertyName("short_bullets")] public List<string> ShortBullets { get; init; } = new();
    [JsonPropertyName("optional_full_answer")] public string OptionalFullAnswer { get; init; } = "";
    [JsonPropertyName("candidate_evidence")] public List<string> CandidateEvidence { get; init; } = new();
    [JsonPropertyName("gap_warning")] public string GapWarning { get; init; } = "";
    [JsonPropertyName("technical_notes")] public string TechnicalNotes { get; init; } = "";
    [JsonPropertyName("product_notes")] public string ProductNotes { get; init; } = "";
    [JsonPropertyName("follow_up_questions")] public List<string> FollowUpQuestions { get; init; } = new();

    [JsonIgnore]
    public AnswerMode Mode => AnswerModeRaw.ToUpperInvariant() switch
    {
        "VERIFIED" => AnswerMode.Verified,
        "BRIDGE" => AnswerMode.Bridge,
        _ => AnswerMode.Hypothetical,
    };
}

public sealed class QuestionBankFile
{
    [JsonPropertyName("questions")] public List<BankQuestion> Questions { get; init; } = new();
}

public sealed class CandidateStory
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("company")] public string Company { get; init; } = "";
    [JsonPropertyName("role")] public string Role { get; init; } = "";
    [JsonPropertyName("situation")] public string Situation { get; init; } = "";
    [JsonPropertyName("action")] public string Action { get; init; } = "";
    [JsonPropertyName("result")] public string Result { get; init; } = "";
    [JsonPropertyName("verified_numbers")] public List<string> VerifiedNumbers { get; init; } = new();
    [JsonPropertyName("skills")] public List<string> Skills { get; init; } = new();
    [JsonPropertyName("question_types")] public List<string> QuestionTypes { get; init; } = new();

    public string ToEvidenceLine() => $"[{Company}, {Role}] {Action} Result: {Result}";
}

public sealed class StoriesFile
{
    [JsonPropertyName("stories")] public List<CandidateStory> Stories { get; init; } = new();
}

public sealed class Experience
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("company")] public string Company { get; init; } = "";
    [JsonPropertyName("location")] public string Location { get; init; } = "";
    [JsonPropertyName("period")] public string Period { get; init; } = "";
    [JsonPropertyName("achievements")] public List<string> Achievements { get; init; } = new();
}

public sealed class CandidateProfile
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("headline")] public string Headline { get; init; } = "";
    [JsonPropertyName("summary")] public string Summary { get; init; } = "";
    [JsonPropertyName("core_competencies")] public List<string> CoreCompetencies { get; init; } = new();
    [JsonPropertyName("experience")] public List<Experience> Experience { get; init; } = new();
    [JsonPropertyName("verified_numbers")] public List<string> VerifiedNumbers { get; init; } = new();
    [JsonPropertyName("verified_entities")] public List<string> VerifiedEntities { get; init; } = new();
    [JsonPropertyName("known_gaps")] public List<string> KnownGaps { get; init; } = new();
}
