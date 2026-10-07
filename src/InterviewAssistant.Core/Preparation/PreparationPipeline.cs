using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using InterviewAssistant.Core.Domain;
using InterviewAssistant.Core.Ingestion;
using InterviewAssistant.Core.Intelligence;
using InterviewAssistant.Core.Knowledge;
using InterviewAssistant.Core.Languages;

namespace InterviewAssistant.Core.Preparation;

public sealed record PreparationProgress(int Stage, int TotalStages, string StageName, string Detail);

/// <summary>Optional AI enrichment (improves low-confidence prepared answers). Must keep the truth rules.</summary>
public interface IPreparationEnricher
{
    Task EnrichAsync(PreparedPack pack, IProgress<PreparationProgress>? progress, CancellationToken ct);
}

public sealed class GenericLibraryItem
{
    [JsonPropertyName("canonical_question")] public string CanonicalQuestion { get; init; } = "";
    [JsonPropertyName("category")] public string Category { get; init; } = "";
    [JsonPropertyName("semantic_variants")] public List<string> SemanticVariants { get; init; } = new();
    [JsonPropertyName("short_bullets")] public List<string> ShortBullets { get; init; } = new();
    [JsonPropertyName("optional_full_answer")] public string OptionalFullAnswer { get; init; } = "";
    [JsonPropertyName("follow_up_questions")] public List<string> FollowUpQuestions { get; init; } = new();
    [JsonPropertyName("domains")] public List<string> Domains { get; init; } = new();
}

/// <summary>Shared, company-neutral inputs shipped with the app (playbooks + generic expert answers).</summary>
public sealed class PreparationAssets
{
    public List<KnowledgeSnippet> Playbooks { get; init; } = new();
    public List<GenericLibraryItem> Library { get; init; } = new();

    public static PreparationAssets Load(string knowledgeDir)
    {
        var playbooks = new List<KnowledgeSnippet>();
        var pbDir = Path.Combine(knowledgeDir, "playbooks");
        if (Directory.Exists(pbDir))
            foreach (var f in Directory.GetFiles(pbDir, "*.md")) playbooks.AddRange(KnowledgeBase.SplitMarkdown(Path.GetFileName(f), File.ReadAllText(f)));
        var libPath = Path.Combine(knowledgeDir, "generic_question_library.json");
        var lib = File.Exists(libPath) ? JsonSerializer.Deserialize<LibFile>(File.ReadAllText(libPath))?.Questions ?? new() : new();
        return new PreparationAssets { Playbooks = playbooks, Library = lib };
    }

    private sealed class LibFile { [JsonPropertyName("questions")] public List<GenericLibraryItem> Questions { get; init; } = new(); }
}

/// <summary>
/// Builds a per-target interview pack from CONFIRMED profile facts + the job package. Deterministic and offline
/// (fast, free, testable); an optional <see cref="IPreparationEnricher"/> upgrades answers with the AI backend.
/// Never sends whole documents to the live path: the output is compact structured context.
/// </summary>
public sealed class PreparationPipeline
{
    public const string GeneratorVersion = "local-2.0";
    public static readonly string[] Stages =
    {
        "Verify candidate profile", "Extract role requirements", "Map candidate to role", "Analyse gaps", "Extract terminology",
        "Company & product context", "Select stories", "Generate likely questions", "Prepare fast answers", "Coach Mode keywords",
        "Domain playbook", "Answer truth policy", "Difficult questions", "Questions to ask", "Build runtime cache",
    };

    private readonly PreparationAssets _assets;
    public PreparationPipeline(PreparationAssets assets) => _assets = assets;

    public async Task<PreparedPack> RunAsync(CandidateProfileRecord profile, InterviewTarget target, IPreparationEnricher? enricher = null,
        IProgress<PreparationProgress>? progress = null, CancellationToken ct = default)
    {
        int stage = 0;
        var log = new List<string>();
        var warnings = new List<string>();
        void Step(string detail) { ct.ThrowIfCancellationRequested(); log.Add($"{Stages[stage]}: {detail}"); progress?.Report(new(stage + 1, Stages.Length, Stages[stage], detail)); stage++; }

        // 1. profile verification — only confirmed facts may become historical claims
        var confirmed = profile.ConfirmedFacts.ToList();
        if (confirmed.Count == 0) throw new InvalidOperationException("Confirm at least some extracted profile facts before preparing an interview.");
        if (profile.PendingReviewCount > 0) warnings.Add($"{profile.PendingReviewCount} extracted facts are not confirmed and will not be used as claims.");
        ProfileService.RebuildStories(profile);
        Step($"{confirmed.Count} confirmed facts, {profile.Stories.Count} stories");

        // 2. JD extraction (untrusted input; injection lines removed)
        var jdText = string.Join("\n", new[] { target.JobDescriptionText }.Concat(target.Documents.Where(d => d.Kind == DocumentKind.JobDescription).Select(d => d.Text)));
        var jd = JobDescriptionParser.Parse(jdText, target.JobTitle.Length > 0 ? target.JobTitle : null, target.Company.Length > 0 ? target.Company : null);
        if (target.JobTitle.Length == 0) target.JobTitle = jd.Title;
        if (target.Company.Length == 0) target.Company = jd.Company;
        warnings.AddRange(jd.SecurityFlags);
        var requirements = jd.Requirements.Concat(jd.NiceToHave).Distinct().Take(30).ToList();
        Step($"{jd.Responsibilities.Count} responsibilities, {jd.Requirements.Count} requirements, {jd.NiceToHave.Count} nice-to-have (language {jd.Language})");

        // 3–4. match map + gaps
        var factIndex = confirmed.Where(f => f.Kind is FactKind.Achievement or FactKind.Role or FactKind.Skill or FactKind.Project or FactKind.Certification or FactKind.Education or FactKind.Summary)
            .Select(f => (Fact: f, Tokens: new HashSet<string>(TextNormalizer.Tokenize(f.Text + " " + f.Title + " " + f.Company)))).ToList();
        var matchMap = requirements.Select(r =>
        {
            var rt = new HashSet<string>(TextNormalizer.Tokenize(r)).Where(t => !GenericReqWords.Contains(t)).ToHashSet();
            var scored = factIndex.Select(x => (x.Fact, Score: rt.Count == 0 ? 0 : (double)rt.Count(x.Tokens.Contains) / rt.Count)).Where(x => x.Score > 0).OrderByDescending(x => x.Score).ToList();
            var best = scored.FirstOrDefault().Score;
            return new RequirementMatch { Requirement = r, FactIds = scored.Take(3).Select(x => x.Fact.Id).ToList(), Score = best, IsGap = best < 0.25 };
        }).ToList();
        var gaps = matchMap.Where(m => m.IsGap).Select(m => KeyPhrase(m.Requirement)).Where(g => g.Length > 2).Distinct().ToList();
        Step($"{matchMap.Count(m => !m.IsGap)} matched requirements");
        Step($"{gaps.Count} likely gaps");

        // 5. terminology
        var terminology = jd.Terminology.Concat(target.Documents.Where(d => d.Kind == DocumentKind.CompanyMaterial).SelectMany(d => JobDescriptionParser.ExtractTerminology(d.Text, target.Company)))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(60).ToList();
        Step($"{terminology.Count} terms");

        // 6. company/product context from USER-PROVIDED sources only (labelled; nothing fabricated)
        var snippets = new List<KnowledgeSnippet>();
        void AddSection(string src, string heading, IEnumerable<string> items) { foreach (var it in items) snippets.Add(new KnowledgeSnippet(src, heading, it)); }
        AddSection("job_description", "Responsibilities", jd.Responsibilities);
        AddSection("job_description", "Requirements", jd.Requirements);
        AddSection("job_description", "Nice to have", jd.NiceToHave);
        AddSection("job_description", "About the company (from JD, user-provided)", jd.About);
        foreach (var d in target.Documents.Where(d => d.Kind == DocumentKind.CompanyMaterial || d.Kind == DocumentKind.Notes))
            snippets.AddRange(KnowledgeBase.SplitMarkdown("user_material:" + d.FileName, d.Text).Take(200));
        if (target.RoleNotes.Length > 0) AddSection("role_notes", "Role notes (user)", target.RoleNotes.Split('\n').Where(l => l.Trim().Length > 20));
        var domains = DetectDomains(jdText + " " + string.Join(" ", terminology));
        snippets.AddRange(_assets.Playbooks.Where(s => domains.Contains("fintech") || !s.Source.Contains("crypto")));
        var products = terminology.Where(t => !Regex.IsMatch(t, @"^[A-Z]{2,5}s?$") && char.IsUpper(t[0]) && !CommonTech.Contains(t)).Take(8).ToList();
        Step($"{snippets.Count} context notes · domains: {string.Join(", ", domains)}");

        // 7. stories, ordered by relevance to this role (used as default evidence order)
        var reqTokens = new HashSet<string>(requirements.Concat(jd.Responsibilities).SelectMany(TextNormalizer.Tokenize));
        var stories = profile.Stories
            .Select(s => (s, Score: TextNormalizer.Tokenize(s.Action).Count(reqTokens.Contains) + s.Metrics.Count * 0.5))
            .OrderByDescending(x => x.Score).Select(x => x.s).ToList();
        Step($"{stories.Count} stories ranked");

        // 8–10. questions + fast answers + coach keywords
        var engineProfile = ProfileService.ToEngineProfile(profile, gaps);
        var builder = new QuestionFactory(profile, target, jd, stories, matchMap, gaps, confirmed);
        var questions = builder.Build(_assets.Library.Where(l => l.Domains.Any(domains.Contains)).ToList());
        Step($"{questions.Count} questions");
        Step($"{questions.Count(q => q.Confidence >= 0.7)} instant answers, {questions.Count(q => q.Confidence < 0.7)} AI-reference answers");
        Step("3 keywords + structure per question");
        Step($"{snippets.Count(s => !s.Source.StartsWith("job") && !s.Source.StartsWith("user"))} playbook notes");

        // 12. truth policy
        var truth = "Historical claims only from user-confirmed facts. Hypothetical questions: strongest approach in future tense. " +
                    "Direct questions about experience the candidate lacks (" + (gaps.Count > 0 ? string.Join("; ", gaps.Take(5)) : "none detected") + "): one honest bridge sentence, then approach.";
        Step("policy set");
        var difficult = gaps.Take(6).Select(g => $"Have you personally worked with {g}?")
            .Concat(new[] { "Why are you looking for a new opportunity?", "What is your biggest weakness?", "Why should we choose you over other candidates?" }).ToList();
        Step($"{difficult.Count} difficult questions");
        var toAsk = QuestionFactory.QuestionsToAsk(target, jd);
        Step($"{toAsk.Count} questions to ask");

        var answerLang = target.AnswerLanguage != "same" ? target.AnswerLanguage : profile.AnswerLanguage;
        var pack = new PreparedPack
        {
            GeneratorVersion = GeneratorVersion, ProfileId = profile.Id, TargetId = target.Id,
            Context = new TargetContext
            {
                CandidateName = profile.PreferredName.Length > 0 ? profile.PreferredName : profile.Name,
                RoleTitle = target.JobTitle.Length > 0 ? target.JobTitle : "the role",
                CompanyName = target.Company, Products = products,
                Vocabulary = terminology.Concat(confirmed.Where(f => f.Kind == FactKind.Skill).Select(f => f.Text)).Distinct().Take(80).ToList(),
                DomainNumbers = Regex.Matches(jdText, @"\b\d[\d,.]*\b").Select(m => m.Value).Distinct().Take(30).ToList(),
                ExtraEntities = terminology.Take(30).ToList(),
                Positioning = builder.Positioning(),
                DomainGuidance = "Key responsibilities: " + string.Join("; ", jd.Responsibilities.Take(6).Select(r => KeyPhrase(r, 12))) + ".",
                InterviewLanguage = target.ExpectedLanguage != "auto" ? target.ExpectedLanguage : profile.InterviewLanguage,
                AnswerLanguage = string.IsNullOrEmpty(answerLang) ? "same" : answerLang,
                ProfileId = profile.Id, TargetId = target.Id,
            },
            Profile = engineProfile, Stories = stories.Select(ProfileService.ToEngineStory).ToList(), Questions = questions, Snippets = snippets,
            MatchMap = matchMap, Gaps = gaps, Terminology = terminology, DifficultQuestions = difficult, QuestionsToAsk = toAsk,
            TruthPolicy = truth, StageLog = log, Warnings = warnings,
        };
        if (enricher != null)
        {
            try { await enricher.EnrichAsync(pack, progress, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is Providers.ProviderException or HttpRequestException) { pack.Warnings.Add("AI enrichment unavailable: " + ex.Message + " (local answers kept)."); }
        }
        Step("ready");
        target.Preparation = PreparationState.Prepared;
        target.PreparedUtc = DateTime.UtcNow;
        target.PreparationError = null;
        return pack;
    }

    private static readonly HashSet<string> GenericReqWords = new(StringComparer.OrdinalIgnoreCase)
    { "experience", "year", "strong", "proven", "ability", "knowledge", "skill", "excellent", "good", "work", "working", "understand", "understanding", "able", "plus", "ideally", "team", "within", "least", "minimum" };
    private static readonly HashSet<string> CommonTech = new(StringComparer.OrdinalIgnoreCase) { "Jira", "Agile", "Scrum", "Kanban", "Figma", "Product Owner", "Engineering", "Design", "Legal", "Compliance" };

    public static List<string> DetectDomains(string text)
    {
        var d = new List<string> { "general" };
        if (Regex.IsMatch(text, @"product|backlog|roadmap|user stor|agile|scrum|sprint|stakeholder|okr|kpi", RegexOptions.IgnoreCase)) d.Add("product");
        if (Regex.IsMatch(text, @"crypto|blockchain|token|ledger|fintech|payment|bank|wallet|mica|kyc|aml|reconcil|digital asset", RegexOptions.IgnoreCase)) d.Add("fintech");
        if (Regex.IsMatch(text, @"regulat|complian|gdpr|audit", RegexOptions.IgnoreCase)) d.Add("regulated");
        if (Regex.IsMatch(text, @"api|architecture|engineer|software|cloud|system design|technical", RegexOptions.IgnoreCase)) d.Add("technical");
        return d;
    }

    /// <summary>Compact noun phrase from a requirement sentence ("Proven experience building financial ledgers" → "building financial ledgers").</summary>
    public static string KeyPhrase(string requirement, int maxWords = 8)
    {
        var s = Regex.Replace(requirement, @"^\s*(\d+\+?\s*(-\s*\d+\s*)?years?( of)?|(proven|strong|solid|deep|hands-on|demonstrated|excellent|good)?\s*(experience|background|knowledge|understanding|expertise|familiarity|proficiency)\s*(in|with|of|building|owning|working with)?|ability to|you have|you are|must have|we expect)\s*", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s*\(.*?\)", "").Trim().TrimEnd('.', ';', ',');
        s = Regex.Replace(s, @"^(in|with|of|a|an|the)\s+", "", RegexOptions.IgnoreCase);
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var phrase = string.Join(' ', words.Take(maxWords)).Trim().TrimEnd(',', ';', ':');
        return phrase.Length > 0 && char.IsUpper(phrase[0]) && !(phrase.Length > 1 && char.IsUpper(phrase[1])) ? char.ToLowerInvariant(phrase[0]) + phrase[1..] : phrase;
    }
}
