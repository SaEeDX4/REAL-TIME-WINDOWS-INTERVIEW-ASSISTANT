using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace InterviewAssistant.Tests;

/// <summary>
/// RC5 design system: form alignment is an architecture rule, not per-page margins. Every labelled input sits in the one
/// field layout (<c>v:FormField</c>: label, ⓘ, accessory, field, hint), field and page widths come from layout tokens in
/// Themes/Theme.xaml, and pages share one hint/label style. These tests parse the real XAML so a page that drifts from
/// the system fails CI.
/// </summary>
public class Rc5DesignSystemTests
{
    private static string AppDir
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "InterviewAssistant.sln"))) d = d.Parent;
            return Path.Combine(d!.FullName, "src", "InterviewAssistant.App");
        }
    }

    private const string V = "clr-namespace:InterviewAssistant.App.Views";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly string[] Inputs = { "TextBox", "PasswordBox", "ComboBox" };

    /// <summary>The pages of Home and their user controls (the Live window's single question field is its own layout).</summary>
    public static IEnumerable<object[]> PageFiles() => new[] { "HomeWindow.xaml", "Views/SettingsPage.xaml", "Views/PreInterviewPage.xaml", "Views/ExportChoiceList.xaml" }
        .Select(f => new object[] { f });

    private static XDocument Load(string file) => XDocument.Load(Path.Combine(AppDir, file));
    private static string Name(XElement e) => (string?)e.Attribute(X + "Name") ?? e.Name.LocalName;

    [Theory]
    [MemberData(nameof(PageFiles))]
    public void EveryLabelledInput_SitsInAFormField(string file)
    {
        var offenders = Load(file).Descendants().Where(e => Inputs.Contains(e.Name.LocalName))
            .Where(e => !e.Ancestors().Any(a => a.Name.LocalName == "FormField" && a.Name.NamespaceName == V)
                        && !e.Ancestors().Any(a => a.Name.LocalName is "DataTemplate" or "ControlTemplate"))
            .Select(Name).ToList();
        Assert.True(offenders.Count == 0, $"{file}: inputs outside v:FormField: {string.Join(", ", offenders)}");
    }

    [Theory]
    [MemberData(nameof(PageFiles))]
    public void Inputs_HaveNoLiteralSizes_OnlyTokens(string file)
    {
        var offenders = Load(file).Descendants().Where(e => Inputs.Contains(e.Name.LocalName))
            .SelectMany(e => e.Attributes().Where(a => a.Name.LocalName is "Width" or "MinWidth" or "MaxWidth" or "Height" or "MinHeight" or "MaxHeight" or "Margin")
                .Where(a => !a.Value.StartsWith("{StaticResource S.", StringComparison.Ordinal))
                .Select(a => $"{Name(e)}.{a.Name.LocalName}={a.Value}"))
            .ToList();
        Assert.True(offenders.Count == 0, $"{file}: literal input sizes/margins: {string.Join(", ", offenders)}");
    }

    [Theory]
    [MemberData(nameof(PageFiles))]
    public void FieldLabels_AreNeverHandMade(string file)
    {
        // a label above a field belongs to its FormField (same spacing, same accessibility link)
        var raw = File.ReadAllText(Path.Combine(AppDir, file));
        Assert.DoesNotContain("StaticResource T.FieldLabel", raw);
    }

    [Fact]
    public void CodeBuiltDialogs_UseTheSameFieldLayout()
    {
        foreach (var file in Directory.GetFiles(AppDir, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
            Assert.False(File.ReadAllText(file).Contains("\"T.FieldLabel\""), $"{Path.GetFileName(file)} builds its own field label; use FormField / DialogForm.Labelled");
    }

    [Fact]
    public void PageWidths_ComeFromLayoutTokens()
    {
        var home = Load("HomeWindow.xaml");
        foreach (var page in new[] { "PageStart", "PageHelp", "PageAccount", "SignedOutPanel" })
        {
            var e = home.Descendants().Single(x => (string?)x.Attribute(X + "Name") == page);
            var max = (string?)e.Attribute("MaxWidth");
            Assert.True(max != null && max.StartsWith("{StaticResource S.", StringComparison.Ordinal), $"{page} MaxWidth={max}");
        }
        var settings = File.ReadAllText(Path.Combine(AppDir, "Views", "SettingsPage.xaml"));
        Assert.Contains("MaxWidth=\"{StaticResource S.FormMaxWidth}\"", settings);
    }

    [Fact]
    public void LayoutTokens_AreDefinedOnce_InTheTheme()
    {
        var theme = File.ReadAllText(Path.Combine(AppDir, "Themes", "Theme.xaml"));
        foreach (var token in new[] { "S.ReadingMaxWidth", "S.PageMaxWidth", "S.ContentMaxWidth", "S.SplitMaxWidth", "S.FormMaxWidth", "S.FieldShortWidth",
                                      "S.MultilineTallHeight", "S.ControlHeight", "S.FieldLabelMargin", "S.FieldGap", "S.CardPadding", "S.ColumnGap" })
            Assert.Single(Regex.Matches(theme, $"x:Key=\"{Regex.Escape(token)}\""));
        Assert.Contains("<Style TargetType=\"v:FormField\">", theme);
        Assert.Contains("Value=\"{StaticResource S.FieldShortWidth}\"", theme);   // short fields: one width everywhere
    }

    [Theory]
    [MemberData(nameof(PageFiles))]
    public void PageHintAndMetaStyles_DeriveFromTheShared_WithoutOverrides(string file)
    {
        foreach (var style in Load(file).Descendants().Where(e => e.Name.LocalName == "Style"))
        {
            var key = (string?)style.Attribute(X + "Key");
            if (key is not ("Hint" or "AboutKey" or "AboutValue")) continue;
            var basedOn = (string?)style.Attribute("BasedOn") ?? "";
            Assert.Matches(@"\{StaticResource T\.(Hint|MetaKey|MetaValue)\}", basedOn);
            Assert.Empty(style.Elements());   // no local setters: one hint look across the app
        }
    }

    [Fact]
    public void FormField_LinksItsLabelToTheInput_AndIsNotATabStop()
    {
        var src = File.ReadAllText(Path.Combine(AppDir, "Views", "FormField.cs"));
        Assert.Contains("AutomationProperties.SetLabeledBy(input, label)", src);
        Assert.Contains("IsTabStopProperty.OverrideMetadata(typeof(FormField), new FrameworkPropertyMetadata(false))", src);
    }
}
