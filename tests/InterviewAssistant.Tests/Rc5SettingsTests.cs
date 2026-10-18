using System.Reflection;
using System.Text.RegularExpressions;
using InterviewAssistant.App.UiState;
using InterviewAssistant.Core.Languages;
using Xunit;

namespace InterviewAssistant.Tests;

/// <summary>
/// rc.5 Settings redesign: one category at a time (ordered ids, texts, deep-link parsing), "Reset this section" /
/// "Restore all defaults" (each section resets exactly its own fields, account/session/workspace/placement/language
/// fields never change), Updates moved from Account into Settings, and the ⓘ info tip replacing secondary hint
/// paragraphs. WPF cannot run here: the logic (UiState) is unit-tested, the wiring is checked at source level, and the
/// Windows self-test drives the real page.
/// </summary>
public class Rc5SettingsTests
{
    private static string RepoRoot
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "InterviewAssistant.sln"))) d = d.Parent;
            return d?.FullName ?? throw new DirectoryNotFoundException("repo root");
        }
    }

    // Line endings normalized: Windows CI checks the sources out with CRLF.
    private static string App(string relative) => File.ReadAllText(Path.Combine(RepoRoot, "src", "InterviewAssistant.App", relative)).Replace("\r\n", "\n");

    // ---------------- categories ----------------

    [Fact]
    public void Categories_AreOrdered_WithTitleAndOneLineDescription_InEveryLanguage()
    {
        Assert.Equal(new[] { "audio", "answers", "listening", "window", "hotkeys", "privacy", "updates" }, SettingsCategories.Ids);
        Assert.Equal(SettingsCategories.Ids, SettingsCategories.All.Select(c => c.Id));
        Assert.Equal("set_nav_audio", SettingsCategories.All[0].TitleKey);
        Assert.Equal("set_cat_audio_desc", SettingsCategories.All[0].DescriptionKey);
        Assert.Equal("Meeting audio device and level", Loc.TableFor("en")["set_cat_audio_desc"]);
        foreach (var lang in LanguageRegistry.All.Select(l => l.Code))
        {
            var t = Loc.TableFor(lang);
            foreach (var c in SettingsCategories.All)
            {
                Assert.True(t.ContainsKey(c.TitleKey), $"{lang}: {c.TitleKey}");
                Assert.True(t.TryGetValue(c.DescriptionKey, out var d) && d.Length > 0 && !d.Contains('\n'), $"{lang}: {c.DescriptionKey}");
            }
        }
    }

    [Theory]
    [InlineData("audio", null, "audio")]
    [InlineData("updates", null, "updates")]
    [InlineData(" HOTKEYS ", null, "hotkeys")]
    [InlineData(null, null, "audio")]                 // no section: Audio …
    [InlineData(null, "privacy", "privacy")]          // … or the last used category
    [InlineData("bogus", "window", "window")]         // unknown section: last used
    [InlineData("bogus", "nope", "audio")]            // unknown last used: Audio
    [InlineData("", null, "audio")]
    public void Parse_SelectsTheSection_FallsBackToLastUsedThenAudio(string? section, string? last, string expected) =>
        Assert.Equal(expected, SettingsCategories.Parse(section, last));

    [Fact]
    public void DeepLinks_SelectTheirCategory()
    {
        foreach (var id in SettingsCategories.Ids)
        {
            var route = HomeRoute.Parse("settings/" + id);
            Assert.Equal("settings", route.Page);
            Assert.Equal(id, SettingsCategories.Parse(route.Section));
        }
        Assert.Equal("audio", SettingsCategories.Parse(HomeRoute.Parse("settings").Section));
    }

    [Fact]
    public void UpDown_MoveWithinTheList_ClampedAtBothEnds()
    {
        Assert.Equal("answers", SettingsCategories.Move("audio", +1));
        Assert.Equal("audio", SettingsCategories.Move("audio", -1));
        Assert.Equal("updates", SettingsCategories.Move("updates", +1));
        Assert.Equal("privacy", SettingsCategories.Move("updates", -1));
        Assert.Equal("audio", SettingsCategories.Move("bogus", +1));
        Assert.Equal(6, SettingsCategories.IndexOf("updates"));
        Assert.Equal(-1, SettingsCategories.IndexOf(null));
    }

    // ---------------- reset to defaults ----------------

    /// <summary>Same properties and defaults as AppSettings (which the test project does not compile); kept in sync by a source test.</summary>
    private enum FakeFont { Compact, Normal, Large, ExtraLarge }

    private sealed class FakeSettings
    {
        public string? PlaybackDeviceId { get; set; }
        public int SettingsVersion { get; set; } = 3;
        public string AnswerModel { get; set; } = "gpt-5.4-mini";
        public string TranscriptionModel { get; set; } = "gpt-4o-transcribe";
        public string RealtimeProtocol { get; set; } = "auto";
        public FakeFont FontPreset { get; set; } = FakeFont.Large;
        public double WindowOpacity { get; set; } = 0.97;
        public double VadSensitivity { get; set; } = 1.0;
        public bool UseFastCache { get; set; } = true;
        public bool CompactMode { get; set; }
        public bool AlwaysOnTop { get; set; } = true;
        public bool ShowDiagnostics { get; set; }
        public bool SaveSessionTranscript { get; set; }
        public string HotkeyToggleWindow { get; set; } = "Ctrl+Alt+Space";
        public string HotkeyStartPause { get; set; } = "Ctrl+Alt+L";
        public string HotkeyManualInput { get; set; } = "Ctrl+Alt+Q";
        public string HotkeyCoachToggle { get; set; } = "Ctrl+Alt+C";
        public string HotkeyResetAdaptive { get; set; } = "Ctrl+Alt+R";
        public string Mode { get; set; } = "cloud";
        public string? UiLanguage { get; set; }
        public string AnswerLanguage { get; set; } = "same";
        public string? ReportLanguage { get; set; }
        public bool AuthorizedUseAcknowledged { get; set; }
        public bool AutoFitAnswer { get; set; } = true;
        public bool StartInCoachMode { get; set; }
        public Guid? CloudDeviceId { get; set; }
        public string UpdateChannel { get; set; } = "stable";
        public double? Left { get; set; }
        public double? Top { get; set; }
        public double Width { get; set; } = 720;
        public double Height { get; set; } = 560;
        public bool FirstRunCompleted { get; set; }
        public bool LiveIntroDismissed { get; set; }
        public string? InterviewFocusPreset { get; set; }
        public bool InterviewFocusIntroSeen { get; set; }
        public double? FocusRestoreLeft { get; set; }
        public double? FocusRestoreTop { get; set; }
        public double? FocusRestoreWidth { get; set; }
        public double? FocusRestoreHeight { get; set; }
        public double? HomeLeft { get; set; }
        public double? HomeTop { get; set; }
        public double HomeWidth { get; set; } = 980;
        public double HomeHeight { get; set; } = 700;
        public bool HomeMaximized { get; set; }
        public string? ActiveProfileId { get; set; }
        public string? ActiveTargetId { get; set; }
        public double PriceTranscribePerMinute { get; set; } = 0.017;
        public double PriceInputPerMTok { get; set; } = 0.75;
        public double PriceOutputPerMTok { get; set; } = 4.50;
    }

    private static readonly PropertyInfo[] FakeProps = typeof(FakeSettings).GetProperties();

    /// <summary>Every value differs from its default (what a long-time user's settings file could hold).</summary>
    private static FakeSettings Customized()
    {
        var s = new FakeSettings();
        foreach (var p in FakeProps)
        {
            object value = p.PropertyType switch
            {
                var t when t == typeof(string) => "custom-" + p.Name,
                var t when t == typeof(bool) => !(bool)p.GetValue(s)!,
                var t when t == typeof(double) => (double)p.GetValue(s)! + 0.5,
                var t when t == typeof(double?) => 123.0,
                var t when t == typeof(int) => (int)p.GetValue(s)! + 7,
                var t when t == typeof(Guid?) => Guid.NewGuid(),
                var t when t == typeof(FakeFont) => FakeFont.Compact,
                var t => throw new NotSupportedException(t.Name),
            };
            p.SetValue(s, value);
        }
        foreach (var p in FakeProps) Assert.NotEqual(p.GetValue(new FakeSettings()), p.GetValue(s));
        return s;
    }

    /// <summary>Public instance properties of AppSettings, read from its source.</summary>
    private static string[] AppSettingsProperties() =>
        Regex.Matches(App("Services/AppSettings.cs"), @"^\s*public (?!static)[\w?<>.]+ (\w+) \{ get; set; \}", RegexOptions.Multiline)
             .Select(m => m.Groups[1].Value).ToArray();

    [Fact]
    public void EveryAppSetting_IsClassifiedExactlyOnce_AndTheTestMirrorMatchesAppSettings()
    {
        var props = AppSettingsProperties();
        Assert.True(props.Length > 30, $"{props.Length} properties parsed");
        Assert.Equal(props.OrderBy(x => x), FakeProps.Select(p => p.Name).OrderBy(x => x));
        var classified = SettingsDefaults.PreferenceFields.Concat(SettingsDefaults.Protected).Concat(SettingsDefaults.NotOnSettingsPage).ToList();
        Assert.Equal(classified.Count, classified.Distinct().Count());   // no field in two lists
        Assert.Equal(props.OrderBy(x => x), classified.OrderBy(x => x)); // a new setting must be classified
        // each category owns its fields alone
        var owned = SettingsCategories.Ids.SelectMany(SettingsDefaults.FieldsOf).ToList();
        Assert.Equal(owned.Count, owned.Distinct().Count());
        Assert.All(SettingsCategories.Ids, id => Assert.NotEmpty(SettingsDefaults.FieldsOf(id)));
    }

    [Fact]
    public void ProtectedFields_CoverAccountSessionWorkspaceFirstRunPlacementAndUiLanguage()
    {
        foreach (var f in new[] { "Mode", "CloudDeviceId", "ActiveProfileId", "ActiveTargetId", "AuthorizedUseAcknowledged", "FirstRunCompleted", "UiLanguage",
                                  "Left", "Top", "Width", "Height", "HomeLeft", "HomeTop", "HomeWidth", "HomeHeight", "HomeMaximized" })
        {
            Assert.Contains(f, SettingsDefaults.Protected);
            Assert.DoesNotContain(f, SettingsDefaults.PreferenceFields);
        }
        // the fields the brief lists per section
        Assert.Equal(new[] { "PlaybackDeviceId" }, SettingsDefaults.FieldsOf("audio"));
        Assert.Contains("FontPreset", SettingsDefaults.FieldsOf("answers"));
        Assert.Contains("UseFastCache", SettingsDefaults.FieldsOf("answers"));
        Assert.Equal(new[] { "VadSensitivity" }, SettingsDefaults.FieldsOf("listening"));
        Assert.Equal(new[] { "WindowOpacity", "AlwaysOnTop", "CompactMode", "AutoFitAnswer" }, SettingsDefaults.FieldsOf("window"));
        Assert.Contains("HotkeyToggleWindow", SettingsDefaults.FieldsOf("hotkeys"));
        Assert.Equal(new[] { "SaveSessionTranscript" }, SettingsDefaults.FieldsOf("privacy"));
        Assert.Equal(new[] { "UpdateChannel" }, SettingsDefaults.FieldsOf("updates"));
    }

    [Theory]
    [InlineData("audio")] [InlineData("answers")] [InlineData("listening")] [InlineData("window")]
    [InlineData("hotkeys")] [InlineData("privacy")] [InlineData("updates")]
    public void ResetSection_ResetsExactlyItsFields(string category)
    {
        var s = Customized();
        var before = Customized();   // same customized values (Guid differs, but CloudDeviceId is never reset)
        before.CloudDeviceId = s.CloudDeviceId;
        var fresh = new FakeSettings();
        var changed = SettingsDefaults.Reset(s, new FakeSettings(), category);
        var fields = SettingsDefaults.FieldsOf(category);
        Assert.Equal(fields.OrderBy(x => x), changed.OrderBy(x => x));
        foreach (var p in FakeProps)
            Assert.Equal(fields.Contains(p.Name) ? p.GetValue(fresh) : p.GetValue(before), p.GetValue(s));
    }

    [Fact]
    public void RestoreAll_EqualsFreshDefaultsForPreferences_AndNeverTouchesProtectedFields()
    {
        var s = Customized();
        var keep = FakeProps.ToDictionary(p => p.Name, p => p.GetValue(s));
        var fresh = new FakeSettings();
        var changed = SettingsDefaults.Reset(s, new FakeSettings(), category: null);
        Assert.Equal(SettingsDefaults.PreferenceFields.OrderBy(x => x), changed.OrderBy(x => x));
        foreach (var p in FakeProps)
        {
            if (SettingsDefaults.PreferenceFields.Contains(p.Name)) Assert.Equal(p.GetValue(fresh), p.GetValue(s));
            else Assert.Equal(keep[p.Name], p.GetValue(s));   // account, session, workspace, first run, placement, languages …
        }
        // already at defaults: nothing changes, nothing to apply
        Assert.Empty(SettingsDefaults.Reset(s, new FakeSettings(), category: null));
        Assert.Empty(SettingsDefaults.Reset(s, new FakeSettings(), "bogus"));
    }

    [Fact]
    public void ResetEffect_RebuildsTheEngineOnlyWhenNeeded()
    {
        Assert.Equal((false, false), SettingsDefaults.EffectOf(new[] { "WindowOpacity", "UpdateChannel" }));
        Assert.Equal((true, false), SettingsDefaults.EffectOf(new[] { "VadSensitivity" }));
        Assert.Equal((true, false), SettingsDefaults.EffectOf(new[] { "UseFastCache" }));
        Assert.Equal((true, true), SettingsDefaults.EffectOf(new[] { "AnswerModel" }));
    }

    [Fact]
    public void ResetAndRestore_AskInTheInAppDialog_CancelIsTheDefault_AndApplyLikeASave()
    {
        var cs = App("Views/SettingsPage.xaml.cs");
        Assert.Contains("AppDialog.ConfirmAsync(owner, message, Loc.T(\"set_restore_confirm\"), destructive: true, title: Loc.T(\"set_restore_title\"))", cs);
        Assert.Contains("SettingsDefaults.Reset(S, new AppSettings(), category)", cs);
        Assert.Contains("_main?.ApplySettings(engineChanged, modelChanged);\n        DefaultsRestored?.Invoke(category);", cs);
        Assert.Contains("Guard.MarkClean();", cs);
        var en = Loc.TableFor("en");
        Assert.Equal("Restore defaults?", en["set_restore_title"]);
        Assert.Equal("This resets Interview Assistant preferences. Your account, profiles, interviews and reports will not be changed.", en["set_restore_body"]);
        Assert.StartsWith("This resets the {0} preferences.", en["set_restore_section_body"]);
        Assert.Equal("Restore defaults", en["set_restore_confirm"]);
        var xaml = App("Views/SettingsPage.xaml");
        Assert.Contains("Click=\"ResetSection_Click\"", xaml);
        Assert.Contains("Click=\"RestoreAll_Click\"", xaml);
        var home = App("HomeWindow.xaml.cs");
        Assert.Contains("PageSettings.DefaultsRestored += category => Notify(", home);   // toast
    }

    // ---------------- page composition ----------------

    [Fact]
    public void SettingsPage_ShowsOneCategoryAtATime_WithAnAccessibleList_AndKeepsTheExplicitSaveModel()
    {
        var xaml = App("Views/SettingsPage.xaml");
        var cs = App("Views/SettingsPage.xaml.cs");
        Assert.Contains("x:Name=\"CategoryList\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"{v:Loc set_categories}\"", xaml);
        Assert.Contains("PreviewKeyDown=\"CategoryList_PreviewKeyDown\"", xaml);
        Assert.Contains("AutomationProperties.HeadingLevel=\"Level2\"", xaml);
        // the selected row: accent bar (mirrors in RTL) + filled background
        Assert.Matches(@"x:Name=""Bar""[^>]*HorizontalAlignment=""Left""[^>]*Background=""\{StaticResource B\.Accent\}""", xaml);
        Assert.Contains("<Setter TargetName=\"Bar\" Property=\"Visibility\" Value=\"Visible\"/>", xaml);
        Assert.Contains("AutomationProperties.SetItemStatus(item", cs);
        // one panel per category, all but the first collapsed
        foreach (var id in SettingsCategories.Ids)
            Assert.Matches($@"<StackPanel x:Name=""Cat\w+"" Tag=""{id}""", xaml);
        Assert.Contains("panel.Visibility = (string)panel.Tag == id ? Visibility.Visible : Visibility.Collapsed", cs);
        // switching category is not navigation: no guard involved
        var select = cs[cs.IndexOf("private void SelectCategory(", StringComparison.Ordinal)..cs.IndexOf("private void UpdateItemStatus(", StringComparison.Ordinal)];
        Assert.DoesNotContain("Guard", select);
        // the developer key belongs to Answers
        var answers = xaml[xaml.IndexOf("x:Name=\"CatAnswers\"", StringComparison.Ordinal)..xaml.IndexOf("x:Name=\"CatListening\"", StringComparison.Ordinal)];
        Assert.Contains("x:Name=\"DeveloperKeyPanel\"", answers);
        // explicit save, validation, preview revert remain
        Assert.Contains("Guard.Save = Save;", cs);
        Assert.Contains("Guard.Discard = RevertPreview;", cs);
        Assert.Contains("set_hotkey_conflict", cs);
        Assert.Contains("x:Name=\"SettingsFooter\"", App("HomeWindow.xaml"));
    }

    [Fact]
    public void Updates_AreInSettings_NotOnTheAccountPage()
    {
        var home = App("HomeWindow.xaml");
        var account = home[home.IndexOf("x:Name=\"PageAccount\"", StringComparison.Ordinal)..home.IndexOf("<!-- ===== Settings ===== -->", StringComparison.Ordinal)];
        foreach (var gone in new[] { "ChannelBox", "update_preference", "RestartUpdateBtn", "UpdateStatus", "BuildChannelText", "updates_hint" })
            Assert.DoesNotContain(gone, account);
        var homeCs = App("HomeWindow.xaml.cs");
        Assert.DoesNotContain("Channel_Changed", homeCs);
        Assert.DoesNotContain("BuildChannelText", homeCs);
        Assert.Contains("_vm.Updates.Changed += OnUpdatesChanged;", homeCs);
        Assert.Contains("PageSettings.RefreshUpdates()", homeCs);

        var xaml = App("Views/SettingsPage.xaml");
        var updates = xaml[xaml.IndexOf("x:Name=\"CatUpdates\"", StringComparison.Ordinal)..];
        foreach (var here in new[] { "x:Name=\"ChannelBox\"", "{v:Loc update_preference}", "x:Name=\"CheckUpdatesBtn\"", "x:Name=\"RestartUpdateBtn\"",
                                     "x:Name=\"UpdateStatus\"", "x:Name=\"UpdateVersion\"", "x:Name=\"UpdateCommit\"", "{v:Loc set_upd_install_note}" })
            Assert.Contains(here, updates);
        Assert.Equal("Updates download in the background and install when you close the app — never during an interview.", Loc.TableFor("en")["set_upd_install_note"]);
        var cs = App("Views/SettingsPage.xaml.cs");
        Assert.Contains("CheckUpdatesBtn.IsEnabled = u.CanCheck && !u.IsChecking;", cs);   // unsigned builds: disabled, status says why
        Assert.Contains("_vm.Updates.CheckNowAsync(_vm.SessionActive)", cs);
    }

    [Fact]
    public void CheckNow_ReusesTheBackgroundCheck_AndNeverFakesAnUpdateForUnsignedBuilds()
    {
        var svc = App("Services/UpdateService.cs");
        var now = svc[svc.IndexOf("public async Task CheckNowAsync(", StringComparison.Ordinal)..svc.IndexOf("private async Task CheckAsync()", StringComparison.Ordinal)];
        Assert.Contains("if (!CanCheck || _pending != null || IsChecking) return;", now);   // unsigned/portable: the status keeps saying why
        Assert.Contains("UpdatePolicy.ShouldCheckNow(sessionActive)", now);
        Assert.Contains("await CheckAsync();", now);
        var tick = svc[svc.IndexOf("public async Task TickAsync(", StringComparison.Ordinal)..svc.IndexOf("public async Task CheckNowAsync(", StringComparison.Ordinal)];
        Assert.Contains("await CheckAsync();", tick);
        Assert.Contains("public bool CanCheck => _mgr is { IsInstalled: true };", svc);
        Assert.Contains("_signer = Environment.ProcessPath is { } exe ? Authenticode.TrustedSigner(exe) : null;", svc);
    }

    // ---------------- info tips ----------------

    [Fact]
    public void InfoTip_IsFocusable_NamedAndDescribedForScreenReaders()
    {
        var cs = App("Views/InfoTip.cs");
        Assert.Contains("public sealed class InfoTip : Button", cs);
        Assert.Contains("AutomationProperties.SetHelpText(this, Text);", cs);
        Assert.Contains("new Binding(\"[info_more]\")", cs);
        Assert.Contains("GotKeyboardFocus +=", cs);
        Assert.Contains("Click += (_, _) => _tip.IsOpen = !_tip.IsOpen;", cs);
        Assert.Contains("Focusable = true;", cs);
    }

    [Fact]
    public void SecondaryHints_AreInfoTips_EssentialStatementsStayVisible()
    {
        var home = App("HomeWindow.xaml");
        // rc.5 design system: the ⓘ is part of the field layout (FormField.Info renders a v:InfoTip)
        Assert.Contains("Info=\"{v:Loc ui_language_hint}\"", home);
        Assert.DoesNotMatch(@"<TextBlock[^>]*\{v:Loc ui_language_hint\}", home);
        var xaml = App("Views/SettingsPage.xaml");
        Assert.Contains("<v:InfoTip Text=\"{v:Loc set_save_hint}\"", xaml);
        Assert.Contains("Info=\"{v:Loc set_end_wait_hint}\"", xaml);
        foreach (var hint in new[] { "set_save_hint", "set_end_wait_hint", "set_audio_hint", "set_hotkeys_hint" })
            Assert.DoesNotMatch($@"<TextBlock[^>]*\{{v:Loc {hint}\}}", xaml);
        Assert.Contains("\"set_audio_hint\"", App("Views/SettingsPage.xaml.cs"));     // ⓘ next to the category heading
        Assert.Contains("\"set_hotkeys_hint\"", App("Views/SettingsPage.xaml.cs"));
        // privacy, security and install-rule statements stay visible
        foreach (var visible in new[] { "set_save_transcript_hint", "set_dev_key_hint", "set_upd_install_note" })
            Assert.Matches($@"<TextBlock[^>]*\{{v:Loc {visible}\}}", xaml);
    }
}
