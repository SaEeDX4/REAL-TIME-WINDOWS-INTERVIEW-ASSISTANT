using System.Text;
using System.Text.RegularExpressions;
using InterviewAssistant.Core.Knowledge;

namespace InterviewAssistant.Core.Intelligence;

/// <summary>Internal confidence in the answer we are about to show. LOW is never presented as certainty.</summary>
public enum AnswerConfidence { Low, Medium, High }

/// <summary>Personal / professional-identity topics answered only from the confirmed profile.</summary>
public enum PersonalTopic { None, Hobbies, Motivation, WorkStyle, Team, Strength, Weakness, WhyLooking, CareerGoals, Location, Remote, Availability, Leadership, Communication }

/// <summary>
/// Structured decision metadata for one answer (no chain-of-thought): what is being asked, what evidence may be used,
/// what structure fits, which risks apply and how confident we are. Built locally in microseconds — the normal
/// answer path never adds a model round-trip.
/// </summary>
public sealed record AnswerPlan(
    string QuestionType,
    string CoreIntent,
    bool Hypothetical,
    bool DirectExperience,
    bool KnownGap,
    IReadOnlyList<string> EvidenceStoryIds,
    IReadOnlyList<string> EvidenceCompanies,
    IReadOnlyList<string> Metrics,
    string Structure,
    IReadOnlyList<string> RiskFlags,
    AnswerConfidence Confidence,
    PersonalTopic Personal,
    IReadOnlyList<string> PersonalFacts)
{
    public bool PersonalWithoutData => Personal != PersonalTopic.None && PersonalFacts.Count == 0;

    /// <summary>Compact plan section for the prompt (instructions, not reasoning).</summary>
    public string ToPromptSection()
    {
        var sb = new StringBuilder("ANSWER PLAN: ");
        sb.Append($"intent={CoreIntent}; structure={Structure}; confidence={Confidence.ToString().ToUpperInvariant()}");
        if (EvidenceCompanies.Count > 0) sb.Append($"; relevant confirmed experience at: {string.Join(", ", EvidenceCompanies)}");
        if (Metrics.Count > 0) sb.Append($"; usable verified numbers: {string.Join(", ", Metrics.Take(6))}");
        sb.AppendLine(".");
        if (KnownGap)
            sb.AppendLine("The exact experience is NOT confirmed: start with a short honest bridge, then cite the closest CONFIRMED experience as past fact (it is true — keep it in past tense), then the approach as \"I'd…\".");
        else if (Hypothetical && !DirectExperience)
            sb.AppendLine("Hypothetical/approach question: answer confidently as \"I'd…\" / \"My approach would be…\" — no disclaimer, no invented history.");
        if (Confidence == AnswerConfidence.Low)
            sb.AppendLine("Confidence is LOW: use measured wording, stay general where evidence is thin, never invent specifics.");
        if (Personal != PersonalTopic.None)
            sb.AppendLine(PersonalFacts.Count > 0
                ? "PERSONAL ANSWER — use ONLY these confirmed facts (do not add others): " + string.Join(" | ", PersonalFacts) +
                  ". Rephrase them naturally in first person and stop. Do NOT add reasons, feelings, benefits, personality traits, skills it " +
                  "built, how often, where, since when, or a link to the role — none of that was confirmed. Short is correct here."
                : "PERSONAL ANSWER — the profile has NO confirmed facts on this: do not invent any (no hobbies, preferences or reasons).");
        return sb.ToString().TrimEnd();
    }
}

public static class AnswerPlanner
{
    private static readonly (PersonalTopic Topic, string[] Categories, Regex Pattern)[] PersonalRules =
    {
        (PersonalTopic.Hobbies, new[] { "hobby", "outside_work", "learning" }, new(@"\b(hobb(y|ies)|free time|spare time|outside (of )?work|weekends?|for fun|pastimes?|interests outside|do you (do|enjoy) (when|after|outside))\b", RegexOptions.IgnoreCase)),
        (PersonalTopic.Motivation, new[] { "motivation" }, new(@"\b(what motivates you|what drives you|motivat(es|ion) you|what gets you (up|excited)|what energi[sz]es you)\b", RegexOptions.IgnoreCase)),
        (PersonalTopic.Team, new[] { "team" }, new(@"\b(what (kind|type) of team|team (do you|you) (prefer|work best)|ideal team|work best (with|in))\b", RegexOptions.IgnoreCase)),
        (PersonalTopic.WorkStyle, new[] { "work_style" }, new(@"\b(work(ing)? style|how do you (like|prefer) to work|preferred way of working|work environment (do you|you) prefer)\b", RegexOptions.IgnoreCase)),
        (PersonalTopic.Strength, new[] { "strength" }, new(@"\b(greatest|biggest|main|key|top) strengths?\b|\bwhat are your strengths\b|\bwhat('s| is) your (greatest |biggest )?strength\b", RegexOptions.IgnoreCase)),
        (PersonalTopic.Weakness, new[] { "weakness" }, new(@"\bweakness(es)?\b|\barea(s)? (for|of) (improvement|development)\b", RegexOptions.IgnoreCase)),
        (PersonalTopic.WhyLooking, new[] { "why_looking" }, new(@"\b(why are you (looking|leaving|changing)|why (do|did) you (want to )?leave|why (now|move on)|reason for (leaving|looking|change))\b", RegexOptions.IgnoreCase)),
        (PersonalTopic.CareerGoals, new[] { "career_goal", "ideal_role" }, new(@"\b(career goals?|where do you see yourself|in (five|5|three|3|ten|10) years|long-?term goals?|next step in your career)\b", RegexOptions.IgnoreCase)),
        (PersonalTopic.Location, new[] { "location" }, new(@"\b(where are you (based|located|from)|which time ?zone)\b", RegexOptions.IgnoreCase)),
        (PersonalTopic.Remote, new[] { "remote" }, new(@"\b(remote|hybrid|relocat|on-?site|in the office)\b.*\?|\b(open to|willing to) relocate\b", RegexOptions.IgnoreCase)),
        (PersonalTopic.Availability, new[] { "availability" }, new(@"\b(notice period|when (can|could) you start|availability to start|earliest start)\b", RegexOptions.IgnoreCase)),
        (PersonalTopic.Leadership, new[] { "leadership_style" }, new(@"\b(leadership style|how (do|would) you (lead|manage) (a |your )?team|describe yourself as a (leader|manager))\b", RegexOptions.IgnoreCase)),
        (PersonalTopic.Communication, new[] { "communication_style" }, new(@"\b(communication style|how do you communicate)\b", RegexOptions.IgnoreCase)),
    };

    public static (PersonalTopic Topic, string[] Categories) DetectPersonal(string question)
    {
        foreach (var (topic, cats, rx) in PersonalRules) if (rx.IsMatch(question)) return (topic, cats);
        return (PersonalTopic.None, Array.Empty<string>());
    }

    public static AnswerPlan Plan(ExtractedQuestion q, Classification cls, MatchResult match, RetrievedContext ctx, KnowledgeBase kb, EvidenceIndex evidence)
    {
        var core = q.CoreQuestion;
        bool direct = QuestionClassifier.IsDirectExperienceQuestion(core);
        bool gap = cls.Mode == AnswerMode.Bridge;
        bool hypothetical = cls.Mode == AnswerMode.Hypothetical;
        var (personal, cats) = DetectPersonal(core);
        var personalFacts = personal == PersonalTopic.None ? new List<string>() : kb.Profile.Personal.Where(p => cats.Contains(p.Category)).Select(p => p.Text).ToList();

        var stories = ctx.Stories.ToList();
        var metrics = stories.SelectMany(s => s.VerifiedNumbers).Where(n => n.Length > 0).Distinct().Take(8).ToList();
        var companies = stories.Select(s => s.Company).Where(c => c.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();

        string structure = personal != PersonalTopic.None ? (personalFacts.Count > 0 ? "the confirmed facts, rephrased in first person — nothing added" : "the candidate's own words (no facts confirmed)")
            : gap ? "honest bridge → closest confirmed experience (past tense) → approach (I'd…)"
            : cls.Mode == AnswerMode.Verified ? (cls.Category == "BEHAVIOURAL" ? "situation → what I did → result" : "direct answer from confirmed experience → how → result")
            : q.SubQuestions.Count >= 2 ? "answer each part in order: " + string.Join(" / ", q.SubQuestions.Select(s => Short(s, 6)))
            : "direct answer → approach → how to measure";

        var risks = new List<string>();
        if (direct && gap) risks.Add("direct-experience-without-evidence");
        if (personal != PersonalTopic.None && personalFacts.Count == 0) risks.Add("personal-question-without-profile-data");
        if (q.Confidence < 0.7) risks.Add("unclear-ask");
        if (q.HasContext) risks.Add("long-context");
        if (cls.IsFollowUp) risks.Add("follow-up");
        if (kb.Profile.Boundaries.Count > 0) risks.Add("user-boundaries");

        AnswerConfidence confidence;
        if (match.Confidence == MatchConfidence.High) confidence = AnswerConfidence.High;
        else if (q.Confidence < 0.7 || (personal != PersonalTopic.None && personalFacts.Count == 0)) confidence = AnswerConfidence.Low;
        else if (gap) confidence = companies.Count > 0 ? AnswerConfidence.Medium : AnswerConfidence.Low;
        else if (cls.Mode == AnswerMode.Verified) confidence = stories.Count > 0 && evidence.Count > 0 ? AnswerConfidence.High : AnswerConfidence.Low;
        else confidence = q.Confidence >= 0.85 ? AnswerConfidence.High : AnswerConfidence.Medium;

        var intent = cls.Category.ToLowerInvariant().Replace('_', ' ') + (personal != PersonalTopic.None ? " / personal: " + personal.ToString().ToLowerInvariant() : "");
        return new AnswerPlan(cls.Category, intent, hypothetical, direct, gap, stories.Select(s => s.Id).ToList(), companies, metrics, structure, risks,
            confidence, personal, personalFacts);
    }

    private static string Short(string s, int words) { var w = s.Split(' ', StringSplitOptions.RemoveEmptyEntries); return w.Length <= words ? s.TrimEnd('?', '.') : string.Join(' ', w.Take(words)) + "…"; }

    /// <summary>
    /// Deterministic guidance when a personal question has no confirmed data: nothing is invented, the candidate
    /// answers from their own life. Shown instantly (no model call). Written in the answer language.
    /// </summary>
    public static IReadOnlyList<string> PersonalGuidance(PersonalTopic topic, string language)
    {
        var t = PersonalTexts.GetValueOrDefault(language) ?? PersonalTexts["en"];
        return new[] { t[0], topic == PersonalTopic.Hobbies ? t[1] : t[2], t[3] };
    }

    private static readonly Dictionary<string, string[]> PersonalTexts = new()
    {
        ["en"] = new[] { "Answer this one from your own life — your profile has no confirmed answer, so nothing is suggested for you.",
            "Keep it short: one real interest, what you enjoy about it, and one quality it reflects (curiosity, discipline, teamwork).",
            "Keep it short and genuine: one honest point, a brief reason, and how it connects to this role.",
            "Tip: add this to Profiles → Interview profile so future answers can use it." },
        ["es"] = new[] { "Responda desde su propia experiencia: su perfil no tiene una respuesta confirmada, así que no se sugiere nada inventado.",
            "Sea breve: un interés real, qué le gusta de él y una cualidad que refleja (curiosidad, disciplina, trabajo en equipo).",
            "Sea breve y auténtico: un punto honesto, una razón breve y su relación con este puesto.",
            "Consejo: añádalo en Perfiles → Perfil de entrevista para usarlo en futuras respuestas." },
        ["fr"] = new[] { "Répondez à partir de votre propre vie : votre profil ne contient aucune réponse confirmée, rien n'est donc inventé pour vous.",
            "Restez bref : un vrai centre d'intérêt, ce qui vous plaît, et une qualité qu'il reflète (curiosité, discipline, esprit d'équipe).",
            "Restez bref et sincère : un point honnête, une courte raison et le lien avec ce poste.",
            "Astuce : ajoutez-le dans Profils → Profil d'entretien pour les prochaines réponses." },
        ["de"] = new[] { "Beantworten Sie das aus Ihrem eigenen Leben – Ihr Profil enthält keine bestätigte Antwort, daher wird nichts erfunden.",
            "Kurz halten: ein echtes Interesse, was Ihnen daran gefällt, und eine Eigenschaft, die es zeigt (Neugier, Disziplin, Teamgeist).",
            "Kurz und ehrlich: ein ehrlicher Punkt, ein kurzer Grund und der Bezug zu dieser Rolle.",
            "Tipp: Ergänzen Sie dies unter Profile → Interviewprofil, damit künftige Antworten es nutzen können." },
        ["pt"] = new[] { "Responda com base na sua própria vida — o seu perfil não tem uma resposta confirmada, por isso nada é inventado.",
            "Seja breve: um interesse real, o que gosta nele e uma qualidade que ele mostra (curiosidade, disciplina, trabalho em equipa).",
            "Seja breve e autêntico: um ponto honesto, uma razão curta e a ligação com esta função.",
            "Dica: adicione isto em Perfis → Perfil de entrevista para respostas futuras." },
        ["it"] = new[] { "Rispondi partendo dalla tua vita — il tuo profilo non ha una risposta confermata, quindi non viene inventato nulla.",
            "Sii breve: un interesse reale, cosa ti piace e una qualità che rivela (curiosità, disciplina, lavoro di squadra).",
            "Sii breve e autentico: un punto onesto, un breve motivo e il collegamento con questo ruolo.",
            "Suggerimento: aggiungilo in Profili → Profilo colloquio per le risposte future." },
        ["ar"] = new[] { "أجب عن هذا من حياتك الخاصة — لا يحتوي ملفك على إجابة مؤكدة، لذلك لا يُقترح عليك أي شيء مختلق.",
            "اجعلها قصيرة: اهتمام حقيقي واحد، وما الذي تستمتع به فيه، وصفة يعكسها (الفضول، الانضباط، العمل الجماعي).",
            "اجعلها قصيرة وصادقة: نقطة صادقة واحدة، وسبب موجز، وصلتها بهذا الدور.",
            "نصيحة: أضف هذا في الملفات الشخصية ← ملف المقابلة لتستخدمه الإجابات القادمة." },
        ["fa"] = new[] { "به این سؤال از زندگی خودتان پاسخ دهید — پروفایل شما پاسخ تأییدشده‌ای ندارد، پس چیزی برایتان ساخته نمی‌شود.",
            "کوتاه بگویید: یک علاقهٔ واقعی، اینکه چه چیزی از آن لذت می‌برید، و یک ویژگی که نشان می‌دهد (کنجکاوی، نظم، کار تیمی).",
            "کوتاه و صادقانه: یک نکتهٔ صادقانه، یک دلیل کوتاه و ارتباط آن با این نقش.",
            "نکته: این را در پروفایل‌ها ← پروفایل مصاحبه اضافه کنید تا پاسخ‌های بعدی از آن استفاده کنند." },
        ["zh"] = new[] { "请根据您自己的生活回答——您的资料中没有已确认的答案，因此不会为您编造内容。",
            "简短作答：一个真实的兴趣、您喜欢它的原因，以及它体现的一种品质（好奇心、自律、团队合作）。",
            "简短而真诚：一个真实的要点、一个简短的理由，以及它与该职位的联系。",
            "提示：在“个人资料 → 面试资料”中补充此项，以便今后的回答使用。" },
        ["hi"] = new[] { "इसका जवाब अपने जीवन से दें — आपकी प्रोफ़ाइल में कोई पुष्टि किया गया उत्तर नहीं है, इसलिए आपके लिए कुछ भी गढ़ा नहीं जाता।",
            "संक्षेप में कहें: एक असली रुचि, आपको उसमें क्या पसंद है, और वह कौन-सा गुण दिखाती है (जिज्ञासा, अनुशासन, टीमवर्क)।",
            "संक्षिप्त और सच्चा रखें: एक ईमानदार बात, एक छोटा कारण, और इस भूमिका से उसका संबंध।",
            "सुझाव: इसे प्रोफ़ाइल → इंटरव्यू प्रोफ़ाइल में जोड़ें ताकि आगे के उत्तर इसका उपयोग कर सकें।" },
    };
}

public sealed record QualityReport(IReadOnlyList<string> Flags, double Relevance, int Words);

/// <summary>
/// Fast, deterministic answer checks (no model call): redundant bullets are dropped while streaming; relevance,
/// generic filler, length, repetition of a recent answer and ignored confirmed evidence are flagged for diagnostics
/// and the report. Unsupported claims are handled by <see cref="FactValidator"/>.
/// </summary>
public static class AnswerQualityGate
{
    private static readonly Regex Generic = new(@"\b(it depends|communication is key|at the end of the day|in today's fast-paced|think outside the box|synergy|best practices in general|i'm a team player|i am a team player|hard-?working and passionate)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True when the new bullet mostly repeats an earlier bullet of the same answer.</summary>
    public static bool IsRedundant(string bullet, IEnumerable<string> earlier)
    {
        var a = new HashSet<string>(TextNormalizer.Tokenize(bullet));
        if (a.Count < 3) return false;
        return earlier.Any(e => TextNormalizer.Jaccard(a, new HashSet<string>(TextNormalizer.Tokenize(e))) >= 0.6);
    }

    public static QualityReport Assess(ExtractedQuestion q, AnswerPlan plan, IReadOnlyList<string> bullets, IEnumerable<IReadOnlyList<string>> recentAnswers, AnswerStyle style)
    {
        var flags = new List<string>();
        if (bullets.Count == 0) return new QualityReport(flags, 0, 0);
        var answerTokens = new HashSet<string>(bullets.SelectMany(TextNormalizer.Tokenize));
        var questionTokens = new HashSet<string>(TextNormalizer.Tokenize(q.CoreQuestion + " " + string.Join(" ", q.Constraints)));
        double relevance = questionTokens.Count == 0 ? 1 : questionTokens.Count(answerTokens.Contains) / (double)questionTokens.Count;
        if (questionTokens.Count >= 3 && relevance == 0) flags.Add("QUALITY_OFF_TOPIC: answer shares no content words with the question");
        if (bullets.Any(b => Generic.IsMatch(b))) flags.Add("QUALITY_GENERIC: filler phrase");
        int words = bullets.Sum(TextNormalizer.WordCount);
        if (style == AnswerStyle.Balanced && words > 85) flags.Add($"QUALITY_TOO_LONG: {words} words");
        foreach (var prev in recentAnswers)
        {
            var p = new HashSet<string>(prev.SelectMany(TextNormalizer.Tokenize));
            if (p.Count >= 8 && TextNormalizer.Jaccard(p, answerTokens) >= 0.55) { flags.Add("QUALITY_REPEATS_RECENT: very similar to a recent answer"); break; }
        }
        if (plan.EvidenceCompanies.Count > 0 && (plan.KnownGap || plan.DirectExperience || plan.QuestionType == "BEHAVIOURAL") &&
            !plan.EvidenceCompanies.Any(c => bullets.Any(b => b.Contains(c.Split(' ')[0], StringComparison.OrdinalIgnoreCase))))
            flags.Add("QUALITY_IGNORED_EVIDENCE: confirmed experience available but not used");
        return new QualityReport(flags, Math.Round(relevance, 2), words);
    }
}
