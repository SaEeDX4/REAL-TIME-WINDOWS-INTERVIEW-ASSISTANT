using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace InterviewAssistant.Core.Knowledge;

/// <summary>Spoken-form variants (ASR misrecognitions) mapped to the canonical term, e.g. ["terox","ter ox"] → "Teroxx".</summary>
public sealed class TermAlias
{
    [JsonPropertyName("canonical")] public string Canonical { get; init; } = "";
    [JsonPropertyName("variants")] public List<string> Variants { get; init; } = new();

    private Regex? _regex;
    [JsonIgnore]
    public Regex Regex => _regex ??= new Regex(@"\b(" + string.Join("|", Variants.Where(v => v.Length > 0).Select(Regex.Escape)) + @")\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}

/// <summary>
/// Everything the live engine needs to know about WHO is interviewing for WHAT. Produced per interview target by the
/// preparation pipeline (or loaded from target_context.json for fixtures). The engine contains no hard-coded
/// candidate, company or product names.
/// </summary>
public sealed class TargetContext
{
    [JsonPropertyName("candidate_name")] public string CandidateName { get; set; } = "the candidate";
    [JsonPropertyName("role_title")] public string RoleTitle { get; set; } = "the role";
    [JsonPropertyName("company_name")] public string CompanyName { get; set; } = "";
    [JsonPropertyName("products")] public List<string> Products { get; set; } = new();
    /// <summary>Domain terms that bias transcription and are preserved verbatim across languages.</summary>
    [JsonPropertyName("vocabulary")] public List<string> Vocabulary { get; set; } = new();
    [JsonPropertyName("aliases")] public List<TermAlias> Aliases { get; set; } = new();
    /// <summary>Numbers that are domain/company knowledge rather than candidate claims (e.g. token supply).</summary>
    [JsonPropertyName("domain_numbers")] public List<string> DomainNumbers { get; set; } = new();
    [JsonPropertyName("extra_entities")] public List<string> ExtraEntities { get; set; } = new();
    /// <summary>Truthful one-paragraph positioning of the candidate for this role (from confirmed facts).</summary>
    [JsonPropertyName("positioning")] public string Positioning { get; set; } = "";
    /// <summary>Role/domain-specific answering guidance (from JD analysis and playbooks).</summary>
    [JsonPropertyName("domain_guidance")] public string DomainGuidance { get; set; } = "";
    /// <summary>BCP-47-ish code or "auto".</summary>
    [JsonPropertyName("interview_language")] public string InterviewLanguage { get; set; } = "auto";
    /// <summary>"same" = answer in the interviewer's language, otherwise a language code.</summary>
    [JsonPropertyName("answer_language")] public string AnswerLanguage { get; set; } = "same";
    [JsonPropertyName("profile_id")] public string ProfileId { get; set; } = "";
    [JsonPropertyName("target_id")] public string TargetId { get; set; } = "";

    [JsonIgnore] public IEnumerable<string> Employers { get; set; } = Array.Empty<string>();

    /// <summary>Transcription bias prompt built from the target (no hard-coded names).</summary>
    public string BuildTranscriptionPrompt()
    {
        var who = string.IsNullOrWhiteSpace(CompanyName) ? $"Job interview for a {RoleTitle} role." : $"Job interview for a {RoleTitle} role at {CompanyName}.";
        var terms = new[] { CandidateName, CompanyName }.Concat(Products).Concat(Employers).Concat(Vocabulary)
            .Where(t => !string.IsNullOrWhiteSpace(t) && t != "the candidate").Distinct(StringComparer.OrdinalIgnoreCase).Take(80);
        return who + " Terms: " + string.Join(", ", terms) + ".";
    }
}
