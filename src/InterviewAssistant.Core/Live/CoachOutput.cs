using System.Text.RegularExpressions;

namespace InterviewAssistant.Core.Live;

public enum Presentation { Answer, Coach }

/// <summary>Coach Mode contract: exactly 3 keywords, one short structure (with arrows), optional one-line reminder.</summary>
public sealed record CoachOutput(IReadOnlyList<string> Keywords, string Structure, string? Reminder)
{
    public string KeywordLine => string.Join(" · ", Keywords);

    public bool IsValid => Keywords.Count == 3 && Keywords.All(k => k.Length is > 0 and <= 32) && Structure.Contains('→') && Structure.Length <= 90;

    /// <summary>Parses model output "KEYWORDS: A · B · C / STRUCTURE: x → y → z / REMINDER: …" (labels may be in any case).</summary>
    public static CoachOutput? Parse(string text)
    {
        string? kw = null, st = null, rem = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('-', '*', '•').Trim();
            var m = Regex.Match(line, @"^(KEYWORDS|STRUCTURE|REMINDER)\s*:\s*(.+)$", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            switch (m.Groups[1].Value.ToUpperInvariant())
            {
                case "KEYWORDS": kw = m.Groups[2].Value; break;
                case "STRUCTURE": st = m.Groups[2].Value; break;
                case "REMINDER": rem = m.Groups[2].Value.Trim(); break;
            }
        }
        if (kw == null || st == null) return null;
        var keys = Regex.Split(kw, @"\s*[·•|,،、]\s*").Select(k => k.Trim().Trim('"')).Where(k => k.Length > 0).Take(3).ToList();
        if (keys.Count < 3) return null;
        var structure = Regex.Replace(st.Trim(), @"\s*(->|=>|⇒|›)\s*", " → ");
        var o = new CoachOutput(keys.Select(k => Regex.IsMatch(k, @"\p{Lu}|\p{Ll}") ? k.ToUpper(System.Globalization.CultureInfo.InvariantCulture) : k).ToList(), structure, string.IsNullOrWhiteSpace(rem) ? null : rem);
        return o.IsValid ? o : null;
    }

    public static CoachOutput FromPrepared(IReadOnlyList<string> keywords, string structure) =>
        new(keywords.Take(3).Concat(Enumerable.Repeat("RESULT", Math.Max(0, 3 - keywords.Count))).ToList(), structure.Contains('→') ? structure : "Direct answer → approach → result", null);
}
