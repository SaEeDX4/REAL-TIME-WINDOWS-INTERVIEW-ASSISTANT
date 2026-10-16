using InterviewAssistant.Core.Intelligence;
using InterviewAssistant.Core.Orchestration;
using Xunit;

namespace InterviewAssistant.Tests;

/// <summary>
/// RC4: a personal answer says what the candidate confirmed and nothing more. With the hobby "Hiking and photography"
/// the answer may say "In my free time I enjoy hiking and photography" — never that it keeps them grounded, taught them
/// patience, happens every weekend or makes them a better product owner.
/// </summary>
public class PersonalGroundingTests
{
    private static readonly string[] Hobby = { "Hiking and photography" };
    private const string Q = "What are your hobbies?";

    [Theory]
    [InlineData("In my free time I enjoy hiking and photography.")]
    [InlineData("I enjoy hiking and photography.")]
    [InlineData("Hiking and photography.")]
    [InlineData("Outside work, I like to hike and take photos.")]
    [InlineData("My hobbies are hiking and photography.")]
    public void Rephrasing_TheConfirmedFact_IsGrounded(string bullet) =>
        Assert.True(PersonalGrounding.IsGrounded(bullet, Hobby, Q), string.Join(", ", PersonalGrounding.Unsupported(bullet, Hobby, Q)));

    [Theory]
    [InlineData("Hiking keeps me grounded and helps me recharge.")]
    [InlineData("Photography taught me patience and an eye for detail.")]
    [InlineData("I go hiking every weekend in the mountains.")]
    [InlineData("It reflects my curiosity, which I bring to product work.")]
    [InlineData("I'm passionate about capturing landscapes.")]
    [InlineData("Being outdoors gives me energy and a fresh perspective.")]
    public void AddedTraitsFeelingsBenefitsOrDetails_AreNotGrounded(string bullet) =>
        Assert.False(PersonalGrounding.IsGrounded(bullet, Hobby, Q));

    [Fact]
    public void Motivation_MayBeRephrased_NotEmbellished()
    {
        var facts = new[] { "Building products people use every day" };
        const string q = "What motivates you?";
        Assert.True(PersonalGrounding.IsGrounded("What motivates me is building products people use every day.", facts, q));
        Assert.False(PersonalGrounding.IsGrounded("I find it deeply rewarding to see my impact on millions of users.", facts, q));
    }

    [Fact]
    public async Task Engine_DropsInventedPersonalBullets_AndFallsBackToTheConfirmedWords()
    {
        var s = new InterviewSimulator(Rc2EngineAcceptanceTests.WithPersonal(("hobby", "Hiking and photography")),
            options: new EngineOptions { AutoTick = false, UseFastCache = false });
        s.P.Responder = _ => new[] { "MODE: VERIFIED\n", "• Hiking keeps me grounded and helps me recharge.\n", "• Photography taught me patience and an eye for detail.\n", "• Both make me a calmer product owner." };
        await s.StartAsync();
        await s.E.SubmitManualQuestionAsync(Q);
        await s.SettleAsync();
        var a = Assert.Single(s.E.History);
        Assert.Equal(new[] { "Hiking and photography" }, a.Bullets);
        Assert.Equal(AnswerSource.CacheFallback, a.Source);
        Assert.Equal(3, a.ValidationFlags.Count(f => f.StartsWith("DROPPED_UNGROUNDED_PERSONAL")));
    }

    [Fact]
    public async Task Engine_FactsInAnotherLanguage_AreNotComparedWordForWord()
    {
        // German facts, English answer: a word-by-word check would drop every translated bullet.
        var s = new InterviewSimulator(Rc2EngineAcceptanceTests.WithPersonal(("hobby", "Wandern und Fotografie in den Bergen")),
            options: new EngineOptions { AutoTick = false, UseFastCache = false });
        s.P.Responder = _ => new[] { "MODE: VERIFIED\n", "• In my free time I enjoy hiking and photography in the mountains." };
        await s.StartAsync();
        await s.E.SubmitManualQuestionAsync(Q);
        await s.SettleAsync();
        var a = Assert.Single(s.E.History);
        Assert.Equal(new[] { "In my free time I enjoy hiking and photography in the mountains." }, a.Bullets);
    }

    [Fact]
    public async Task Engine_KeepsAGroundedRephrasing_AndDropsOnlyTheEmbellishment()
    {
        var s = new InterviewSimulator(Rc2EngineAcceptanceTests.WithPersonal(("hobby", "Hiking and photography")),
            options: new EngineOptions { AutoTick = false, UseFastCache = false });
        s.P.Responder = _ => new[] { "MODE: VERIFIED\n", "• In my free time I enjoy hiking and photography.\n", "• It keeps me balanced and creative." };
        await s.StartAsync();
        await s.E.SubmitManualQuestionAsync(Q);
        await s.SettleAsync();
        var a = Assert.Single(s.E.History);
        Assert.Equal(new[] { "In my free time I enjoy hiking and photography." }, a.Bullets);
        Assert.Equal(AnswerSource.Llm, a.Source);
        var prompt = s.P.Calls[0][1].Content;
        Assert.Contains("Do NOT add reasons, feelings, benefits, personality traits", prompt);
    }
}

/// <summary>RC4: confirming a hobby must not make one count say 44 and another 45 without saying why.</summary>
public class FactCountSemanticsTests
{
    [Fact]
    public void AddingAHobby_IsCountedAsAnInterviewProfileAnswer_NotAsAnExperienceFact()
    {
        var p = new InterviewAssistant.Core.Domain.CandidateProfileRecord();
        for (int i = 0; i < 44; i++)
        {
            var f = new InterviewAssistant.Core.Domain.ProfileFact { Kind = InterviewAssistant.Core.Domain.FactKind.Achievement, Text = "Fact " + i };
            f.SetConfirmed(true, DateTime.UtcNow);
            p.Facts.Add(f);
        }
        Assert.Equal(44, p.Stats.ConfirmedExperience);
        InterviewAssistant.Core.Domain.ProfileService.SetPersonal(p, "hobby", "Hiking and photography");
        Assert.Equal(45, p.Stats.Confirmed);
        Assert.Equal(44, p.Stats.ConfirmedExperience);
        Assert.Equal(1, p.Stats.ConfirmedProfileAnswers);
        var text = InterviewAssistant.Core.Languages.Loc.Format("en", "toast_profile_saved_detail", p.Stats.ConfirmedExperience, p.Stats.ConfirmedProfileAnswers);
        Assert.StartsWith("44 confirmed experience facts + 1 interview-profile answers.", text);
    }

    [Fact]
    public void SplitCountKeys_HaveTheirPlaceholdersInEveryLanguage()
    {
        foreach (var l in InterviewAssistant.Core.Languages.LanguageRegistry.All)
        {
            var start = InterviewAssistant.Core.Languages.Loc.Format(l.Code, "start_profile_status", 1, 44, 1, 3);
            Assert.Contains("44", start); Assert.Contains("3", start);
            var row = InterviewAssistant.Core.Languages.Loc.Format(l.Code, "profile_confirmed_count", 44, 1);
            Assert.Contains("44", row); Assert.Contains("1", row);
        }
    }
}
