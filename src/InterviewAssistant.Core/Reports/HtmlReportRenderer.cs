using System.Net;
using System.Text;
using InterviewAssistant.Core.Languages;

namespace InterviewAssistant.Core.Reports;

/// <summary>
/// Self-contained, printable HTML (print → PDF via the OS). All user/interviewer content is HTML-encoded. Semantic
/// markup with real text separators, so copying into Word, e-mail, plain text or a chat tool keeps the spacing
/// ("Q1. Question" — never "Q1micarVerifiedenQuestion").
/// </summary>
public static class HtmlReportRenderer
{
    /// <summary>"Shervin" + "Shervin · Product Owner · Teroxx" → "Shervin · Product Owner · Teroxx" (no duplicated name).</summary>
    public static string Header(string profileLabel, string targetLabel)
    {
        var parts = new List<string>();
        foreach (var p in new[] { profileLabel, targetLabel }.SelectMany(x => (x ?? "").Split('·')).Select(x => x.Trim()).Where(x => x.Length > 0))
            if (!parts.Any(e => e.Equals(p, StringComparison.OrdinalIgnoreCase) || e.StartsWith(p + " ", StringComparison.OrdinalIgnoreCase) || p.StartsWith(e + " ", StringComparison.OrdinalIgnoreCase) && FirstWord(e) == FirstWord(p)))
                parts.Add(p);
        return string.Join(" · ", parts);
    }

    private static string FirstWord(string s) => s.Split(' ')[0].ToLowerInvariant();

    public static string Render(InterviewReport r, string productName = "Interview Assistant")
    {
        string L(string k) => ReportLabels.Get(r.ReportLanguage, k);
        string E(string s) => WebUtility.HtmlEncode(s);
        var rtl = LanguageRegistry.IsRtl(r.ReportLanguage);
        var sb = new StringBuilder();
        sb.Append($"<!doctype html><html lang=\"{E(r.ReportLanguage)}\" dir=\"{(rtl ? "rtl" : "ltr")}\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append($"<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\"><title>{E(L("title"))} — {E(r.TargetLabel)}</title><style>");
        sb.Append("body{font-family:'Segoe UI',system-ui,sans-serif;max-width:860px;margin:32px auto;padding:0 20px;color:#1b1f24;line-height:1.5}");
        sb.Append("h1{font-size:24px;margin:0 0 4px}h2{font-size:16px;margin:28px 0 8px;border-bottom:1px solid #e3e6ea;padding-bottom:4px}");
        sb.Append(".meta{color:#5b6470;font-size:13px}.note{background:#fff7e6;border:1px solid #f2d091;border-radius:8px;padding:10px 14px;font-size:13px;margin:16px 0}");
        sb.Append(".q{border:1px solid #e3e6ea;border-radius:8px;padding:10px 14px;margin:8px 0;break-inside:avoid}.q h3{font-size:15px;margin:0 0 2px}.q.unc{border-style:dashed;background:#fafbfc}.q .labels,.q .ctx{font-size:12px;color:#5b6470;margin:2px 0 6px}");
        sb.Append(".inf{font-size:11px;font-weight:400;color:#8a5a00}ul{margin:6px 0;padding-inline-start:22px}bdi{unicode-bidi:isolate}</style></head><body>");
        sb.Append($"<h1>{E(L("title"))}</h1><p class=\"meta\"><bdi>{E(Header(r.ProfileLabel, r.TargetLabel))}</bdi> · {r.StartedUtc:yyyy-MM-dd HH:mm} UTC · {E(L("duration"))}: {(int)(r.EndedUtc - r.StartedUtc).TotalMinutes} min · {E(L("language"))}: {E(string.Join(", ", r.LanguagesUsed.Select(LanguageRegistry.EnglishName)))}</p>");
        if (!r.CandidateMicrophoneEnabled) sb.Append($"<p class=\"note\">{E(L("honesty"))}</p>");

        sb.Append($"<h2>{E(L("questions"))} ({r.Questions.Count})</h2>");
        foreach (var q in r.Questions)
        {
            sb.Append("<section class=\"q\">");
            sb.Append($"<h3>Q{q.Index}. <bdi dir=\"auto\">{E(q.OriginalWording)}</bdi></h3>");
            sb.Append($"<p class=\"labels\">{E(L("category"))}: {E(q.Category.ToLowerInvariant().Replace('_', ' '))} · {E(L("mode"))}: {E(q.Mode.ToLowerInvariant())} · {E(L("language"))}: {E(q.DetectedLanguage)}{(q.Typed ? " · " + E(L("typed")) : "")}</p>");
            if (q.Context.Length > 0) sb.Append($"<p class=\"ctx\">{E(L("context"))}: <bdi dir=\"auto\">{E(q.Context)}</bdi></p>");
            if (q.SuggestionsShown.Count > 0) { sb.Append("<ul>"); foreach (var s in q.SuggestionsShown) sb.Append($"<li><bdi dir=\"auto\">{E(s)}</bdi></li>"); sb.Append("</ul>"); }
            sb.Append("</section>");
        }
        if (r.UncertainQuestions.Count > 0)
        {
            sb.Append($"<h2>{E(L("uncertain"))} ({r.UncertainQuestions.Count})</h2><p class=\"note\">{E(L("uncertain_note"))}</p>");
            foreach (var q in r.UncertainQuestions)
                sb.Append($"<section class=\"q unc\"><h3>Q{q.Index}. <bdi dir=\"auto\">{E(q.OriginalWording)}</bdi></h3><p class=\"labels\">{E(q.UncertainReason)}</p></section>");
        }
        if (r.ExcludedTurns > 0) sb.Append($"<p class=\"meta\">{E(L("excluded"))}: {r.ExcludedTurns}</p>");

        void List(string key, IEnumerable<string> items, bool inference = false)
        {
            var l = items.ToList();
            sb.Append($"<h2>{E(L(key))}{(inference ? $" <span class=\"inf\">({E(L("inference"))})</span>" : "")}</h2>");
            if (l.Count == 0) { sb.Append($"<p class=\"meta\">{E(L("none"))}</p>"); return; }
            sb.Append("<ul>"); foreach (var i in l) sb.Append($"<li><bdi dir=\"auto\">{E(i)}</bdi></li>"); sb.Append("</ul>");
        }
        List("difficulty", r.DifficultyAreas, true);
        List("stories", r.StoriesRecommended.Select(s => $"{s.Label} ×{s.Count}"));
        List("metrics", r.MetricsRecommended.Select(m => $"{m.Metric} ×{m.Count}"));
        List("repeated", r.RepeatedTopics);
        List("notcovered", r.TopicsNotCovered, true);
        List("gaps", r.GapQuestions);
        List("interruptions", r.Interruptions.Select(i => Languages.Loc.Format(r.ReportLanguage, "rpt_interruption", i.AfterQuestionIndex, Math.Round(i.Confidence, 2), Math.Round(i.ElapsedMs / 1000.0, 1), Math.Round(i.ExpectedMs / 1000.0, 1))), true);
        List("latency", r.LatencyP50Ms >= 0 ? new[] { Languages.Loc.Format(r.ReportLanguage, "rpt_latency", r.LatencyP50Ms, r.LatencyP95Ms) } : Array.Empty<string>());
        List("transcription", r.TranscriptionIssues);
        List("strengths", r.StrengthsOfPreparation, true);
        List("weaknesses", r.PreparationWeaknesses, true);
        List("followup", r.FollowUpPreparation, true);
        List("thankyou", r.ThankYouThemes, true);
        List("unused", r.UnusedRelevantStories);
        sb.Append($"<p class=\"meta\">{E(productName)} · {E(r.SessionId)}</p></body></html>");
        return sb.ToString();
    }

    /// <summary>Plain-text version (copy to e-mail, notes, a chat assistant). Same content and honesty note as the HTML.</summary>
    public static string RenderText(InterviewReport r, string productName = "Interview Assistant")
    {
        string L(string k) => ReportLabels.Get(r.ReportLanguage, k);
        var sb = new StringBuilder();
        sb.AppendLine(L("title"));
        sb.AppendLine($"{Header(r.ProfileLabel, r.TargetLabel)} · {r.StartedUtc:yyyy-MM-dd HH:mm} UTC · {L("duration")}: {(int)(r.EndedUtc - r.StartedUtc).TotalMinutes} min");
        if (!r.CandidateMicrophoneEnabled) { sb.AppendLine(); sb.AppendLine(L("honesty")); }
        sb.AppendLine(); sb.AppendLine($"{L("questions")} ({r.Questions.Count})");
        foreach (var q in r.Questions)
        {
            sb.AppendLine(); sb.AppendLine($"Q{q.Index}. {q.OriginalWording}");
            sb.AppendLine($"   {L("category")}: {q.Category.ToLowerInvariant().Replace('_', ' ')} · {L("mode")}: {q.Mode.ToLowerInvariant()}{(q.Typed ? " · " + L("typed") : "")}");
            if (q.Context.Length > 0) sb.AppendLine($"   {L("context")}: {q.Context}");
            foreach (var s in q.SuggestionsShown) sb.AppendLine("   - " + s);
        }
        if (r.UncertainQuestions.Count > 0)
        {
            sb.AppendLine(); sb.AppendLine($"{L("uncertain")} ({r.UncertainQuestions.Count})"); sb.AppendLine(L("uncertain_note"));
            foreach (var q in r.UncertainQuestions) { sb.AppendLine(); sb.AppendLine($"Q{q.Index}. {q.OriginalWording}"); sb.AppendLine($"   {q.UncertainReason}"); }
        }
        void List(string key, IEnumerable<string> items, bool inference = false)
        {
            var l = items.ToList();
            sb.AppendLine(); sb.AppendLine(L(key) + (inference ? $" ({L("inference")})" : ""));
            if (l.Count == 0) sb.AppendLine("   " + L("none")); else foreach (var i in l) sb.AppendLine("   - " + i);
        }
        List("difficulty", r.DifficultyAreas, true);
        List("gaps", r.GapQuestions);
        List("followup", r.FollowUpPreparation, true);
        List("thankyou", r.ThankYouThemes, true);
        sb.AppendLine(); sb.AppendLine($"{productName} · {r.SessionId}");
        return sb.ToString();
    }
}
