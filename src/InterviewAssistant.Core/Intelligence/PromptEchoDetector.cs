using System.Text.RegularExpressions;
using InterviewAssistant.Core.Audio;

namespace InterviewAssistant.Core.Intelligence;

public sealed record EchoResult(bool IsEcho, string Reason, double Coverage, int LongestRun);

/// <summary>
/// Speech-recognition models can "transcribe" their own bias prompt when they receive a non-speech sound (a beep, a
/// click): the result reads like "Job interview for a Product Owner role at Teroxx. Terms: XAB, MiCA, Jira…". No
/// interviewer speaks the prompt, so a transcript that reproduces its structure or a long run of its words is an echo.
/// Mentioning one or two of the terms ("What do you know about Teroxx?") is never an echo. Stronger acoustic speech
/// evidence raises the bar.
/// </summary>
public sealed class PromptEchoDetector
{
    private readonly string[] _prompt;
    private readonly HashSet<string> _promptSet;
    private readonly HashSet<string> _terms;
    private static readonly Regex Word = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);
    private static readonly Regex TermsMarker = new(@"\b(terms|vocabulary|keywords)\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public PromptEchoDetector(string transcriptionPrompt, IEnumerable<string>? terms = null)
    {
        _prompt = Tokens(transcriptionPrompt).Take(400).ToArray();
        _promptSet = new HashSet<string>(_prompt, StringComparer.Ordinal);
        _terms = new HashSet<string>((terms ?? Array.Empty<string>()).Select(Norm).Where(t => t.Length > 0), StringComparer.Ordinal);
        foreach (var t in Tokens("job interview professional business technology vocabulary")) _promptSet.Add(t);
    }

    public static PromptEchoDetector Generic => new("Job interview. Professional business and technology vocabulary.");

    public EchoResult Check(string transcript, SpeechEvidence evidence = default)
    {
        var words = Tokens(transcript).Take(240).ToArray();
        if (words.Length == 0) return new(false, "", 0, 0);
        double coverage = words.Count(_promptSet.Contains) / (double)words.Length;
        int run = LongestCommonRun(words, _prompt);
        var items = transcript.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(Norm).Where(i => i.Length > 0).ToList();
        int termItems = items.Count(i => _terms.Contains(i) || _terms.Contains(Norm(Regex.Replace(i, @"^(terms|vocabulary|keywords)\s*", "", RegexOptions.IgnoreCase))));
        bool marker = TermsMarker.IsMatch(transcript);
        bool startsLikePrompt = words.Length >= 2 && words[0] == "job" && words[1] == "interview";
        bool listLike = items.Count >= 4 && termItems >= 3;
        // Long, clearly voiced speech: only an unmistakable reproduction of the prompt counts.
        bool strongSpeech = evidence.HasSpeech && evidence.VoicedMs >= 1500;

        string reason = "";
        if (marker && (termItems >= 2 || coverage >= 0.6)) reason = "terms list";
        else if (listLike && coverage >= 0.7) reason = "vocabulary list";
        else if (startsLikePrompt && coverage >= 0.8) reason = "prompt opening";
        else if (run >= 5 && run >= words.Length * 0.7 && coverage >= 0.8) reason = "prompt wording";
        if (reason.Length > 0 && strongSpeech && !(marker && termItems >= 3) && !(run >= 8 && run >= words.Length * 0.9))
            reason = "";
        return new(reason.Length > 0, reason, Math.Round(coverage, 2), run);
    }

    private static IEnumerable<string> Tokens(string s) => Word.Matches(s.ToLowerInvariant()).Select(m => m.Value);
    private static string Norm(string s) => string.Join(' ', Tokens(s));

    private static int LongestCommonRun(string[] a, string[] b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        int best = 0;
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                cur[j] = a[i - 1] == b[j - 1] ? prev[j - 1] + 1 : 0;
                if (cur[j] > best) best = cur[j];
            }
            (prev, cur) = (cur, prev);
        }
        return best;
    }
}
