using System.Text.RegularExpressions;
using InterviewAssistant.Core.Domain;
using InterviewAssistant.Core.Languages;

namespace InterviewAssistant.Core.Ingestion;

public sealed class ParsedResume
{
    public string Name { get; set; } = "";
    public string Headline { get; set; } = "";
    public string Location { get; set; } = "";
    public string Language { get; set; } = "und";
    public List<ProfileFact> Facts { get; } = new();
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Deterministic résumé structure extraction (no network, no AI). Every fact carries provenance (document id,
/// line numbers, snippet) and starts as <see cref="FactStatus.Source"/>: it is not a verified claim until the
/// user confirms it. Ambiguities are flagged rather than guessed.
/// </summary>
public static class ResumeParser
{
    private enum Section { Header, Summary, Experience, Education, Skills, Languages, Certifications, Projects, Other }

    private static readonly (Section Section, Regex Heading)[] Headings =
    {
        (Section.Experience, H(@"(professional |work |relevant )?experience|employment( history)?|career history|berufserfahrung|beruflicher werdegang|exp[ée]rience(s)? professionnelle(s)?|experiencia( laboral| profesional)?|experi[êe]ncia( profissional)?|esperienz[ae]( lavorativ[ae]| professional[ie])?|سوابق (کاری|شغلی)|تجربه کاری|الخبرات?( المهنية| العملية)?|工作经[历歷]|工作经验|कार्य अनुभव|अनुभव")),
        (Section.Education, H(@"education|academic background|ausbildung|bildung|studium|formation|éducation|educaci[óo]n|formaci[óo]n|forma[çc][ãa]o|educa[çc][ãa]o|istruzione|formazione|تحصیلات|التعليم|المؤهلات العلمية|教育(背景|经历)?|शिक्षा")),
        (Section.Skills, H(@"(core |key |technical )?(skills|competencies|competences)|kenntnisse|kompetenzen|f[äa]higkeiten|comp[ée]tences|habilidades|competencias|compet[êe]ncias|competenze|abilit[àa]|مهارت(‌|\s)?ها|مهارت|المهارات|技能|专业技能|कौशल")),
        (Section.Languages, H(@"languages?|sprachen|sprachkenntnisse|langues|idiomas|lingue|زبان(‌|\s)?ها|زبان|اللغات|语言(能力)?|भाषाएँ|भाषा")),
        (Section.Certifications, H(@"certifications?|certificates?|licen[cs]es|zertifikate|zertifizierungen|certificaciones|certifica[çc][õo]es|certificazioni|گواهینامه(‌|\s)?ها|الشهادات|证书|प्रमाणपत्र")),
        (Section.Summary, H(@"(professional )?summary|profile|about( me)?|objective|zusammenfassung|profil|kurzprofil|r[ée]sum[ée] professionnel|perfil( profesional| profissional)?|resumen|resumo|sommario|profilo|خلاصه|درباره من|الملخص|نبذة|个人简介|简介|सारांश|प्रोफ़ाइल")),
        (Section.Projects, H(@"projects?|projekte|projets|proyectos|projetos|progetti|پروژه(‌|\s)?ها|المشاريع|项目|परियोजनाएँ")),
    };

    private static Regex H(string body) => new(@"^\s*(" + body + @")\s*:?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const string PresentWords = @"present|current|now|today|heute|aktuell|actuel(lement)?|aujourd'hui|actualidad|presente|actual|atual|attuale|oggi|اکنون|تاکنون|حال|حتى الآن|الحالي|至今|现在|वर्तमान";
    private static readonly Regex PeriodRx = new(@"((?:(?:jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)[a-z]*\.?\s+|\d{1,2}[/.])?(?:19|20)\d{2})\s*(?:–|—|-|to|bis|à|a|al|até|fino a|تا|إلى|至|से)\s*((?:(?:jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)[a-z]*\.?\s+|\d{1,2}[/.])?(?:19|20)\d{2}|" + PresentWords + ")", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MetricRx = new(@"[$€£]?\d[\d,.]*\s?(%|percent|k|m|million|bn|billion|x)?\+?(?=\b|\s|$|%)|(?:\d[\d,.]*)\s?(?:to|→|->)\s?\d[\d,.]*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BulletRx = new(@"^\s*([•·▪■◦●\-\*–]|\d+[.)])\s+", RegexOptions.Compiled);
    private static readonly char[] Separators = { '|', '·', '•', '—', '–', ',' };

    public static ParsedResume Parse(SourceDocument doc)
    {
        var result = new ParsedResume { Language = LanguageDetector.DetectOr(doc.Text, "und", 0.4) };
        var lines = doc.Text.Split('\n').Select(l => l.Trim()).ToList();
        var section = Section.Header;
        ProfileFact? currentRole = null;
        string? pendingTitle = null; int pendingTitleLine = -1;
        int headerLines = 0;

        ProfileFact Fact(FactKind kind, string text, int start, int end, double conf = 0.85) => new()
        {
            Kind = kind, Text = text.Trim(), Confidence = conf, Language = result.Language,
            Provenance = new Provenance(doc.Id, start + 1, end + 1, string.Join(" ", lines.Skip(start).Take(end - start + 1)).Trim() is var snip && snip.Length > 240 ? snip[..240] : string.Join(" ", lines.Skip(start).Take(end - start + 1)).Trim()),
        };

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue;
            var heading = Headings.FirstOrDefault(h => h.Heading.IsMatch(line));
            if (heading.Heading != null && line.Length < 60) { section = heading.Section; pendingTitle = null; continue; }
            bool isBullet = BulletRx.IsMatch(line);
            var content = BulletRx.Replace(line, "").Trim();

            switch (section)
            {
                case Section.Header:
                    headerLines++;
                    if (result.Name.Length == 0 && LooksLikeName(content)) { result.Name = ToTitle(content); continue; }
                    if (content.Contains('@') || Regex.IsMatch(content, @"\+?\d[\d\s()-]{7,}") || content.Contains("linkedin", StringComparison.OrdinalIgnoreCase)) continue;
                    if (result.Headline.Length == 0 && result.Name.Length > 0) { result.Headline = content; continue; }
                    if (result.Location.Length == 0 && content.Contains(',') && content.Length < 120) { result.Location = content.Split('|')[0].Trim(); continue; }
                    if (headerLines > 6) { section = Section.Summary; goto case Section.Summary; }
                    break;

                case Section.Summary:
                    result.Facts.Add(Fact(FactKind.Summary, content, i, i, 0.9));
                    break;

                case Section.Experience:
                    var period = PeriodRx.Match(content);
                    if (!isBullet && period.Success)
                    {
                        // "Company | Location | 2019 – 2023"  or  "Title, Company, 2019-2023"  or  "Title at Company (2019-2023)"
                        var before = content[..period.Index].Trim().TrimEnd('(', '|', ',', '·', '-', '–', '—').Trim();
                        var parts = before.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                        string title = pendingTitle ?? "", company = "", location = "";
                        int startLine = pendingTitle != null ? pendingTitleLine : i;
                        var atMatch = Regex.Match(before, @"^(?<t>.+?)\s+(?:at|@|bei|chez|en|na|presso|در|في|在)\s+(?<c>.+)$", RegexOptions.IgnoreCase);
                        if (pendingTitle == null && atMatch.Success) { title = atMatch.Groups["t"].Value.Trim(); company = atMatch.Groups["c"].Value.Trim(); }
                        else if (pendingTitle != null) { company = parts.ElementAtOrDefault(0) ?? ""; location = string.Join(", ", parts.Skip(1)); }
                        else if (parts.Count == 1) company = parts[0]; // only one name: cannot know if it is title or company → company, flagged
                        else { title = parts.ElementAtOrDefault(0) ?? ""; company = parts.ElementAtOrDefault(1) ?? ""; location = string.Join(", ", parts.Skip(2)); }
                        currentRole = Fact(FactKind.Role, $"{title} — {company}".Trim(' ', '—'), startLine, i, 0.9);
                        currentRole.Title = title; currentRole.Company = company; currentRole.Location = location;
                        currentRole.Period = NormalizePeriod(period.Value);
                        if (title.Length == 0) currentRole.Flags.Add("missing-title");
                        if (company.Length == 0) currentRole.Flags.Add("missing-company");
                        if (currentRole.Flags.Count > 0) currentRole.Confidence = 0.5;
                        result.Facts.Add(currentRole);
                        pendingTitle = null;
                    }
                    else if (!isBullet && content.Length < 90 && !content.EndsWith('.'))
                    {
                        pendingTitle = content; pendingTitleLine = i; // title line preceding "Company | Location | Period"
                    }
                    else
                    {
                        // Achievement bullet (may wrap onto following non-bullet lines).
                        int end = i;
                        while (end + 1 < lines.Count && lines[end + 1].Length > 0 && !BulletRx.IsMatch(lines[end + 1]) && !PeriodRx.IsMatch(lines[end + 1])
                               && char.IsLower(lines[end + 1][0]) && Headings.All(h => !h.Heading.IsMatch(lines[end + 1]))) { content += " " + lines[++end]; }
                        var f = Fact(FactKind.Achievement, content, i, end);
                        if (currentRole != null) { f.ParentId = currentRole.Id; f.Company = currentRole.Company; f.Title = currentRole.Title; f.Period = currentRole.Period; }
                        else f.Flags.Add("unattached-to-role");
                        f.Metrics = ExtractMetrics(content);
                        result.Facts.Add(f);
                        i = end;
                    }
                    break;

                case Section.Education:
                    if (i + 1 < lines.Count && lines[i + 1].Length > 0 && Headings.All(h => !h.Heading.IsMatch(lines[i + 1])) && !BulletRx.IsMatch(lines[i + 1])
                        && Regex.IsMatch(lines[i + 1], @"universit|college|school|institut|hochschule|école|universidad|universidade|università|دانشگاه|جامعة|大学|विश्वविद्यालय", RegexOptions.IgnoreCase))
                    {
                        var ed = Fact(FactKind.Education, $"{content} — {lines[i + 1]}", i, i + 1, 0.9);
                        ed.Title = content; ed.Company = lines[i + 1];
                        result.Facts.Add(ed); i++;
                    }
                    else result.Facts.Add(Fact(FactKind.Education, content, i, i, 0.8));
                    break;

                case Section.Skills:
                    foreach (var skill in isBullet ? new List<string> { content } : SplitOutsideBrackets(content, ",;|•·"))
                        if (skill.Length > 1) result.Facts.Add(Fact(FactKind.Skill, skill, i, i, 0.9));
                    break;

                case Section.Languages:
                    foreach (var lang in content.Split(new[] { '|', ',', ';', '•', '·' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (lang.Length > 1) result.Facts.Add(Fact(FactKind.Language, lang, i, i, 0.9));
                    break;

                case Section.Certifications:
                    result.Facts.Add(Fact(FactKind.Certification, content, i, i, 0.85)); break;
                case Section.Projects:
                    var pf = Fact(FactKind.Project, content, i, i, 0.8); pf.Metrics = ExtractMetrics(content); result.Facts.Add(pf); break;
                default:
                    result.Facts.Add(Fact(FactKind.Other, content, i, i, 0.5)); break;
            }
        }

        // Consistency checks → flags (never silent fixes).
        var roles = result.Facts.Where(f => f.Kind == FactKind.Role).ToList();
        if (roles.Count == 0) result.Warnings.Add("No employment history detected — please check the experience section or paste your roles.");
        foreach (var r in roles)
        {
            var yrs = Regex.Matches(r.Period, @"(19|20)\d{2}").Select(m => int.Parse(m.Value)).ToList();
            if (yrs.Count == 2 && yrs[1] < yrs[0]) { r.Flags.Add("end-before-start"); r.Confidence = 0.4; }
            if (yrs.Count >= 1 && yrs[0] > DateTime.UtcNow.Year) { r.Flags.Add("future-date"); r.Confidence = 0.4; }
        }
        if (result.Name.Length == 0) result.Warnings.Add("Name not detected.");
        return result;
    }

    /// <summary>Splits on separators that are not inside (), [] — keeps "CRM (Salesforce, HubSpot)" intact.</summary>
    public static List<string> SplitOutsideBrackets(string text, string separators)
    {
        var parts = new List<string>(); var cur = new System.Text.StringBuilder(); int depth = 0;
        foreach (var c in text)
        {
            if (c is '(' or '[') depth++;
            if (c is ')' or ']') depth = Math.Max(0, depth - 1);
            if (depth == 0 && separators.Contains(c)) { if (cur.ToString().Trim().Length > 0) parts.Add(cur.ToString().Trim()); cur.Clear(); }
            else cur.Append(c);
        }
        if (cur.ToString().Trim().Length > 0) parts.Add(cur.ToString().Trim());
        return parts;
    }

    public static List<string> ExtractMetrics(string text)
    {
        var list = new List<string>();
        foreach (Match m in MetricRx.Matches(text))
        {
            var v = m.Value.Trim().TrimEnd('.', ',');
            if (v.Length == 0) continue;
            if (Regex.IsMatch(v, @"^(19|20)\d{2}$")) continue; // a year, not a metric
            if (!list.Contains(v)) list.Add(v);
        }
        return list;
    }

    private static string NormalizePeriod(string p) => Regex.Replace(p, @"\s*(–|—|-|to|bis|à|a|al|até|fino a)\s*", " – ", RegexOptions.IgnoreCase).Trim();

    private static bool LooksLikeName(string s)
    {
        if (s.Length is < 3 or > 60 || Regex.IsMatch(s, @"\d|@|\|")) return false;
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length is >= 1 and <= 5 && words.All(w => char.IsLetter(w[0]));
    }

    private static string ToTitle(string s) =>
        s.Any(char.IsLower) ? s : string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.Length > 1 && w.All(c => !char.IsLetter(c) || char.IsUpper(c)) ? char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant() : w));
}
