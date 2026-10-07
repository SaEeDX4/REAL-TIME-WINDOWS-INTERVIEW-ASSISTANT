namespace InterviewAssistant.Core.Intelligence;

/// <summary>Live answer variants. Balanced is the default live format (3 speakable bullets).</summary>
public enum AnswerStyle { Balanced, VeryShort, Technical, Full, Example, Concise, Rapid }

public static class AnswerStyleSpec
{
    public static (string BulletCount, string WordsPerBullet, string TotalWords, string Override, int MaxTokens) For(AnswerStyle style) => style switch
    {
        AnswerStyle.Concise => ("3", "8-15", "25-45", "The interviewer is moving fast: keep each bullet short and put the direct answer first.", 150),
        AnswerStyle.Rapid => ("2", "6-12", "15-25", "The interviewer keeps interrupting: bullet 1 must directly answer the question; no background or examples.", 100),
        AnswerStyle.VeryShort => ("2-3", "8-16", "25-45", "Make it as short as possible while still complete.", 140),
        AnswerStyle.Technical => ("3-4", "12-24", "50-90", "Be more technical and precise: name mechanisms (states, idempotency keys, invariants, controls), still speakable.", 260),
        AnswerStyle.Example => ("3", "12-22", "40-80", "Ground the answer in ONE concrete example: a verified story from CANDIDATE EVIDENCE if relevant, otherwise a concrete scenario for this role/company phrased as what they WOULD do.", 220),
        AnswerStyle.Full => ("0", "n/a", "90-130", "OVERRIDE: instead of bullets, write ONE compact natural spoken paragraph after the MODE line (no bullet characters).", 320),
        _ => ("3 (4 only if truly necessary)", "10-22", "35-75", "", 200),
    };
}
