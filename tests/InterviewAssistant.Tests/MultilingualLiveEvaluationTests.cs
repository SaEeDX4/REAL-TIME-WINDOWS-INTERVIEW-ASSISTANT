using InterviewAssistant.Core.Intelligence;
using InterviewAssistant.Core.Knowledge;
using InterviewAssistant.Core.Languages;
using InterviewAssistant.Core.Live;
using InterviewAssistant.Core.Orchestration;
using InterviewAssistant.Core.Providers;
using Xunit;
using Xunit.Abstractions;

namespace InterviewAssistant.Tests;

/// <summary>
/// LIVE multilingual answer-quality evaluation (runs only with OPENAI_API_KEY): 10 languages × 7 question types
/// (behavioural, technical, product-owner, résumé-based, hypothetical, follow-up, gap/bridge). Automated checks:
/// output language matches, 2–4 bullets, no unverified numbers/unsupported claims, Bridge on gap questions, Coach
/// contract in every language. Naturalness/tone require human native review — recorded in docs/v2/MULTILINGUAL_VALIDATION.md.
/// </summary>
public class MultilingualLiveEvaluationTests
{
    private readonly ITestOutputHelper _out;
    public MultilingualLiveEvaluationTests(ITestOutputHelper o) => _out = o;

    public static readonly Dictionary<string, string[]> Questions = new()
    {
        ["en"] = new[] { "Tell me about a time you led a team through a difficult change.", "How would you design an API rate limit?", "How do you prioritise a product backlog?", "Tell me about your experience at Arzif.", "How would you increase adoption of a new loyalty token?", "Why?", "Have you personally built a financial ledger?" },
        ["es"] = new[] { "Cuénteme una situación en la que lideró a un equipo en un cambio difícil.", "¿Cómo diseñaría un límite de uso para una API?", "¿Cómo prioriza el backlog de un producto?", "Hábleme de su experiencia en Arzif.", "¿Cómo aumentaría la adopción de un nuevo token de fidelidad?", "¿Por qué?", "¿Ha construido usted personalmente un libro contable financiero?" },
        ["fr"] = new[] { "Parlez-moi d'une fois où vous avez mené une équipe à travers un changement difficile.", "Comment concevriez-vous une limite de débit pour une API ?", "Comment priorisez-vous un backlog produit ?", "Parlez-moi de votre expérience chez Arzif.", "Comment augmenteriez-vous l'adoption d'un nouveau jeton de fidélité ?", "Pourquoi ?", "Avez-vous personnellement construit un registre financier ?" },
        ["de"] = new[] { "Erzählen Sie von einer Situation, in der Sie ein Team durch eine schwierige Veränderung geführt haben.", "Wie würden Sie ein Rate-Limit für eine API entwerfen?", "Wie priorisieren Sie ein Produkt-Backlog?", "Erzählen Sie von Ihrer Erfahrung bei Arzif.", "Wie würden Sie die Nutzung eines neuen Loyalty-Tokens steigern?", "Warum?", "Haben Sie persönlich ein Finanz-Ledger aufgebaut?" },
        ["pt"] = new[] { "Conte-me sobre uma vez em que você liderou uma equipe em uma mudança difícil.", "Como você projetaria um limite de taxa para uma API?", "Como você prioriza um backlog de produto?", "Fale sobre sua experiência na Arzif.", "Como você aumentaria a adoção de um novo token de fidelidade?", "Por quê?", "Você já construiu pessoalmente um razão financeiro?" },
        ["it"] = new[] { "Mi parli di una volta in cui ha guidato un team in un cambiamento difficile.", "Come progetterebbe un limite di frequenza per un'API?", "Come assegna le priorità a un backlog di prodotto?", "Mi parli della sua esperienza in Arzif.", "Come aumenterebbe l'adozione di un nuovo token fedeltà?", "Perché?", "Ha mai costruito personalmente un registro contabile finanziario?" },
        ["ar"] = new[] { "حدثني عن مرة قدت فيها فريقًا خلال تغيير صعب.", "كيف ستصمم حدًا لمعدل الطلبات في واجهة API؟", "كيف تحدد أولويات قائمة مهام المنتج؟", "حدثني عن خبرتك في Arzif.", "كيف ستزيد اعتماد رمز ولاء جديد؟", "لماذا؟", "هل قمت شخصيًا ببناء دفتر أستاذ مالي؟" },
        ["fa"] = new[] { "درباره زمانی بگویید که تیمی را در یک تغییر دشوار رهبری کردید.", "چطور برای یک API محدودیت نرخ طراحی می‌کنید؟", "چطور بک‌لاگ محصول را اولویت‌بندی می‌کنید؟", "درباره تجربه‌تان در Arzif بگویید.", "چطور پذیرش یک توکن وفاداری جدید را افزایش می‌دهید؟", "چرا؟", "آیا شخصاً یک دفتر کل مالی ساخته‌اید؟" },
        ["zh"] = new[] { "请讲一次你带领团队度过艰难变革的经历。", "你会如何设计API的限流？", "你如何确定产品待办事项的优先级？", "请谈谈你在Arzif的经历。", "你会如何提高新忠诚度代币的使用率？", "为什么？", "你亲自搭建过财务账本系统吗？" },
        ["hi"] = new[] { "मुझे उस समय के बारे में बताइए जब आपने किसी कठिन बदलाव में टीम का नेतृत्व किया।", "आप किसी API के लिए रेट लिमिट कैसे डिज़ाइन करेंगे?", "आप प्रोडक्ट बैकलॉग को प्राथमिकता कैसे देते हैं?", "Arzif में अपने अनुभव के बारे में बताइए।", "आप एक नए लॉयल्टी टोकन को अपनाने की दर कैसे बढ़ाएंगे?", "क्यों?", "क्या आपने व्यक्तिगत रूप से कोई वित्तीय लेजर बनाया है?" },
    };

    [Fact]
    public void FixtureCoversTenLanguagesAndDetectsEachQuestionLanguage()
    {
        Assert.Equal(10, Questions.Count);
        foreach (var (lang, qs) in Questions)
        {
            Assert.Equal(7, qs.Length);
            // Full questions (not the 1-word follow-up) must be detected as their language.
            foreach (var q in qs.Where(q => q.Length > 20)) Assert.True(lang == LanguageDetector.Detect(q).Code, $"{lang}: \"{q}\" detected as {LanguageDetector.Detect(q)}");
        }
    }

    [Fact]
    public async Task LiveAnswersInAllTenLanguages()
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) { _out.WriteLine("SKIPPED: OPENAI_API_KEY not set — multilingual live quality NOT YET VALIDATED"); return; }
        using var provider = new OpenAiChatAnswerProvider(() => key, new ChatProviderOptions { Model = Environment.GetEnvironmentVariable("IA_ANSWER_MODEL") ?? "gpt-5.4-mini" });
        var kb = TestKnowledge.Load();
        var validator = new FactValidator(kb.Profile, kb.Context);
        int problems = 0, total = 0;
        foreach (var (lang, qs) in Questions)
        {
            var engine = new InterviewEngine(kb, null, provider, new EngineOptions { AutoTick = false, UseFastCache = false });
            engine.AnswerLanguageOverride = lang; // short follow-ups like "Why?" are too short to detect reliably
            for (int i = 0; i < qs.Length; i++)
            {
                await engine.SubmitManualQuestionAsync(qs[i]);
                var a = engine.Current!; total++;
                var text = string.Join(" ", a.Bullets);
                var detected = LanguageDetector.Detect(text).Code;
                var r = validator.Validate(a.Bullets, a.Mode, AnswerStyle.Technical);
                bool ok = a.Bullets.Count is >= 2 and <= 4 && detected == lang && !r.HasFactualIssues && (i != 6 || a.Mode == AnswerMode.Bridge);
                if (!ok) problems++;
                _out.WriteLine($"[{lang}] {(ok ? "OK " : "BAD")} {a.Mode} lang={detected} first={engine.Metrics.Samples.Last().FinalizedToFirstBulletMs}ms :: {qs[i]}");
                foreach (var b in a.Bullets) _out.WriteLine("    • " + b);
            }
            engine.Presentation = Presentation.Coach;
            await engine.SubmitManualQuestionAsync(qs[2]);
            Assert.True(engine.Current!.Coach!.IsValid, $"coach contract failed for {lang}");
        }
        _out.WriteLine($"{total - problems}/{total} automated checks passed");
        Assert.True(problems <= total / 10, $"{problems}/{total} multilingual answers failed automated checks");
    }
}
