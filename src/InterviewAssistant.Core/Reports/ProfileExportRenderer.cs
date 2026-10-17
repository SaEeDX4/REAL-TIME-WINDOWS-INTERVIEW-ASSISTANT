using System.Net;
using System.Text;
using InterviewAssistant.Core.Domain;
using InterviewAssistant.Core.Persistence;

namespace InterviewAssistant.Core.Reports;

/// <summary>
/// Readable, printable HTML of one candidate profile: name and headline, confirmed experience grouped by role, the
/// interview-profile answers, the facts still to review (clearly marked — the assistant does not use them as claims),
/// where each fact came from in words, and the source documents by name (never their text). Localized, mirrored for
/// Arabic/Persian, every value HTML-encoded and passed through <see cref="SecretFilter"/>.
/// </summary>
public static class ProfileExportRenderer
{
    public static string Render(CandidateProfileRecord p, string lang, string productName = "Interview Assistant", TimeZoneInfo? zone = null, DateTime? nowUtc = null)
    {
        lang = ExportLabels.Code(lang);
        string L(string k) => ExportLabels.Get(lang, k);
        string F(string k, params object?[] a) => ExportLabels.Format(lang, k, a);
        static string E(string? s) => WebUtility.HtmlEncode(SecretFilter.Redact(s ?? ""));
        var now = nowUtc ?? DateTime.UtcNow;

        var facts = p.Facts.Where(f => f.IsReviewable).ToList();
        bool IsAnswer(ProfileFact f) => f.Kind is FactKind.Personal or FactKind.Boundary;
        var confirmed = facts.Where(f => f.IsVerifiedClaim && !IsAnswer(f)).ToList();
        var answers = facts.Where(f => f.IsVerifiedClaim && IsAnswer(f)).ToList();
        var toReview = facts.Where(f => f.AwaitingReview).ToList();
        var roles = p.Facts.Where(f => f.Kind == FactKind.Role).GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First());

        string Source(ProfileFact f)
        {
            var source = f.Source switch
            {
                FactSource.ResumeImport => F("fact_source_resume", f.SourceName.Length > 0 ? f.SourceName : DocName(p, f) ?? L("fact_source_document")),
                FactSource.Manual => L("fact_source_manual"),
                FactSource.Questionnaire => L("fact_source_questionnaire"),
                FactSource.Document => f.SourceName.Length > 0 || DocName(p, f) != null ? F("fact_source_resume", f.SourceName.Length > 0 ? f.SourceName : DocName(p, f)) : L("fact_source_document"),
                _ => f.Provenance != null ? (DocName(p, f) is { } n ? F("fact_source_resume", n) : L("fact_source_document")) : L("fact_source_unknown"),
            };
            if (f.Provenance is { LineStart: > 0 } pr && f.Source != FactSource.Manual) source += " · " + F("fact_line", pr.LineStart);
            if (f.Status == FactStatus.UserConfirmed && f.ConfirmedUtc is { } c) source += " · " + F("exp_confirmed_on", ExportLabels.ShortDate(lang, c, zone));
            return F("exp_source", source);
        }
        string Kind(ProfileFact f) => IsAnswer(f) && PersonalCategories.Find(f.Category) is { } c ? L($"pc_{c.Key}_label") : L("fact_kind_" + f.Kind.ToString().ToLowerInvariant());
        void Item(StringBuilder sb, ProfileFact f, bool showKind, bool badge)
        {
            sb.Append("<li>");
            if (showKind) sb.Append($"<strong>{E(Kind(f))}:</strong> ");
            sb.Append($"<bdi dir=\"auto\">{E(FactText(f))}</bdi>");
            if (f.Metrics.Count > 0) sb.Append($" <span class=\"src\">({E(string.Join(", ", f.Metrics))})</span>");
            if (badge) sb.Append($"<span class=\"badge\">{E(L("exp_badge_to_review"))}</span>");
            sb.Append($"<br><span class=\"src\">{E(Source(f))}</span></li>");
        }

        var name = p.Name.Trim().Length > 0 ? p.Name.Trim() : p.PreferredName.Trim();
        var sb = new StringBuilder();
        ExportLabels.Head(sb, lang, $"{L("exp_prof_title")} — {name}");
        sb.Append($"<p class=\"meta\">{E(L("exp_prof_title"))}</p><h1><bdi dir=\"auto\">{E(name)}</bdi></h1>");
        if (p.Headline.Trim().Length > 0) sb.Append($"<p><bdi dir=\"auto\">{E(p.Headline.Trim())}</bdi></p>");
        sb.Append("<dl>");
        if (p.PreferredName.Trim().Length > 0 && p.PreferredName.Trim() != name) sb.Append($"<dt>{E(L("exp_preferred_name"))}</dt><dd><bdi dir=\"auto\">{E(p.PreferredName.Trim())}</bdi></dd>");
        if (p.Location.Trim().Length > 0) sb.Append($"<dt>{E(L("exp_location"))}</dt><dd><bdi dir=\"auto\">{E(p.Location.Trim())}</bdi></dd>");
        sb.Append("</dl>");
        var stats = p.Stats;
        sb.Append($"<p class=\"meta\">{E(F("exp_prof_intro", ExportLabels.DateAndTime(lang, now, zone)))} {E(F("exp_prof_counts", stats.ConfirmedExperience, stats.ConfirmedProfileAnswers, stats.ToReview))}</p>");

        // ---- confirmed experience, grouped by role (in résumé order)
        sb.Append($"<h2>{E(L("exp_section_experience"))}</h2>");
        if (confirmed.Count == 0) sb.Append($"<p class=\"meta\">{E(L("exp_none"))}</p>");
        foreach (var summary in confirmed.Where(f => f.Kind == FactKind.Summary)) sb.Append($"<p><bdi dir=\"auto\">{E(summary.Text)}</bdi></p>");
        var byRole = confirmed.Where(f => f.Kind != FactKind.Summary && f.Kind != FactKind.Role && f.ParentId != null && roles.ContainsKey(f.ParentId)).GroupBy(f => f.ParentId!).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var role in p.Facts.Where(f => f.Kind == FactKind.Role && ((f.IsVerifiedClaim) || byRole.ContainsKey(f.Id))))
        {
            sb.Append($"<h3><bdi dir=\"auto\">{E(FactText(role))}</bdi>{(role.AwaitingReview ? $"<span class=\"badge\">{E(L("exp_badge_to_review"))}</span>" : "")}</h3>");
            if (role.Kind == FactKind.Role && role.Text.Trim().Length > 0 && role.Text.Trim() != FactText(role)) sb.Append($"<p><bdi dir=\"auto\">{E(role.Text)}</bdi></p>");
            sb.Append($"<p class=\"src\">{E(Source(role))}</p>");
            if (byRole.TryGetValue(role.Id, out var items))
            {
                sb.Append("<ul>");
                foreach (var f in items) Item(sb, f, showKind: f.Kind != FactKind.Achievement, badge: false);
                sb.Append("</ul>");
            }
        }
        var other = confirmed.Where(f => f.Kind != FactKind.Summary && f.Kind != FactKind.Role && !(f.ParentId != null && roles.ContainsKey(f.ParentId))).ToList();
        if (other.Count > 0)
        {
            sb.Append($"<h3>{E(L("exp_prof_other_experience"))}</h3><ul>");
            foreach (var f in other.OrderBy(f => f.Kind)) Item(sb, f, showKind: true, badge: false);
            sb.Append("</ul>");
        }

        // ---- interview-profile answers (personal topics and boundaries), in questionnaire order
        sb.Append($"<h2>{E(L("exp_section_answers"))}</h2>");
        if (answers.Count == 0) sb.Append($"<p class=\"meta\">{E(L("exp_none"))}</p>");
        else
        {
            sb.Append("<ul>");
            foreach (var f in answers.OrderBy(f => Order(f.Category))) Item(sb, f, showKind: true, badge: false);
            sb.Append("</ul>");
        }

        // ---- still to review: listed, clearly marked, never presented as confirmed
        sb.Append($"<h2>{E(L("exp_section_review"))} ({toReview.Count})</h2>");
        if (toReview.Count == 0) sb.Append($"<p class=\"meta\">{E(L("exp_none"))}</p>");
        else
        {
            sb.Append($"<p class=\"note\">{E(L("exp_review_note"))}</p><ul>");
            foreach (var f in toReview) Item(sb, f, showKind: true, badge: true);
            sb.Append("</ul>");
        }

        // ---- source documents by name only
        if (p.Documents.Count > 0)
        {
            sb.Append($"<h2>{E(L("exp_section_source_docs"))}</h2><ul>");
            foreach (var d in p.Documents) sb.Append($"<li><bdi dir=\"auto\">{E(d.FileName)}</bdi> <span class=\"src\">· {E(F("exp_doc_added", ExportLabels.Date(lang, d.AddedUtc, zone)))}</span></li>");
            sb.Append("</ul>");
        }

        sb.Append($"<details><summary>{E(L("exp_technical"))}</summary><p>{E(L("exp_technical_note"))}</p><table><thead><tr><th>{E(L("exp_tech_item"))}</th><th>{E(L("exp_tech_id"))}</th></tr></thead><tbody>");
        sb.Append($"<tr><td>{E(L("exp_tech_profile"))}</td><td><code>{E(p.Id)}</code></td></tr>");
        foreach (var f in facts) sb.Append($"<tr><td>{E(L("exp_tech_fact"))} · {E(Kind(f))}</td><td><code>{E(f.Id)}</code></td></tr>");
        foreach (var d in p.Documents) sb.Append($"<tr><td>{E(L("exp_tech_document"))} · <bdi>{E(d.FileName)}</bdi></td><td><code>{E(d.Sha256.Length > 0 ? d.Sha256 : d.Id)}</code></td></tr>");
        sb.Append("</tbody></table></details>");
        sb.Append($"<p class=\"meta\">{E(F("exp_footer", productName, ExportLabels.DateAndTime(lang, now, zone)))}</p></body></html>");
        return sb.ToString();
    }

    /// <summary>The fact as one line: a role reads "Title — Company (Period)".</summary>
    public static string FactText(ProfileFact f) =>
        f.Kind == FactKind.Role ? $"{f.Title} — {f.Company} ({f.Period})".Replace(" ()", "").Replace(" —  (", " (").Trim(' ', '—') : f.Text;

    private static string? DocName(CandidateProfileRecord p, ProfileFact f) =>
        f.Provenance is { } pr ? p.Documents.FirstOrDefault(d => d.Id == pr.DocumentId)?.FileName is { Length: > 0 } n ? n : null : null;

    private static int Order(string category)
    {
        for (int i = 0; i < PersonalCategories.All.Count; i++) if (PersonalCategories.All[i].Key == category) return i;
        return int.MaxValue;
    }
}
