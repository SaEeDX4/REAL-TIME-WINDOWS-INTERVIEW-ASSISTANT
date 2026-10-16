namespace InterviewAssistant.Core.Intelligence;

/// <summary>
/// Grounding check for answers to personal questions (hobbies, motivation, team preference…). The candidate confirmed
/// facts such as "Hiking and photography"; an answer may rephrase them ("In my free time I enjoy hiking and
/// photography") but must not add traits, feelings, benefits, frequencies, places or links the candidate never stated
/// ("it keeps me grounded", "it taught me patience", "every weekend"). A bullet passes only when every content word comes
/// from the facts, the question, or a small set of neutral framing words.
/// English only: other answer languages rely on the prompt rule (their wording cannot be compared word for word).
/// </summary>
public static class PersonalGrounding
{
    // Neutral framing that states a fact without adding to it. "enjoy"/"like" are the minimal verbs for a pastime.
    private static readonly HashSet<string> Framing = new(TextNormalizer.Tokenize(
        "enjoy like spend free time outside work hobby hobbies interest interests personally mostly mainly both thing things " +
        "prefer preferred best ideal motivate motivates drive drives team teams style way approach most lot currently answer " +
        "am not no but all its these those being get go take out up here other more such each own same too only well one"), StringComparer.Ordinal);

    /// <summary>Content words of <paramref name="bullet"/> that none of the facts, the question or neutral framing supports.</summary>
    public static IReadOnlyList<string> Unsupported(string bullet, IEnumerable<string> facts, string question)
    {
        var allowed = new HashSet<string>(Framing, StringComparer.Ordinal);
        foreach (var f in facts) allowed.UnionWith(TextNormalizer.Tokenize(f));
        allowed.UnionWith(TextNormalizer.Tokenize(question));
        return TextNormalizer.Tokenize(bullet).Where(t => !Supported(t, allowed)).Distinct().ToList();
    }

    public static bool IsGrounded(string bullet, IEnumerable<string> facts, string question) => Unsupported(bullet, facts, question).Count == 0;

    // Light stems differ across word forms (hike/hiking → "hike"/"hik", photos/photography): a shared prefix of at least
    // four letters (or the whole shorter stem) counts as the same word.
    private static bool Supported(string token, HashSet<string> allowed)
    {
        if (allowed.Contains(token)) return true;
        foreach (var a in allowed)
        {
            var (s, l) = a.Length <= token.Length ? (a, token) : (token, a);
            if (s.Length >= 3 && l.StartsWith(s, StringComparison.Ordinal) && (s.Length >= 4 || l.Length - s.Length <= 1)) return true;
        }
        return false;
    }
}
