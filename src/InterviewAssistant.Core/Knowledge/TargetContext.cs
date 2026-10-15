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

    private List<(Regex Pattern, string Canonical)>? _termRules;

    /// <summary>
    /// Conservative, target-scoped normalization of recognised terms: an acronym-style vocabulary term (e.g. "MiCAR")
    /// also replaces its one-letter-shorter recognition ("MiCA"/"mica") — only when the shorter form is not itself in
    /// this target's vocabulary. Never global, never for short terms (so "XAB" can never replace "app").
    /// </summary>
    public string NormalizeTerms(string text)
    {
        _termRules ??= BuildTermRules();
        foreach (var (rx, canonical) in _termRules) text = rx.Replace(text, canonical);
        return text;
    }

    private List<(Regex, string)> BuildTermRules()
    {
        var vocab = new[] { CompanyName }.Concat(Products).Concat(Vocabulary).Concat(Aliases.Select(a => a.Canonical))
            .Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct(StringComparer.Ordinal).ToList();
        var lower = new HashSet<string>(vocab.Select(v => v.ToLowerInvariant()));
        var rules = new List<(Regex, string)>();
        foreach (var v in vocab)
        {
            bool acronymLike = v.Length >= 4 && v.Length <= 12 && !v.Contains(' ') && v.Count(char.IsUpper) >= 2 && v.All(char.IsLetterOrDigit);
            if (!acronymLike) continue;
            var shorter = v[..^1];
            if (shorter.Length < 3 || lower.Contains(shorter.ToLowerInvariant()) || !char.IsUpper(v[^1])) continue;
            rules.Add((new Regex(@"(?<![\p{L}\p{N}])" + Regex.Escape(shorter) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), v));
        }
        return rules;
    }

    /// <summary>Transcription bias prompt built from the target (no hard-coded names).</summary>
    public string BuildTranscriptionPrompt()
    {
        var who = string.IsNullOrWhiteSpace(CompanyName) ? $"Job interview for a {RoleTitle} role." : $"Job interview for a {RoleTitle} role at {CompanyName}.";
        return who + " Terms: " + string.Join(", ", TranscriptionTerms()) + ".";
    }

    /// <summary>
    /// Names and terms the interviewer is likely to say for THIS interview: company, products, domain vocabulary,
    /// canonical forms of misrecognised terms, and the candidate's confirmed employers. No sentences, no résumé text.
    /// </summary>
    public IReadOnlyList<string> TranscriptionTerms() =>
        new[] { CompanyName }.Concat(Products).Concat(Vocabulary).Concat(Aliases.Select(a => a.Canonical)).Concat(Employers)
            .Select(t => t?.Trim() ?? "").Where(t => t.Length > 1 && t.Length <= 48 && t != "the candidate")
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(80).ToList();
}
