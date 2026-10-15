using InterviewAssistant.Core.Diagnostics;
using InterviewAssistant.Core.Intelligence;
using InterviewAssistant.Core.Live;
using InterviewAssistant.Core.Orchestration;
using Xunit;

namespace InterviewAssistant.Tests;

public class LiveIntelligenceTests
{
    private static (InterviewEngine E, FakeProvider P, ManualClock C) Create(bool provider = true)
    {
        var c = new ManualClock();
        var p = new FakeProvider();
        var e = new InterviewEngine(TestKnowledge.Load(), null, provider ? p : null, new EngineOptions { AutoTick = false }, c);
        return (e, p, c);
    }

    // ---------------- Coach Mode ----------------

    [Fact]
    public async Task CoachModeFromCacheIsImmediateAndHonoursContract()
    {
        var (e, p, _) = Create();
        e.Presentation = Presentation.Coach;
        await e.SubmitManualQuestionAsync("How would you prioritize improvements to the XAB rewards ecosystem?");
        var a = e.Current!;
        Assert.NotNull(a.Coach);
        Assert.True(a.Coach!.IsValid);
        Assert.Equal(3, a.Coach.Keywords.Count);
        Assert.Contains("→", a.Coach.Structure);
        Assert.Equal(2, a.Bullets.Count);              // keyword line + structure line only
        Assert.Equal(0, p.Requests);                    // no network
        Assert.InRange(e.Metrics.Samples.Last().FinalizedToFirstBulletMs, 0, 5);
    }

    [Fact]
    public async Task CoachModeViaAiParsesContractAndFallsBackWhenMalformed()
    {
        var (e, p, _) = Create();
        e.Presentation = Presentation.Coach;
        p.Responder = _ => new[] { "KEYWORDS: PRICING · BUNDLE · RETENTION\n", "STRUCTURE: Direct answer -> pricing logic -> measure\n", "REMINDER: keep it under 20 seconds" };
        await e.SubmitManualQuestionAsync("How would you price a premium tier bundled with partner benefits?");
        Assert.Equal(new[] { "PRICING", "BUNDLE", "RETENTION" }, e.Current!.Coach!.Keywords);
        Assert.Equal("Direct answer → pricing logic → measure", e.Current.Coach.Structure);
        Assert.Equal("keep it under 20 seconds", e.Current.Coach.Reminder);
        Assert.Contains("KEYWORDS", p.Calls[0][0].Content);

        p.Responder = _ => new[] { "Sure! Here are some ideas about pricing." };
        await e.SubmitManualQuestionAsync("How would you design onboarding for enterprise customers?");
        Assert.True(e.Current!.Coach!.IsValid);
        Assert.Contains(e.Current.ValidationFlags, f => f.StartsWith("COACH_FORMAT"));
    }

    [Fact]
    public void CoachToggleSwitchesModes()
    {
        var (e, _, _) = Create();
        Assert.Equal(Presentation.Coach, e.TogglePresentation());
        Assert.Equal(Presentation.Answer, e.TogglePresentation());
    }

    [Theory]
    [InlineData("KEYWORDS: کاربرد · ماندگاری مشتری · شاخص‌ها\nSTRUCTURE: پاسخ مستقیم → رویکرد → اندازه‌گیری", "کاربرد")]
    [InlineData("KEYWORDS: utilité · rétention · métriques\nSTRUCTURE: réponse directe → approche → mesure", "UTILITÉ")]
    [InlineData("KEYWORDS: 效用 · 留存 · 指标\nSTRUCTURE: 直接回答 → 方法 → 衡量", "效用")]
    public void CoachParserWorksForAnyScript(string text, string firstKeyword)
    {
        var c = CoachOutput.Parse(text)!;
        Assert.True(c.IsValid);
        Assert.Equal(firstKeyword, c.Keywords[0]);
    }

    // ---------------- Memory, follow-ups, repetition ----------------

    [Fact]
    public async Task FollowUpsAreLinkedAndMemoryReachesThePrompt()
    {
        var (e, p, _) = Create();
        await e.SubmitManualQuestionAsync("Tell me about your crypto experience.");
        await e.SubmitManualQuestionAsync("Why?");
        var q2 = e.Memory.Questions[1];
        Assert.Equal(1, q2.FollowUpOf);
        var prompt = p.Calls.Last()[1].Content;
        Assert.Contains("INTERVIEW MEMORY", prompt);
        Assert.Contains("crypto experience", prompt);
        Assert.Contains("FOLLOW-UP", prompt);
    }

    [Fact]
    public async Task RepeatedStoryIsAvoidedAndAlreadyMentionedIsRecognised()
    {
        var (e, p, _) = Create();
        await e.SubmitManualQuestionAsync("How did you grow Arzif to 60,000 active traders?");
        Assert.Equal(AnswerSource.Cache, e.Current!.Source);
        Assert.NotEmpty(e.Memory.StoryUse);
        // Same stories again → engine prefers a fresh AI answer and tells the model to avoid them.
        await e.SubmitManualQuestionAsync("Tell me about your crypto experience.");
        Assert.Equal(AnswerSource.Llm, e.Current!.Source);
        Assert.Contains("Avoid re-using the stories", p.Calls.Last()[1].Content);
        Assert.Contains("Stories already used", p.Calls.Last()[1].Content);
        await e.SubmitManualQuestionAsync("You already mentioned the 60,000 traders — give me another example of growth.");
        Assert.True(e.Memory.Last!.ReferencesEarlierAnswer);
        Assert.Contains("do NOT repeat it", p.Calls.Last()[1].Content);
        Assert.Contains("Metrics already mentioned", p.Calls.Last()[1].Content);
    }

    [Fact]
    public void StoryPenaltyRanksLeastUsedFirst()
    {
        var m = new SessionMemory();
        m.Add(new QuestionRecord { Original = "q1", StoryIds = { "a" } });
        m.Add(new QuestionRecord { Original = "q2", StoryIds = { "a" } });
        m.Add(new QuestionRecord { Original = "q3", StoryIds = { "b" } });
        Assert.True(m.StoryPenalty("a") > m.StoryPenalty("b"));
        Assert.Equal(0, m.StoryPenalty("c"));
        Assert.Contains("a", m.RecentlyUsedStories());
    }

    [Fact]
    public void MemoryStaysBoundedOverLongInterviewAndSerializes()
    {
        var m = new SessionMemory();
        for (int i = 0; i < 1000; i++) m.Add(new QuestionRecord { Original = $"Question number {i} about topic {i % 7}", Category = "CAT" + (i % 5), AnswerBullets = { new string('x', 200), "b", "c" } });
        Assert.True(m.Questions.Count <= m.MaxRecords);
        Assert.True(m.Questions.Count(q => !q.Truncated) <= m.MaxDetailedRecords);
        Assert.Contains("earlier questions", m.RollingSummary);
        var ctx = m.BuildPromptContext();
        Assert.True(ctx.Length < 3000, $"prompt memory {ctx.Length} chars");
        var back = SessionMemory.FromJson(m.ToJson());
        Assert.Equal(m.Questions.Count, back.Questions.Count);
        Assert.Equal(m.RollingSummary, back.RollingSummary);
    }

    [Fact]
    public async Task MemorySurvivesRestoreAfterDisconnect()
    {
        var (e1, _, _) = Create();
        await e1.SubmitManualQuestionAsync("How did you grow Arzif to 60,000 active traders?");
        var json = e1.Memory.ToJson();
        var (e2, p2, _) = Create();
        e2.RestoreMemory(SessionMemory.FromJson(json));
        await e2.SubmitManualQuestionAsync("And what happened next?");
        Assert.Equal(2, e2.Memory.Last!.Index);
        Assert.Contains("60,000", p2.Calls.Last()[1].Content);
    }

    // ---------------- Interruption adaptation ----------------

    /// <summary>Asks a heard question and lets the answer appear (realistic VAD timing + speech audio).</summary>
    private static async Task AskAsync(InterviewSimulator s, string q)
    {
        s.Say(q);
        await s.RunAsync(q.Split(" ").Length * s.MsPerWord + 4500);   // beyond the 2.5 s continuation window
        await s.SettleAsync();
    }

    [Fact]
    public async Task InterruptionsShortenAnswersAndResetRestoresNormal()
    {
        // RC2: pace adapts to HEARD interviewer questions that cut in on a long answer (not to typed questions or beeps).
        var s = new InterviewSimulator();
        await s.StartAsync();
        var levels = new List<AdaptiveLevel>();
        s.E.AdaptiveChanged += levels.Add;
        await AskAsync(s, "How would you price a premium tier bundled with partner benefits?");
        await AskAsync(s, "How would you design onboarding for enterprise customers?");   // asked ~1 s after a ~20 s answer appeared
        Assert.Equal(AdaptiveLevel.Concise, s.E.Adaptive);
        Assert.Single(s.E.Memory.Interruptions);
        Assert.True(s.E.Memory.Interruptions[0].Confidence >= 0.7);
        Assert.Equal(AnswerStyle.Concise, s.E.Current!.Style);
        Assert.Contains("moving fast", s.P.Calls.Last()[0].Content);

        await AskAsync(s, "How would you measure partner channel profitability?");
        Assert.Equal(AdaptiveLevel.Rapid, s.E.Adaptive);
        await AskAsync(s, "How would you decide which partner benefits to retire first?");
        Assert.Equal(AnswerStyle.Rapid, s.E.Current!.Style);
        Assert.Contains("bullet 1 must directly answer", s.P.Calls.Last()[0].Content);

        s.E.ResetAdaptive();
        Assert.Equal(AdaptiveLevel.Normal, s.E.Adaptive);
        Assert.Equal(new[] { AdaptiveLevel.Concise, AdaptiveLevel.Rapid, AdaptiveLevel.Normal }, levels);
    }

    [Fact]
    public async Task TypedQuestionsAndBareSpeechStartsNeverChangeThePace()
    {
        // RC1 bug: repeated manual tests (typed questions) and any speech_started (even a beep) switched to Concise/Rapid.
        var (e, _, c) = Create();
        for (int i = 0; i < 4; i++)
        {
            await e.SubmitManualQuestionAsync($"How would you price premium tier number {i} for partners?");
            c.Advance(800);
            e.OnInterviewerSpeechStarted();
        }
        Assert.Equal(AdaptiveLevel.Normal, e.Adaptive);
        Assert.Empty(e.Memory.Interruptions);
    }

    [Fact]
    public async Task BeepsAndVariantsNeverChangeThePace()
    {
        var s = new InterviewSimulator();
        await s.StartAsync();
        await AskAsync(s, "How would you price a premium tier bundled with partner benefits?");
        // A notification beep right after the answer appears, "transcribed" as the vocabulary prompt.
        s.Sound(InterviewSimulator.SoundKind.Beep, "Job interview for a Product Owner role at Teroxx. Terms: XAB, Abloxx, Jira, CRM, KPIs.");
        await s.RunAsync(2500); await s.SettleAsync();
        await s.E.RequestVariantAsync(s.E.Current!.Id, AnswerStyle.Technical);
        await s.E.RequestVariantAsync(s.E.Current!.Id, AnswerStyle.VeryShort);
        Assert.Equal(AdaptiveLevel.Normal, s.E.Adaptive);
        Assert.Empty(s.E.Memory.Interruptions);
        Assert.Single(s.E.Memory.Questions);
    }

    [Fact]
    public void CalmPaceIsNotAnInterruptionAndAdaptiveDecays()
    {
        var t = new InterruptionTracker();
        t.AnswerShown(0, 50, 1);
        Assert.Null(t.InterviewerSpeechStarted(25_000));   // waited long enough
        t.AnswerShown(30_000, 50, 2);
        Assert.NotNull(t.InterviewerSpeechStarted(31_000)); // fast follow
        Assert.Equal(AdaptiveLevel.Concise, t.Level);
        for (int i = 0; i < 3; i++) { t.AnswerShown(100_000 + i * 60_000, 40, 3 + i); t.InterviewerSpeechStarted(100_000 + i * 60_000 + 40_000); }
        Assert.Equal(AdaptiveLevel.Normal, t.Level);
    }

    [Fact]
    public async Task RapidModeTrimsCachedAnswersToTwoBullets()
    {
        var s = new InterviewSimulator(withProvider: false);
        await s.StartAsync();
        await AskAsync(s, "How would you increase XAB adoption?");
        await AskAsync(s, "How would you design VIP tier governance?");
        await AskAsync(s, "How would you prioritize improvements to the XAB rewards ecosystem?");
        Assert.Equal(AdaptiveLevel.Rapid, s.E.Adaptive);
        await AskAsync(s, "What would you do in your first 90 days?");
        Assert.Equal(2, s.E.Current!.Bullets.Count);
    }

    // ---------------- Multilingual ----------------

    [Fact]
    public async Task AnswersFollowTheInterviewerLanguageAndMemorySurvivesSwitches()
    {
        var (e, p, _) = Create();
        await e.SubmitManualQuestionAsync("How would you prioritize the product backlog?");
        Assert.Equal("en", e.Current!.AnswerLanguage);
        Assert.Equal(AnswerSource.Cache, e.Current.Source);
        await e.SubmitManualQuestionAsync("Und wie würden Sie das mit den Stakeholdern abstimmen?");
        Assert.Equal("de", e.Current!.DetectedLanguage);
        Assert.Equal("de", e.Current.AnswerLanguage);
        Assert.Equal(AnswerSource.Llm, e.Current.Source);     // English prepared answers are never shown for a German question
        var call = p.Calls.Last();
        Assert.Contains("German", call[0].Content);
        Assert.Contains("prioritize the product backlog", call[1].Content); // English context retained
        Assert.Equal(2, e.Memory.Questions.Count);
    }

    [Fact]
    public async Task CrossLanguageAnswerOverride()
    {
        var (e, p, _) = Create();
        e.AnswerLanguageOverride = "fa";
        await e.SubmitManualQuestionAsync("How would you prioritize the product backlog?");
        Assert.Equal("en", e.Current!.DetectedLanguage);
        Assert.Equal("fa", e.Current.AnswerLanguage);
        Assert.Equal(AnswerSource.Llm, e.Current.Source);
        Assert.Contains("Persian", p.Calls.Last()[0].Content);
    }

    [Theory]
    [InlineData("Usted ya mencionó ese ejemplo, ¿tiene otro?")]
    [InlineData("Vous avez déjà mentionné cela.")]
    [InlineData("Sie haben bereits erwähnt, dass Sie das Team geführt haben.")]
    [InlineData("你已经提到过这个例子了")]
    [InlineData("قبلاً گفتید که تیم را رهبری کردید")]
    public void AlreadyMentionedDetectedAcrossLanguages(string q) => Assert.True(SessionMemory.ReferencesEarlier(q));
}
