using System.Text.RegularExpressions;
using InterviewAssistant.Core.Knowledge;

namespace InterviewAssistant.Core.Intelligence;

public sealed record Classification(string Category, AnswerMode Mode, bool IsFollowUp);

/// <summary>Fast rule-based classifier (microseconds). Mode decides truthfulness policy; category steers retrieval.</summary>
public static class QuestionClassifier
{
    private static readonly (string Category, string Pattern)[] CategoryRules =
    {
        ("LEDGER", @"\bledger|double[- ]entry|idempoten|duplicate (transaction|payment|payout)|transaction state|reversal|debit|credit\b"),
        ("RECONCILIATION", @"reconcil"),
        ("MICAR", @"\bmica|micar|markets in crypto"),
        ("COMPLIANCE", @"complian|regulat|legal|\bkyc\b|\baml\b|cysec|white ?paper"),
        ("VIP", @"\bvip\b|tier|status level|platinum|gold|silver"),
        ("REWARDS", @"reward|booster|cashback|loyalty|payout"),
        ("TOKENOMICS", @"tokenomic|token supply|supply|emission|inflation|distribution"),
        ("TOKENOMICS", @"token utility|utility token"),
        ("CRYPTO", @"crypto|blockchain|exchange|wallet|staking|on-?chain|erc-?20|defi|stablecoin"),
        ("AGILE_JIRA", @"\bjira\b|agile|scrum|sprint|kanban|definition of (done|ready)|refinement"),
        ("PRIORITIZATION", @"priorit|trade-?off|backlog|roadmap|technical debt|tech debt"),
        ("METRICS", @"metric|kpi|okr|north star|measure|a/b|experiment|retention|churn|activation|conversion|ltv"),
        ("STAKEHOLDER", @"stakeholder|disagree|conflict|c-?level|ceo|executive|leadership team|engineering (team|disagree)|marketing wants|push ?back"),
        ("LEADERSHIP", @"lead(ership)? style|manage (a )?team|mentor|motivat|hire|hiring"),
        ("PRODUCT_DISCOVERY", @"discovery|user research|customer research|interview users|validate"),
        ("PRODUCT_EXECUTION", @"user stor|acceptance criteria|spec|requirement|release|launch|uat"),
        ("PRODUCT_STRATEGY", @"strategy|vision|competitor|compet|market"),
        ("TECHNICAL_CONCEPT", @"\bapi\b|security|architecture|database|custod"),
        ("INTRODUCTION", @"about yourself|introduce|walk (me|us) through your|your background"),
        ("MOTIVATION", @"why (do you want|us|this|product owner|should we)|motivat"),
        ("BEHAVIOURAL", @"tell (me|us) about a time|give (me|us) an example|describe a situation|have you ever|biggest (achievement|failure)"),
        ("COMPANY_SPECIFIC", @"our company|our product"),
    };

    // Direct questions about personal history (Mode C candidates).
    private static readonly Regex PersonalHistory = new(
        @"\b(have you (ever |personally |actually |previously )?(built|owned|worked|designed|implemented|managed|led|run|done|launched|created|used)|did you (ever |personally )?(build|own|work|design|implement|manage|lead)|do you have (any |direct |hands-on |practical )?experience|what('s| is) your experience|your experience (with|in)|in your (previous|past|last) (role|job))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Topics in which the résumé has no direct evidence (known gaps).
    private static readonly Regex GapTopics = new(
        @"\b(ledger|reconcil|wallet architecture|wallet|closed[- ]loop|reward (system|program|engine)|loyalty|tokenomic|mica|micar|product owner|product manager|smart contract|double[- ]entry|idempoten|scrum master|token (launch|design|economy))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Hypothetical = new(
        @"\b(how would you|what would you|how will you|how do you|how should|what is|what's|what are|explain|design|imagine|suppose|if you|walk me through how)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VerifiedCue = new(
        @"\b(tell (me|us) about (yourself|your|a time)|your (background|career|cv|resume|role at|time at|current)|give (me|us) an example from|why (do you want|should we hire)|your (strength|weakness|achievement))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FollowUpCue = new(
        @"^(and |but |so |why\??$|how so|what (do you mean|metric|kpi|exactly)|can you (elaborate|expand|give (me )?an example|be more specific)|for example|such as|what happened|how would that|tell me more|go deeper|which one)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static Classification Classify(string question) => Classify(question, null);

    /// <summary>Classify with target context: employer names count as personal-history cues, company/product names as company-specific.</summary>
    public static Classification Classify(string question, TargetContext? ctx)
    {
        var q = question.Trim();
        var lower = q.ToLowerInvariant();
        var category = "GENERAL_BUSINESS";
        bool mentionsEmployer = ctx != null && ctx.Employers.Any(e => e.Length > 1 && ContainsWord(lower, e.ToLowerInvariant().Split(' ')[0]));
        bool mentionsCompany = ctx != null && new[] { ctx.CompanyName }.Concat(ctx.Products).Any(c => c.Length > 1 && ContainsWord(lower, c.ToLowerInvariant()));
        foreach (var (cat, pattern) in CategoryRules)
        {
            if (Regex.IsMatch(lower, pattern)) { category = cat; break; }
        }

        if (category == "GENERAL_BUSINESS" && mentionsCompany) category = "COMPANY_SPECIFIC";
        if (category == "GENERAL_BUSINESS" && mentionsEmployer) category = "RESUME_EXPERIENCE";
        bool isFollowUp = FollowUpCue.IsMatch(lower) || TextNormalizer.WordCount(q) <= 4 && !lower.Contains("yourself");
        if (isFollowUp && category == "GENERAL_BUSINESS") category = "FOLLOW_UP";

        AnswerMode mode;
        if (PersonalHistory.IsMatch(lower) && GapTopics.IsMatch(lower)) { mode = AnswerMode.Bridge; category = category == "GENERAL_BUSINESS" ? "GAP_EXPERIENCE" : category; }
        else if (VerifiedCue.IsMatch(lower) || PersonalHistory.IsMatch(lower) || (mentionsEmployer && !Hypothetical.IsMatch(lower))) mode = AnswerMode.Verified;
        else if (Hypothetical.IsMatch(lower)) mode = AnswerMode.Hypothetical;
        else mode = AnswerMode.Hypothetical;

        return new Classification(category, mode, isFollowUp);
    }

    private static bool ContainsWord(string haystack, string word) =>
        word.Length > 0 && Regex.IsMatch(haystack, @"(^|[^\p{L}\p{N}])" + Regex.Escape(word) + @"($|[^\p{L}\p{N}])");
}
