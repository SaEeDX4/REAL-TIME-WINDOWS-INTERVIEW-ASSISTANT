using System.Text.RegularExpressions;
using InterviewAssistant.Core.Knowledge;

namespace InterviewAssistant.Core.Intelligence;

public sealed record ValidationIssue(string Kind, string Detail, int BulletIndex);

public sealed record ValidationReport(IReadOnlyList<ValidationIssue> Issues, int WordCount, int BulletCount)
{
    public bool HasFactualIssues => Issues.Any(i => i.Kind is "UNVERIFIED_NUMBER" or "UNSUPPORTED_CLAIM" or "UNKNOWN_EMPLOYER");
}

/// <summary>
/// Guards against fabricated history: flags numbers not on the résumé, past-tense achievement claims in
/// hypothetical/bridge answers that aren't anchored to a verified employer, and unknown employers.
/// Also checks format (bullet count/length) for automated quality evaluation.
/// </summary>
public sealed class FactValidator
{
    private readonly HashSet<string> _numbers;
    private readonly List<string> _entities;
    private static readonly Regex NumberRegex = new(@"\b\d[\d,\.]*\s*(%|percent)?|\b(twenty-two|twenty-five|one|two|three|four|five|six|seven|eight|nine|ten|twelve|fifteen|eighteen|twenty|thirty|fifty|sixty|hundred)(\s|-)?(thousand|percent)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PastClaim = new(@"\bI\s+(built|implemented|led|managed|delivered|launched|designed|owned|created|developed|ran|was responsible for|have built|have owned|have implemented)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EmployerClaim = new(@"\b(?:at|with|while at|when I was at)\s+([A-Z][A-Za-z]+(?:\s[A-Z][A-Za-z]+)?)", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> WordNumbers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["one"] = "1", ["two"] = "2", ["three"] = "3", ["four"] = "4", ["five"] = "5", ["six"] = "6", ["seven"] = "7",
        ["eight"] = "8", ["nine"] = "9", ["ten"] = "10", ["twelve"] = "12", ["fifteen"] = "15", ["eighteen"] = "18",
        ["twenty"] = "20", ["twenty-two"] = "22", ["twenty-five"] = "25", ["thirty"] = "30", ["fifty"] = "50", ["sixty"] = "60", ["hundred"] = "100",
    };

    // Numbers that are domain knowledge rather than résumé claims (e.g. XAB supply, 30/60/90 plan, white paper facts).
    private static readonly HashSet<string> DomainNumbers = new() { "250", "250000000", "30", "60", "90", "1", "2", "3", "4", "5", "6", "20", "2024", "2025", "2026", "004", "100" };

    public FactValidator(CandidateProfile profile)
    {
        _numbers = new HashSet<string>(profile.VerifiedNumbers.Select(NormalizeNumber));
        _entities = profile.VerifiedEntities.Concat(new[] { "Teroxx", "Abloxx", "XAB", "Engineering", "Compliance", "Legal", "Finance", "Marketing", "CRM", "Design", "Product", "Security", "Ethereum", "Platinum", "Gold", "Silver", "Lite", "MiCA", "CySEC", "Jira", "Scrum", "QA" }).ToList();
    }

    public static string NormalizeNumber(string raw)
    {
        var s = raw.Trim().ToLowerInvariant().Replace("percent", "").Replace("%", "").Replace("+", "").Trim();
        var parts = s.Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0 && WordNumbers.TryGetValue(s, out var direct)) return direct;
        if (parts.Length == 2 && WordNumbers.TryGetValue(parts[0], out var n) && parts[1] == "thousand") return n + "000";
        if (parts.Length >= 1 && parts[^1] == "years") s = parts[0];
        if (WordNumbers.TryGetValue(s, out var w)) return w;
        s = s.Replace(",", "").Replace(" ", "");
        if (s.EndsWith("thousand")) s = s.Replace("thousand", "000");
        return s.TrimEnd('.');
    }

    public ValidationReport Validate(IReadOnlyList<string> bullets, AnswerMode mode, AnswerStyle style = AnswerStyle.Balanced)
    {
        var issues = new List<ValidationIssue>();
        int words = 0;
        for (int i = 0; i < bullets.Count; i++)
        {
            var b = bullets[i];
            var wc = TextNormalizer.WordCount(b);
            words += wc;
            if (style == AnswerStyle.Balanced && (wc < 6 || wc > 30)) issues.Add(new("BULLET_LENGTH", $"{wc} words", i));

            foreach (Match m in NumberRegex.Matches(b))
            {
                var n = NormalizeNumber(m.Value);
                if (n.Length == 0) continue;
                if (_numbers.Contains(n) || DomainNumbers.Contains(n)) continue;
                // Accept a number if any verified number matches ignoring formatting (e.g., "60000" vs "60,000").
                if (_numbers.Any(v => v.Replace(",", "") == n)) continue;
                issues.Add(new("UNVERIFIED_NUMBER", m.Value.Trim(), i));
            }

            foreach (Match m in EmployerClaim.Matches(b))
            {
                var name = m.Groups[1].Value;
                if (!_entities.Any(e => name.StartsWith(e, StringComparison.OrdinalIgnoreCase) || e.StartsWith(name, StringComparison.OrdinalIgnoreCase)) && PastClaim.IsMatch(b))
                    issues.Add(new("UNKNOWN_EMPLOYER", name, i));
            }

            if (mode != AnswerMode.Verified && PastClaim.IsMatch(b))
            {
                var anchored = _entities.Take(25).Any(e => b.Contains(e, StringComparison.OrdinalIgnoreCase));
                if (!anchored) issues.Add(new("UNSUPPORTED_CLAIM", PastClaim.Match(b).Value, i));
            }
        }
        if (style == AnswerStyle.Balanced && (bullets.Count < 2 || bullets.Count > 4)) issues.Add(new("BULLET_COUNT", bullets.Count.ToString(), -1));
        if (style == AnswerStyle.Balanced && words > 85) issues.Add(new("TOTAL_LENGTH", $"{words} words", -1));
        return new ValidationReport(issues, words, bullets.Count);
    }

    /// <summary>Rewrites or drops bullets with factual issues (used on LLM output before it is shown as final).</summary>
    public static string SoftenClaim(string bullet)
    {
        // "I built X" -> "I'd build X" style softening for hypothetical answers.
        return PastClaim.Replace(bullet, m =>
        {
            var verb = m.Groups[1].Value.ToLowerInvariant().Replace("have ", "");
            var baseVerb = verb switch
            {
                "built" => "build", "implemented" => "implement", "led" => "lead", "managed" => "manage", "delivered" => "deliver",
                "launched" => "launch", "designed" => "design", "owned" => "own", "created" => "create", "developed" => "develop",
                "ran" => "run", "was responsible for" => "be responsible for", _ => verb,
            };
            return "I'd " + baseVerb;
        });
    }
}
