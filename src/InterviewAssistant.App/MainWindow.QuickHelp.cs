using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using InterviewAssistant.App.UiState;
using InterviewAssistant.Core.Languages;

namespace InterviewAssistant.App;

/// <summary>
/// Quick help (rc.5): the "?" popover with tips for what the user is doing right now (<see cref="QuickHelp"/>) and the
/// hotkeys they configured. Opens by itself once on first use (<see cref="Services.AppSettings.LiveIntroDismissed"/>),
/// never over the first-run onboarding in Home. Content is built when it opens and removed when it closes, so a closed
/// popover never holds text in an old UI language.
/// </summary>
public partial class MainWindow
{
    private Views.Popover _quickHelp = null!;
    private bool _firstUseHelpPending;

    /// <summary>Called once from <see cref="InitChrome"/>.</summary>
    private void InitQuickHelp()
    {
        _quickHelp = new Views.Popover(this, QuickHelpPopup, HelpBtn);
        _quickHelp.Closed += () =>
        {
            _vm.DismissLiveIntro(remember: true);
            QuickHelpBody.Children.Clear();
            QuickHelpAction.Content = null;
        };
        Activated += (_, _) => ShowFirstUseHelp();   // back from the onboarding in Home
    }

    internal bool QuickHelpOpen => _quickHelp.IsOpen;

    /// <summary>The texts the popover shows right now (self-test).</summary>
    internal IReadOnlyList<string> QuickHelpTexts =>
        QuickHelpBody.Children.OfType<FrameworkElement>().SelectMany(Texts).ToList();

    private static IEnumerable<string> Texts(FrameworkElement e) => e switch
    {
        TextBlock t => new[] { t.Text },
        Panel p => p.Children.OfType<FrameworkElement>().SelectMany(Texts),
        _ => Array.Empty<string>(),
    };

    private void Help_Click(object sender, RoutedEventArgs e) => ToggleQuickHelp();

    internal void ToggleQuickHelp()
    {
        if (_quickHelp.IsOpen) { _quickHelp.Close(); return; }
        OpenQuickHelp();
    }

    private void OpenQuickHelp()
    {
        CloseMore();
        BuildQuickHelp();
        _vm.ShowLiveIntro = true;
        _quickHelp.Open(QuickHelpClose);
    }

    /// <summary>First use: once, when the Live window is on screen and the onboarding in Home is not.</summary>
    private void ShowFirstUseHelp()
    {
        if (!_firstUseHelpPending || !IsLoaded || !IsVisible || WindowState == WindowState.Minimized) return;
        if (App.SelfTestOutput == null && (_home is { IsLoaded: true } || Views.AppDialog.IsOpen(this))) return;
        _firstUseHelpPending = false;
        OpenQuickHelp();
    }

    private void BuildQuickHelp()
    {
        var s = _vm.Settings;
        var signInRequired = _vm.UsingCloud && _vm.Cloud.IsConfigured && !_vm.Cloud.IsSignedIn;
        var state = QuickHelp.StateFor(signInRequired, _vm.IsListening, _vm.IsPaused);
        var content = QuickHelp.For(state, answerShown: _vm.Bullets.Count > 0 || _vm.IsCoachView,
            new QuickHelpHotkeys(s.HotkeyStartPause, s.HotkeyManualInput, s.HotkeyToggleWindow, s.HotkeyCoachToggle));

        QuickHelpBody.Children.Clear();
        var first = true;
        foreach (var section in content.Sections)
        {
            var title = new TextBlock { Text = Loc.T(section.TitleKey), FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, first ? 0 : 12, 0, 6) };
            title.SetResourceReference(TextBlock.ForegroundProperty, "B.Text");
            AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level2);
            QuickHelpBody.Children.Add(title);
            foreach (var key in section.LineKeys)
            {
                var line = new TextBlock { Text = Loc.T(key), Margin = new Thickness(0, 0, 0, 5) };
                line.SetResourceReference(StyleProperty, "T.Body");
                QuickHelpBody.Children.Add(line);
            }
            first = false;
        }
        if (content.Hotkeys.Count > 0)
        {
            var header = new TextBlock { Text = Loc.T("qh_shortcuts"), Margin = new Thickness(0, 10, 0, 6) };
            header.SetResourceReference(StyleProperty, "T.Label");
            QuickHelpBody.Children.Add(header);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            foreach (var hk in content.Hotkeys)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var row = grid.RowDefinitions.Count - 1;
                var label = new TextBlock { Text = Loc.T(hk.LabelKey), Margin = new Thickness(0, 2, 12, 2), FontSize = 12.5 };
                label.SetResourceReference(StyleProperty, "T.Body");
                // a technical value: always left-to-right, also inside a right-to-left layout
                var keys = new TextBlock { Text = hk.Keys, FlowDirection = FlowDirection.LeftToRight, FontWeight = FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(0, 2, 0, 2), VerticalAlignment = VerticalAlignment.Center };
                keys.SetResourceReference(TextBlock.ForegroundProperty, "B.Text");
                AutomationProperties.SetName(keys, $"{Loc.T(hk.LabelKey)}: {hk.Keys}");
                Grid.SetRow(label, row); Grid.SetRow(keys, row); Grid.SetColumn(keys, 1);
                grid.Children.Add(label); grid.Children.Add(keys);
            }
            QuickHelpBody.Children.Add(grid);
        }
        var actionKey = QuickHelp.ActionKey(content.Action);
        QuickHelpAction.Content = actionKey == null ? null : Loc.T(actionKey);
        QuickHelpAction.Tag = QuickHelp.ActionRoute(content.Action);
        QuickHelpAction.Visibility = actionKey == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void QuickHelpClose_Click(object sender, RoutedEventArgs e) => _quickHelp.Close(restoreFocus: true);

    private void QuickHelpAction_Click(object sender, RoutedEventArgs e)
    {
        var route = QuickHelpAction.Tag as string;
        _quickHelp.Close(restoreFocus: false);
        if (route != null) OpenHome(route);
    }

    private void QuickHelpFull_Click(object sender, RoutedEventArgs e)
    {
        _quickHelp.Close(restoreFocus: false);
        OpenHome("help");
    }
}
