using System.Text.RegularExpressions;
using InterviewAssistant.Core.Knowledge;

namespace InterviewAssistant.Core.Intelligence;

public sealed record ValidationIssue(string Kind, string Detail, int BulletIndex);

public sealed record ValidationReport(IReadOnlyList<ValidationIssue> Issues, int WordCount, int BulletCount)
{
    public bool HasFactualIssues => Issues.Any(i => i.Kind is "UNVERIFIED_NUMBER" or "UNSUPPORTED_CLAIM" or "UNKNOWN_EMPLOYER");
}

/// <summary>
/// Guards against fabricated history: flags numbers not on the résumé, first-person past claims ("I built…",
/// "I've implemented…", "I led…") that no confirmed evidence item supports — in every mode, including VERIFIED —
/// and unknown employers. Also checks format (bullet count/length) for automated quality evaluation.
/// </summary>
public sealed class FactValidator
{
    private readonly HashSet<string> _numbers;
    private readonly List<string> _entities;
    private static readonly Regex NumberRegex = new(@"\b\d[\d,\.]*\s*(%|percent)?|\b(twenty-two|twenty-five|one|two|three|four|five|six|seven|eight|nine|ten|twelve|fifteen|eighteen|twenty|thirty|fifty|sixty|hundred)(\s|-)?(thousand|percent)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Past-tense ownership claims. Present habits ("I run a weekly review") are not history; "I've run…" is.
    private static readonly Regex PastClaim = new(
        @"\bI(?:(?:'ve|’ve|\s+have)\s+(built|implemented|led|managed|delivered|launched|designed|owned|created|developed|run|been responsible for|been in charge of|set up|introduced|driven|scaled|overseen|headed|spearheaded|architected|shipped|rolled out|established)" +
        @"|\s+(built|implemented|led|managed|delivered|launched|designed|owned|created|developed|ran|was responsible for|was in charge of|set up|introduced|drove|scaled|oversaw|headed|spearheaded|architected|shipped|rolled out|established)" +
        @"|\s+did\s+(build|implement|lead|manage|deliver|launch|design|own|create|develop|run|set up|introduce|drive|scale|oversee|head|architect|ship|roll out|establish))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ClauseEnd = new(@"[.;!?]|,\s+(and|but|which|so)\b|\s+—\s+", RegexOptions.Compiled);
    private static readonly Regex EmployerClaim = new(@"\b(?:at|with|while at|when I was at)\s+([A-Z][A-Za-z]+(?:\s[A-Z][A-Za-z]+)?)", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> WordNumbers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["one"] = "1", ["two"] = "2", ["three"] = "3", ["four"] = "4", ["five"] = "5", ["six"] = "6", ["seven"] = "7",
        ["eight"] = "8", ["nine"] = "9", ["ten"] = "10", ["twelve"] = "12", ["fifteen"] = "15", ["eighteen"] = "18",
        ["twenty"] = "20", ["twenty-two"] = "22", ["twenty-five"] = "25", ["thirty"] = "30", ["fifty"] = "50", ["sixty"] = "60", ["hundred"] = "100",
    };

    // Generic numbers that are not candidate claims (30/60/90-day plans, small counts, recent years).
    private static readonly string[] GenericNumbers = { "30", "60", "90", "1", "2", "3", "4", "5", "6", "20", "100" };
    private static readonly string[] GenericEntities = { "Engineering", "Compliance", "Legal", "Finance", "Marketing", "CRM", "Design", "Product", "Security", "Sales", "Operations", "Support", "Data", "Jira", "Scrum", "Agile", "QA", "MiCA" };
    private readonly HashSet<string> DomainNumbers;
    private readonly EvidenceIndex _evidence;
    private readonly List<string> _profileEntities;

    public FactValidator(CandidateProfile profile, TargetContext? context = null, EvidenceIndex? evidence = null)
    {
        _evidence = evidence ?? new EvidenceIndex(profile, Array.Empty<CandidateStory>());
        _profileEntities = profile.VerifiedEntities.Concat(profile.Experience.Select(e => e.Company)).Where(e => e.Length > 1).Distinct().ToList();
        _numbers = new HashSet<string>(profile.VerifiedNumbers.Select(NormalizeNumber));
        var year = DateTime.UtcNow.Year;
        DomainNumbers = new HashSet<string>(GenericNumbers.Concat(new[] { (year - 2).ToString(), (year - 1).ToString(), year.ToString() })
            .Concat((context?.DomainNumbers ?? new()).Select(NormalizeNumber)));
        _entities = profile.VerifiedEntities
            .Concat(context == null ? Array.Empty<string>() : new[] { context.CompanyName }.Concat(context.Products).Concat(context.ExtraEntities))
            .Concat(GenericEntities).Where(e => !string.IsNullOrWhiteSpace(e)).ToList();
    }

    public static string NormalizeNumber(string raw)
    {
        var s = raw.Trim().Trim('-').ToLowerInvariant().Replace("percent", "").Replace("%", "").Replace("+", "").Trim();
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

            foreach (Match claim in PastClaim.Matches(b))
            {
                if (!IsSupported(b, claim)) { issues.Add(new("UNSUPPORTED_CLAIM", claim.Value, i)); break; }
            }
        }
        if (style == AnswerStyle.Balanced && (bullets.Count < 2 || bullets.Count > 4)) issues.Add(new("BULLET_COUNT", bullets.Count.ToString(), -1));
        if (style == AnswerStyle.Balanced && words > 85) issues.Add(new("TOTAL_LENGTH", $"{words} words", -1));
        return new ValidationReport(issues, words, bullets.Count);
    }

    /// <summary>
    /// A past claim is supported when a confirmed evidence item covers most of its clause ("I led the Arzif growth
    /// team to 60,000 traders" vs. the confirmed Arzif achievement). Generic words like "Engineering" never count.
    /// </summary>
    private bool IsSupported(string bullet, Match claim)
    {
        var rest = bullet[(claim.Index + claim.Length)..];
        var end = ClauseEnd.Match(rest);
        var clause = end.Success ? rest[..end.Index] : rest;
        // The clause may name the employer before the verb ("At Arzif, I led…"). Only the SAME clause counts: an
        // earlier clause such as the honest bridge ("I haven't owned that ledger, but I led…") must not dilute the claim.
        var lead = LeadIn(bullet[..claim.Index]);
        if (EvidenceIndex.Topic(clause).Count == 0) return _profileEntities.Any(e => lead.Contains(e, StringComparison.OrdinalIgnoreCase));
        return _evidence.Supports(clause, 0.5) || (EvidenceIndex.Topic(lead).Count > 0 && _evidence.Supports(lead + " " + clause, 0.5));
    }

    private static readonly Regex ClauseBoundary = new(@"(.*)(\bbut\b|\band\b|\bhowever\b|\balthough\b|\bwhile\b|\bthough\b|[.;!?]|\s[—–-]\s)", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
    private static string LeadIn(string before)
    {
        var m = ClauseBoundary.Match(before);
        return m.Success ? before[(m.Groups[1].Length + m.Groups[2].Length)..] : before;
    }

    /// <summary>True when the bullet contains a first-person past claim no confirmed evidence supports.</summary>
    public bool HasUnsupportedClaim(string bullet) => PastClaim.Matches(bullet).Any(m => !IsSupported(bullet, m));

    /// <summary>Rewrites or drops bullets with factual issues (used on LLM output before it is shown as final).</summary>
    public static string SoftenClaim(string bullet)
    {
        // "I built X" / "I've built X" -> "I'd build X": the same idea as a plan, never as history.
        return PastClaim.Replace(bullet, m =>
        {
            var verb = (m.Groups[1].Success ? m.Groups[1] : m.Groups[2].Success ? m.Groups[2] : m.Groups[3]).Value.ToLowerInvariant();
            var baseVerb = verb switch
            {
                "built" => "build", "implemented" => "implement", "led" => "lead", "managed" => "manage", "delivered" => "deliver",
                "launched" => "launch", "designed" => "design", "owned" => "own", "created" => "create", "developed" => "develop",
                "ran" or "run" => "run", "was responsible for" or "been responsible for" => "be responsible for", "was in charge of" or "been in charge of" => "be in charge of",
                "set up" => "set up", "introduced" => "introduce", "drove" or "driven" => "drive", "scaled" => "scale", "oversaw" or "overseen" => "oversee", "headed" => "head",
                "spearheaded" => "spearhead", "architected" => "architect", "shipped" => "ship", "rolled out" => "roll out", "established" => "establish",
                _ => verb,
            };
            return "I'd " + baseVerb;
        });
    }
}
