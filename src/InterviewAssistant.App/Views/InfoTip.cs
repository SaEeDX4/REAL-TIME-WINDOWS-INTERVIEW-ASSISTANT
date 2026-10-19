using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace InterviewAssistant.App.Views;

/// <summary>
/// A small ⓘ button that holds secondary help text instead of a grey paragraph under a field: the text opens as a tool
/// tip on hover, on keyboard focus and on click (click again or Escape closes it). Screen readers get the text as the
/// button's help text, with "More information: <field>" as its name (<see cref="Subject"/>), so a list of ⓘ buttons is
/// not eight identical names. Use it only for secondary hints; warnings stay visible.
/// <c>&lt;v:InfoTip Text="{v:Loc set_end_wait_hint}"/&gt;</c>
/// </summary>
public sealed class InfoTip : Button
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(InfoTip),
        new FrameworkPropertyMetadata("", (d, _) => ((InfoTip)d).Render()));

    /// <summary>The help text (localized).</summary>
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    public static readonly DependencyProperty SubjectProperty = DependencyProperty.Register(nameof(Subject), typeof(string), typeof(InfoTip),
        new FrameworkPropertyMetadata("", (d, _) => ((InfoTip)d).Render()));

    /// <summary>What the tip explains (the field label); part of the accessible name.</summary>
    public string Subject { get => (string)GetValue(SubjectProperty); set => SetValue(SubjectProperty, value); }

    private readonly ToolTip _tip;
    private readonly TextBlock _tipText;

    public InfoTip()
    {
        var res = Application.Current.Resources;
        Style = (Style)res["Btn.Ghost"];
        Padding = new Thickness(4, 2, 4, 2);
        Margin = new Thickness(4, 0, 0, 0);
        VerticalAlignment = VerticalAlignment.Center;
        Focusable = true;
        IsTabStop = true;
        Content = new TextBlock { FontFamily = (FontFamily)res["F.Icons"], Text = "", FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        _tipText = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 340 };
        _tip = new ToolTip { Content = _tipText, PlacementTarget = this, Placement = PlacementMode.Bottom };
        // the tool tip lives in its own popup: it takes the reading direction of the button it explains
        _tip.SetBinding(FlowDirectionProperty, new Binding(nameof(FlowDirection)) { Source = this });
        ToolTip = _tip;
        ToolTipService.SetInitialShowDelay(this, 250);
        ToolTipService.SetShowDuration(this, 60000);
        // re-render on a UI language switch (the name contains the localized "More information")
        System.ComponentModel.PropertyChangedEventManager.AddHandler(LocSource.Instance, (_, _) => Dispatcher.BeginInvoke(Render), "");   // weak: no leak
        Click += (_, _) => _tip.IsOpen = !_tip.IsOpen;
        GotKeyboardFocus += (_, _) => { if (Text.Length > 0) _tip.IsOpen = true; };
        LostKeyboardFocus += (_, _) => _tip.IsOpen = false;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && _tip.IsOpen) { _tip.IsOpen = false; e.Handled = true; } };
        Render();
    }

    private void Render()
    {
        _tipText.Text = Text;
        AutomationProperties.SetHelpText(this, Text);
        var more = Core.Languages.Loc.T("info_more");
        AutomationProperties.SetName(this, string.IsNullOrWhiteSpace(Subject) ? more : $"{more}: {Subject}");
        Visibility = string.IsNullOrEmpty(Text) ? Visibility.Collapsed : Visibility.Visible;
    }
}
