using System.Text.RegularExpressions;
using InterviewAssistant.Core.Languages;

namespace InterviewAssistant.Core.Ingestion;

public sealed class JobAnalysis
{
    public string Title { get; set; } = "";
    public string Company { get; set; } = "";
    public string Language { get; set; } = "und";
    public List<string> Responsibilities { get; } = new();
    public List<string> Requirements { get; } = new();
    public List<string> NiceToHave { get; } = new();
    public List<string> About { get; } = new();
    public List<string> Terminology { get; } = new();
    public List<string> SecurityFlags { get; } = new();
}

/// <summary>Extracts a language-neutral structure from a job description (any supported language). Untrusted input.</summary>
public static class JobDescriptionParser
{
    private enum S { Unknown, About, Responsibilities, Requirements, Nice, Benefits }

    private static readonly (S Section, Regex Rx)[] Headings =
    {
        (S.Responsibilities, R(@"(key )?responsibilities|what you('| wi)ll do|your (role|mission|tasks)|the role|duties|aufgaben|ihre aufgaben|deine aufgaben|missions?|vos missions|responsabilit[ée]s|responsabilidades|funciones|atribui[çc][õo]es|responsabilit[àa]|mansioni|مسئولیت(‌|\s)?ها|شرح وظایف|المسؤوليات|المهام|岗位职责|工作职责|职责|जिम्मेदारियां|ज़िम्मेदारियाँ")),
        (S.Requirements, R(@"requirements|qualifications|what you('| wi)ll bring|what we('| a)re looking for|who you are|must[- ]have|your profile|profile|anforderungen|ihr profil|dein profil|qualifikationen|profil recherch[ée]|pr[ée]requis|requisitos|perfil|requisiti|competenze richieste|الزامات|شرایط احراز|المتطلبات|المؤهلات|任职要求|岗位要求|要求|आवश्यकताएं|योग्यता")),
        (S.Nice, R(@"nice[- ]to[- ]have|bonus( points)?|preferred( qualifications)?|plus|good to have|w[üu]nschenswert|von vorteil|atouts?|serait un plus|deseable|valorable|diferencial|desej[aá]vel|gradit[oa]|costituisce un plus|مزیت|امتیاز|ميزة إضافية|يفضل|加分项|优先|अच्छा होगा")),
        (S.Benefits, R(@"benefits|what we offer|perks|wir bieten|avantages|beneficios|benef[ií]cios|benefici|مزایا|المزايا|福利|लाभ")),
        (S.About, R(@"about (us|the company|the team)|who we are|[üu]ber uns|qui sommes-nous|[àa] propos|sobre n[óo]s(otros)?|chi siamo|درباره ما|من نحن|关于我们|हमारे बारे में")),
    };

    private static Regex R(string b) => new(@"^\s*(" + b + @")\s*:?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Bullet = new(@"^\s*([•·▪■◦●\-\*–]|\d+[.)])\s+", RegexOptions.Compiled);
    private static readonly HashSet<string> NotTerms = new(StringComparer.OrdinalIgnoreCase) { "The", "We", "You", "Our", "Your", "This", "As", "In", "If", "For", "And", "With", "Join", "About", "What", "Who", "Ideally", "Strong", "Experience", "Proven", "Ability", "Excellent", "Bonus", "Nice" };

    public static JobAnalysis Parse(string rawText, string? titleHint = null, string? companyHint = null)
    {
        var injection = PromptInjectionGuard.Sanitize(rawText);
        var text = injection.Text;
        var a = new JobAnalysis { Language = LanguageDetector.DetectOr(text, "und", 0.4), Title = titleHint ?? "", Company = companyHint ?? "" };
        a.SecurityFlags.AddRange(injection.Flags);
        var section = S.Unknown;
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var h = Headings.FirstOrDefault(x => x.Rx.IsMatch(line));
            if (h.Rx != null && line.Length < 60) { section = h.Section; continue; }
            var kv = Regex.Match(line, @"^(job title|title|position|role|company|employer)\s*:\s*(.+)$", RegexOptions.IgnoreCase);
            if (kv.Success)
            {
                if (kv.Groups[1].Value.ToLowerInvariant() is "company" or "employer") { if (a.Company.Length == 0) a.Company = kv.Groups[2].Value.Trim(); }
                else if (a.Title.Length == 0) a.Title = kv.Groups[2].Value.Trim();
                continue;
            }
            if (i == 0 && a.Title.Length == 0 && line.Length < 100 && !Bullet.IsMatch(line) && !line.EndsWith('.'))
            {
                var at = Regex.Match(line, @"^(?<t>.+?)\s+(?:at|@|bei|chez|en|presso|na|\|)\s+(?<c>[^|]+)$", RegexOptions.IgnoreCase);
                if (at.Success) { a.Title = at.Groups["t"].Value.Trim(); if (a.Company.Length == 0) a.Company = at.Groups["c"].Value.Trim(); }
                else a.Title = line.Trim();
                continue;
            }
            var item = Bullet.Replace(line, "").Trim();
            if (item.Length < 3) continue;
            switch (section)
            {
                case S.Responsibilities: a.Responsibilities.Add(item); break;
                case S.Requirements: a.Requirements.Add(item); break;
                case S.Nice: a.NiceToHave.Add(item); break;
                case S.About: a.About.Add(item); break;
                case S.Benefits: break;
                default:
                    if (Regex.IsMatch(item, @"\b(experience|knowledge|years|degree|proficien|familiar|understanding|skills?)\b", RegexOptions.IgnoreCase)) a.Requirements.Add(item);
                    else if (Bullet.IsMatch(line)) a.Responsibilities.Add(item);
                    else a.About.Add(item);
                    break;
            }
        }
        if (a.Company.Length == 0)
        {
            var m = Regex.Match(text, @"\b(?:at|join|about)\s+([A-Z][\w&.-]+(?:\s[A-Z][\w&.-]+)?)");
            if (m.Success && !NotTerms.Contains(m.Groups[1].Value)) a.Company = m.Groups[1].Value;
        }
        a.Terminology.AddRange(ExtractTerminology(text, a.Company));
        return a;
    }

    /// <summary>Acronyms, CamelCase/brand tokens and repeated capitalised phrases — preserved verbatim across languages.</summary>
    public static List<string> ExtractTerminology(string text, string company = "")
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        void Add(string t) { t = t.Trim().Trim('.', ',', ':', ';', '(', ')'); if (t.Length < 2 || NotTerms.Contains(t)) return; counts[t] = counts.GetValueOrDefault(t) + 1; }
        foreach (Match m in Regex.Matches(text, @"\b[A-Z][A-Z0-9]{1,}(?:[-/][A-Z0-9]+)*s?\b")) Add(m.Value);                  // KPI, MiCA-like acronyms, ERC-20
        foreach (Match m in Regex.Matches(text, @"\b[A-Z][a-z]+[A-Z][A-Za-z]+\b")) Add(m.Value);                               // CamelCase (FinTech, HubSpot)
        foreach (Match m in Regex.Matches(text, @"(?<=[a-z,;]\s)[A-Z][a-z]{2,}(?:\s[A-Z][a-z]{2,})?\b")) Add(m.Value);       // mid-sentence capitalised (Jira, Product Owner)
        foreach (Match m in Regex.Matches(text, @"\b(MiCA|MiCAR|KYC|AML|API|SaaS|B2B|B2C|OKR|KPI|SQL|Jira|Scrum|Agile|Kanban|Figma)\b", RegexOptions.IgnoreCase)) Add(m.Value);
        if (company.Length > 0) counts.Remove(company);
        return counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Select(kv => kv.Key).Take(40).ToList();
    }
}
