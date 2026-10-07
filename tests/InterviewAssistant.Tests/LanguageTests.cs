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
