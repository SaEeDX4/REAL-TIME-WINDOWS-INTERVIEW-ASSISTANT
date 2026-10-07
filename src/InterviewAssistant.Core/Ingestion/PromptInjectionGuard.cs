using System.Text.RegularExpressions;

namespace InterviewAssistant.Core.Ingestion;

/// <summary>
/// Uploaded documents are untrusted content. This guard removes instruction-like lines that try to steer the
/// model ("ignore previous instructions", "you are now…", "system prompt") in the supported languages and
/// records a flag. Defence in depth: prompts also wrap documents as quoted data and state they cannot change rules.
/// </summary>
public static class PromptInjectionGuard
{
    private static readonly Regex Pattern = new(
        @"(ignore|disregard|forget|override)\s+(all\s+|any\s+|the\s+)?(previous|prior|above|earlier|system)\s+(instructions|prompts?|rules)" +
        @"|\byou are now\b|\bact as (an?|the)\b.*\b(ai|assistant|model)\b|\bsystem prompt\b|\bdeveloper mode\b|\bjailbreak\b" +
        @"|reveal (your|the) (instructions|prompt|api key|secret)|\bnew instructions\s*:" +
        @"|ignora (las|todas las) instrucciones|ignorez (les|toutes les) instructions|ignoriere (alle|die) (vorherigen )?anweisungen" +
        @"|ignore (as|todas as) instruções|ignora (le|tutte le) istruzioni|دستورالعمل‌های قبلی را نادیده|تجاهل (جميع )?التعليمات|忽略(之前|以上)的?指令|पिछले निर्देशों को अनदेखा",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public sealed record Result(string Text, List<string> Flags);

    public static Result Sanitize(string text)
    {
        var flags = new List<string>();
        var kept = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            if (Pattern.IsMatch(line)) { flags.Add("prompt-injection-removed: " + (line.Length > 80 ? line[..80] + "…" : line).Trim()); continue; }
            kept.Add(line);
        }
        return new Result(string.Join("\n", kept), flags);
    }

    public static bool LooksMalicious(string text) => Pattern.IsMatch(text);
}
