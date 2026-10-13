using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using InterviewAssistant.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace InterviewAssistant.Backend.Services;

/// <summary>
/// Builds the speech-recognition bias prompt for a live session from the CURRENT user's OWN target document
/// (job title, company, vocabulary). The lookup is keyed by (target id, user id) — another user's document can never
/// be used, even if its id is known. Terms are sanitized and the prompt is bounded.
/// </summary>
public static class TranscriptionPrompt
{
    public const string Generic = "Job interview. Professional business and technology vocabulary.";
    private static readonly Regex SafeTerm = new(@"^[\p{L}\p{N}][\p{L}\p{N} .&+/'’-]{0,47}$", RegexOptions.Compiled);

    public static async Task<string> BuildAsync(AppDbContext db, Guid userId, Guid? targetId, int maxChars, CancellationToken ct)
    {
        if (targetId == null) return Generic;
        var json = await db.Documents.AsNoTracking()
            .Where(d => d.Id == targetId && d.UserId == userId && d.Kind == "target")
            .Select(d => d.DataJson).FirstOrDefaultAsync(ct);
        return json == null ? Generic : FromJson(json, maxChars);
    }

    public static string FromJson(string json, int maxChars = 1500)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return Generic;
            string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? Clean(v.GetString()) : "";
            var title = S("jobTitle");
            var company = S("company");
            var vocab = r.TryGetProperty("vocabulary", out var arr) && arr.ValueKind == JsonValueKind.Array
                ? arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => Clean(x.GetString())).Where(t => SafeTerm.IsMatch(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(80).ToList()
                : new List<string>();
            var sb = new StringBuilder("Job interview");
            if (title.Length > 0) sb.Append(" for a ").Append(title).Append(" role");
            if (company.Length > 0) sb.Append(" at ").Append(company);
            sb.Append('.');
            if (vocab.Count > 0) sb.Append(" Terms: ").Append(string.Join(", ", vocab)).Append('.');
            var p = sb.ToString();
            if (p.Length <= maxChars) return p;
            var cut = p[..maxChars];
            var comma = cut.LastIndexOf(',');
            return (comma > 0 ? cut[..comma] : cut) + ".";
        }
        catch (JsonException) { return Generic; }
    }

    private static string Clean(string? s)
    {
        var t = Regex.Replace(s ?? "", @"[\u0000-\u001f]+", " ").Trim();
        return t.Length > 120 ? t[..120].Trim() : t;
    }
}
