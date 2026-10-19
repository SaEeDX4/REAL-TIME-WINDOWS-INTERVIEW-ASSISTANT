using System.Xml.Linq;
using Xunit;

namespace InterviewAssistant.Tests;

/// <summary>
/// RC5 accessibility audit: rules that keep screen-reader output real rather than declared. WPF panels and Borders have
/// no automation peer, so a name or live setting on them never reaches UI Automation; a live setting without a
/// LiveRegionChanged event is never announced. These tests parse the real XAML so a regression fails CI.
/// </summary>
public class Rc5AccessibilityTests
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

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly string[] PeerlessTypes = { "Border", "StackPanel", "DockPanel", "Grid", "WrapPanel" };

    public static IEnumerable<object[]> XamlFiles() =>
        Directory.GetFiles(AppDir, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(f => new object[] { Path.GetRelativePath(AppDir, f) });

    private static XDocument Load(string file) => XDocument.Load(Path.Combine(AppDir, file));
    private static string Id(XElement e) => (string?)e.Attribute(X + "Name") ?? $"{e.Name.LocalName}@{((System.Xml.IXmlLineInfo)e).LineNumber}";

    [Theory]
    [MemberData(nameof(XamlFiles))]
    public void LiveRegions_UseTheAnnouncingAttachedProperty(string file)
    {
        // AutomationProperties.LiveSetting alone is inert: v:LiveRegion.Mode raises LiveRegionChanged on every change
        var raw = File.ReadAllText(Path.Combine(AppDir, file));
        Assert.DoesNotContain("AutomationProperties.LiveSetting=", raw);
    }

    [Theory]
    [MemberData(nameof(XamlFiles))]
    public void FocusableOrNamedContainers_HaveAnAutomationPeer(string file)
    {
        var doc = XDocument.Load(Path.Combine(AppDir, file), LoadOptions.SetLineInfo);
        var offenders = doc.Descendants()
            .Where(e => PeerlessTypes.Contains(e.Name.LocalName) && e.Name.NamespaceName.StartsWith("http://schemas.microsoft.com/winfx", StringComparison.Ordinal))
            .Where(e => (string?)e.Attribute("Focusable") == "True" || e.Attributes().Any(a => a.Name.LocalName == "AutomationProperties.Name"))
            .Select(Id).ToList();
        Assert.True(offenders.Count == 0, $"{file}: focusable or named Border/panel without an automation peer (use v:AccessibleBorder): {string.Join(", ", offenders)}");
    }

    [Fact]
    public void StyledFocusableBorders_AreAccessibleBorders()
    {
        // a style that makes a Border focusable must target the peer-carrying subclass
        var home = File.ReadAllText(Path.Combine(AppDir, "HomeWindow.xaml"));
        Assert.Contains("<Style x:Key=\"StepCard\" TargetType=\"v:AccessibleBorder\"", home);
    }

    [Theory]
    [InlineData("MainWindow.xaml")] [InlineData("Views/SettingsPage.xaml")] [InlineData("Views/PreInterviewPage.xaml")]
    public void AudioMeters_AreProgressBarsForScreenReaders(string file)
    {
        var meters = Load(file).Descendants().Where(e => e.Name.LocalName == "LevelMeter").ToList();
        Assert.NotEmpty(meters);
        Assert.All(meters, m => Assert.Equal("{Binding AudioLevel}", (string?)m.Attribute("Level")));
    }

    [Fact]
    public void LiveReadinessIndicator_IsAnIcon_NotAColourOnlyDot()
    {
        var main = Load("MainWindow.xaml");
        var dot = main.Descendants().Single(e => (string?)e.Attribute(X + "Name") == "ReadinessDot");
        Assert.Equal("TextBlock", dot.Name.LocalName);
        var chrome = File.ReadAllText(Path.Combine(AppDir, "MainWindow.Chrome.cs"));
        Assert.Contains("ReadinessDot.Text = ReadinessPresentation.Icon(state)", chrome);
    }

    [Fact]
    public void ToggleMenuRows_AnnounceTheirState_AndInPlaceActionsKeepFocus()
    {
        var chrome = File.ReadAllText(Path.Combine(AppDir, "MainWindow.Chrome.cs"));
        Assert.Contains("SetItemStatus(MoreCompact", chrome);
        Assert.Contains("SetItemStatus(MoreCoach", chrome);
        Assert.Contains("CloseMore(restoreFocus: true);\n        _vm.Settings.CompactMode", chrome.Replace("\r\n", "\n"));
    }

    [Fact]
    public void RadioGroups_AreOneTabStop_WithArrowSelection()
    {
        foreach (var f in new[] { "Views/ExportChoiceList.xaml.cs", "MainWindow.Focus.cs" })
            Assert.Contains("RovingRadios.Attach(", File.ReadAllText(Path.Combine(AppDir, f)));
    }

    [Fact]
    public void InfoTips_NameTheFieldTheyExplain()
    {
        var theme = File.ReadAllText(Path.Combine(AppDir, "Themes", "Theme.xaml"));
        Assert.Contains("Subject=\"{TemplateBinding Label}\"", theme);
    }
}
