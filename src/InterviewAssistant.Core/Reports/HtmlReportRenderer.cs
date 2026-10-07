using System.Net;
using System.Text;
using InterviewAssistant.Core.Languages;

namespace InterviewAssistant.Core.Reports;

/// <summary>Self-contained, printable HTML (print → PDF via the OS). All user/interviewer content is HTML-encoded.</summary>
public static class HtmlReportRenderer
{
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
        sb.Append(".q{border:1px solid #e3e6ea;border-radius:8px;padding:10px 14px;margin:8px 0}.q .t{font-weight:600}.tag{display:inline-block;font-size:11px;background:#eef2f7;border-radius:4px;padding:1px 6px;margin-inline-end:6px;color:#3d4652}");
        sb.Append(".inf{font-size:11px;color:#8a5a00;margin-inline-start:6px}ul{margin:6px 0;padding-inline-start:22px}bdi{unicode-bidi:isolate}@media print{.q{break-inside:avoid}}</style></head><body>");
        sb.Append($"<h1>{E(L("title"))}</h1><div class=\"meta\"><bdi>{E(r.ProfileLabel)}</bdi> · <bdi>{E(r.TargetLabel)}</bdi> · {r.StartedUtc:yyyy-MM-dd HH:mm} UTC · {E(L("duration"))}: {(int)(r.EndedUtc - r.StartedUtc).TotalMinutes} min · {E(L("language"))}: {E(string.Join(", ", r.LanguagesUsed.Select(LanguageRegistry.EnglishName)))}</div>");
        if (!r.CandidateMicrophoneEnabled) sb.Append($"<div class=\"note\">{E(L("honesty"))}</div>");

        sb.Append($"<h2>{E(L("questions"))} ({r.Questions.Count})</h2>");
        foreach (var q in r.Questions)
        {
            sb.Append("<div class=\"q\">");
            sb.Append($"<div class=\"t\"><span class=\"tag\">Q{q.Index}</span><span class=\"tag\">{E(q.Category.ToLowerInvariant().Replace('_', ' '))}</span><span class=\"tag\">{E(q.Mode)}</span><span class=\"tag\">{E(q.DetectedLanguage)}</span>");
            sb.Append($"<bdi dir=\"auto\">{E(q.OriginalWording)}</bdi></div>");
            if (q.SuggestionsShown.Count > 0) { sb.Append("<ul>"); foreach (var s in q.SuggestionsShown) sb.Append($"<li><bdi dir=\"auto\">{E(s)}</bdi></li>"); sb.Append("</ul>"); }
            sb.Append("</div>");
        }

        void List(string key, IEnumerable<string> items, bool inference = false)
        {
            var l = items.ToList();
            sb.Append($"<h2>{E(L(key))}{(inference ? $"<span class=\"inf\">({E(L("inference"))})</span>" : "")}</h2>");
            if (l.Count == 0) { sb.Append($"<p class=\"meta\">{E(L("none"))}</p>"); return; }
            sb.Append("<ul>"); foreach (var i in l) sb.Append($"<li><bdi dir=\"auto\">{E(i)}</bdi></li>"); sb.Append("</ul>");
        }
        List("difficulty", r.DifficultyAreas, true);
        List("stories", r.StoriesRecommended.Select(s => $"{s.Label} ×{s.Count}"));
        List("metrics", r.MetricsRecommended.Select(m => $"{m.Metric} ×{m.Count}"));
        List("repeated", r.RepeatedTopics);
        List("notcovered", r.TopicsNotCovered, true);
        List("gaps", r.GapQuestions);
        List("interruptions", r.Interruptions.Select(i => $"after Q{i.AfterQuestionIndex}: {i.Note} (confidence {i.Confidence:0.00}, {i.ElapsedMs / 1000.0:0.0}s of ~{i.ExpectedMs / 1000.0:0.0}s)"), true);
        List("latency", r.LatencyP50Ms >= 0 ? new[] { $"P50 {r.LatencyP50Ms} ms · P95 {r.LatencyP95Ms} ms (question → first suggestion)" } : Array.Empty<string>());
        List("transcription", r.TranscriptionIssues);
        List("strengths", r.StrengthsOfPreparation, true);
        List("weaknesses", r.PreparationWeaknesses, true);
        List("followup", r.FollowUpPreparation, true);
        List("thankyou", r.ThankYouThemes, true);
        List("unused", r.UnusedRelevantStories);
        sb.Append($"<p class=\"meta\">{E(productName)} · {E(r.SessionId)}</p></body></html>");
        return sb.ToString();
    }
}
