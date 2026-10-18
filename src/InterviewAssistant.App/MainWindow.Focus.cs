using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using InterviewAssistant.App.Services;
using InterviewAssistant.App.UiState;
using InterviewAssistant.App.Views;
using InterviewAssistant.Core.Languages;

namespace InterviewAssistant.App;

/// <summary>
/// Interview Focus (rc.5, More → Interview Focus…): sizes and places the Live window close to the webcam so the user can
/// glance at notes with a natural eye line. Geometry: <see cref="FocusPlacement"/> on the work area of the monitor the
/// window is on (multi-monitor and DPI aware). The first use explains honestly what it does — and that it does not hide
/// the window from screen sharing. The chosen preset is remembered; "Restore previous size" returns to the old placement.
/// </summary>
public partial class MainWindow
{
    private async void MoreFocus_Click(object sender, RoutedEventArgs e)
    {
        CloseMore();
        await ShowFocusDialogAsync();
    }

    private void MoreFocusRestore_Click(object sender, RoutedEventArgs e)
    {
        CloseMore();
        RestoreFocusPlacement();
    }

    /// <summary>True while the window is placed by Interview Focus (a previous placement can be restored).</summary>
    internal bool HasFocusRestore => _vm.Settings.FocusRestoreWidth is > 0 && _vm.Settings.FocusRestoreHeight is > 0;

    /// <summary>The preset dialog (first time: the "Keep your eye line natural" explanation). Null = cancelled.</summary>
    internal async Task<FocusPreset?> ShowFocusDialogAsync()
    {
        var s = _vm.Settings;
        var first = !s.InterviewFocusIntroSeen;
        var current = FocusPlacement.Parse(s.InterviewFocusPreset);
        var group = "focus-" + Guid.NewGuid().ToString("n");
        var options = new StackPanel();
        KeyboardNavigation.SetDirectionalNavigation(options, KeyboardNavigationMode.Cycle);
        AutomationProperties.SetName(options, Loc.T("focus_position"));
        var radios = new Dictionary<FocusPreset, RadioButton>();
        foreach (var preset in Enum.GetValues<FocusPreset>())
        {
            var rb = new RadioButton { Content = Loc.T(FocusPlacement.Key(preset)), GroupName = group, IsChecked = preset == current, Margin = new Thickness(0, 0, 0, 2) };
            rb.SetResourceReference(StyleProperty, "Seg.Item");
            radios[preset] = rb;
            options.Children.Add(rb);
        }
        var track = new Border { Child = options };
        track.SetResourceReference(StyleProperty, "Seg.Track");
        var label = new TextBlock { Text = Loc.T("focus_position") };
        label.SetResourceReference(StyleProperty, "T.FieldLabel");
        var body = new StackPanel();
        body.Children.Add(label);
        body.Children.Add(track);

        var actions = new List<DialogAction> { new() { Text = Loc.T("dlg_cancel"), Result = AppDialogResult.Cancel, IsCancel = true } };
        if (HasFocusRestore) actions.Add(new DialogAction { Text = Loc.T("focus_restore"), Result = AppDialogResult.Secondary });
        actions.Add(new DialogAction { Text = Loc.T("focus_apply"), Result = AppDialogResult.Primary, Role = DialogButtonRole.Primary, IsDefault = true });

        var result = await AppDialog.ShowAsync(this, new DialogRequest
        {
            Title = Loc.T(first ? "focus_title_first" : "focus_title"),
            Message = Loc.T(first ? "focus_body_first" : "focus_body"),
            Tone = DialogTone.Info,
            Body = body,
            Actions = actions,
            InitialFocus = radios[current],
            MaxWidth = 480,
        });
        if (first) { s.InterviewFocusIntroSeen = true; s.Save(); }   // explained once, whatever was chosen
        if (result == AppDialogResult.Secondary) { RestoreFocusPlacement(); return null; }
        if (result != AppDialogResult.Primary) return null;
        var chosen = radios.First(kv => kv.Value.IsChecked == true).Key;
        ApplyFocus(chosen);
        return chosen;
    }

    /// <summary>Sizes and places the window for <paramref name="preset"/> on its current monitor; remembers what to restore.</summary>
    internal void ApplyFocus(FocusPreset preset)
    {
        var s = _vm.Settings;
        if (!HasFocusRestore)
        {
            var before = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            if (!before.IsEmpty) { s.FocusRestoreLeft = before.Left; s.FocusRestoreTop = before.Top; s.FocusRestoreWidth = before.Width; s.FocusRestoreHeight = before.Height; }
        }
        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        var placed = WindowPlacement.PlaceOnCurrentMonitor(this, work =>
        {
            var r = FocusPlacement.Compute(new PlacementRect(work.Left, work.Top, work.Width, work.Height), preset, MinWidth, MinHeight);
            return new Rect(r.Left, r.Top, r.Width, r.Height);
        });
        if (!placed) AppLog.Warn("Interview Focus: the window has no monitor yet");
        s.InterviewFocusPreset = preset.ToString();
        s.Save();
        AppLog.Info("Interview Focus: " + preset);
        UpdateFocusMenu();
    }

    /// <summary>Back to the placement before Interview Focus (if that spot is still on a connected monitor).</summary>
    internal void RestoreFocusPlacement()
    {
        var s = _vm.Settings;
        if (!HasFocusRestore) return;
        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        Width = Math.Max(MinWidth, s.FocusRestoreWidth!.Value);
        Height = Math.Max(MinHeight, s.FocusRestoreHeight!.Value);
        if (s.FocusRestoreLeft is double l && s.FocusRestoreTop is double t && WindowPlacement.IsVisibleOnAnyMonitor(l, t, Width)) { Left = l; Top = t; }
        s.FocusRestoreLeft = s.FocusRestoreTop = s.FocusRestoreWidth = s.FocusRestoreHeight = null;
        s.Save();
        UpdateFocusMenu();
    }

    private void UpdateFocusMenu() => MoreFocusRestore.Visibility = HasFocusRestore ? Visibility.Visible : Visibility.Collapsed;
}
