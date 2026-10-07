using InterviewAssistant.Core.Languages;
using Xunit;

namespace InterviewAssistant.Tests;

public class LanguageTests
{
    [Theory]
    [InlineData("How would you prioritize the product backlog for this team?", "en")]
    [InlineData("¿Cómo priorizaría usted el backlog del producto para este equipo?", "es")]
    [InlineData("Comment prioriseriez-vous le backlog produit pour cette équipe ?", "fr")]
    [InlineData("Wie würden Sie das Produkt-Backlog für dieses Team priorisieren?", "de")]
    [InlineData("Como você priorizaria o backlog do produto para esta equipe?", "pt")]
    [InlineData("Come darebbe priorità al backlog del prodotto per questo team?", "it")]
    [InlineData("كيف ستحدد أولويات قائمة المنتج لهذا الفريق؟", "ar")]
    [InlineData("چگونه بک‌لاگ محصول را برای این تیم اولویت‌بندی می‌کنید؟", "fa")]
    [InlineData("你会如何为这个团队确定产品待办事项的优先级？", "zh")]
    [InlineData("आप इस टीम के लिए प्रोडक्ट बैकलॉग को कैसे प्राथमिकता देंगे?", "hi")]
    public void DetectsAllTenLaunchLanguages(string text, string expected) => Assert.Equal(expected, LanguageDetector.Detect(text).Code);

    [Fact]
    public void ShortOrUnknownTextFallsBackToLockedLanguage()
    {
        Assert.Equal("de", LanguageDetector.DetectOr("OK", "de"));
        Assert.Equal("fr", LanguageDetector.DetectOr("", "fr"));
        Assert.Equal("und", LanguageDetector.Detect("12345 ?!").Code);
    }

    [Fact]
    public void RegistryHasTenLanguagesWithRtlForArabicAndPersian()
    {
        Assert.Equal(10, LanguageRegistry.All.Count);
        Assert.Equal(new[] { "ar", "fa" }, LanguageRegistry.All.Where(l => l.IsRightToLeft).Select(l => l.Code));
        Assert.True(LanguageRegistry.IsSupported("pt-BR"));
        Assert.False(LanguageRegistry.IsSupported("xx"));
    }

    [Theory]
    [InlineData("same", "auto", "de", "de")]   // follow interviewer
    [InlineData("fa", "en", "en", "fa")]       // cross-language: English question, Persian answer
    [InlineData("same", "fr", "und", "fr")]    // undetected → locked interview language
    [InlineData("same", "auto", null, "en")]
    public void AnswerLanguageResolution(string answer, string interview, string? detected, string expected) =>
        Assert.Equal(expected, LanguageRegistry.ResolveAnswerLanguage(answer, interview, detected));

    [Fact]
    public void AnswerRulePreservesTermsAndAvoidsLiteralTranslation()
    {
        var rule = LanguageRegistry.AnswerLanguageRule("fa");
        Assert.Contains("Persian", rule);
        Assert.Contains("فارسی", rule);
        Assert.Contains("not translate word-for-word", rule);
        Assert.Contains("Product Owner", rule);
    }
}

public class UiStringsTests
{
    [Fact]
    public void AllTenLanguagesAreCompleteAndDistinct()
    {
        foreach (var l in InterviewAssistant.Core.Languages.LanguageRegistry.All)
        {
            Assert.True(InterviewAssistant.Core.Languages.UiStrings.IsComplete(l.Code), l.Code);
            if (l.Code != "en") Assert.NotEqual(InterviewAssistant.Core.Languages.UiStrings.Get("en", "waiting"), InterviewAssistant.Core.Languages.UiStrings.Get(l.Code, "waiting"));
        }
    }

    [Theory]
    [InlineData("ar", "ar")] [InlineData("fa", "fa")] [InlineData("de-DE", "de")] [InlineData("xx", "en")] [InlineData(null, "en")]
    public void LookupNormalisesAndFallsBack(string? code, string expected) =>
        Assert.Equal(InterviewAssistant.Core.Languages.UiStrings.Get(expected, "start"), InterviewAssistant.Core.Languages.UiStrings.Get(code, "start"));

    [Fact]
    public void PlaceholdersSurviveTranslationAndUnknownKeysAreVisible()
    {
        foreach (var l in InterviewAssistant.Core.Languages.LanguageRegistry.All)
            Assert.Contains("7", InterviewAssistant.Core.Languages.UiStrings.Format(l.Code, "minutes_left", 7));
        Assert.Equal("no_such_key", InterviewAssistant.Core.Languages.UiStrings.Get("de", "no_such_key"));
    }

    [Fact]
    public void RtlLanguagesContainArabicScript()
    {
        foreach (var code in new[] { "ar", "fa" })
            Assert.Contains(InterviewAssistant.Core.Languages.UiStrings.Get(code, "question"), c => c >= '؀' && c <= 'ۿ');
    }
}
