using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace InterviewAssistant.App.Views;

public enum MessageKind { Error, Warning, Info, Success }

/// <summary>
/// The shared inline feedback component: icon + message (+ optional secondary line and a text action such as "Try
/// again"), placed right where the problem is — under a form field, inside an Account section, in a dialog. Hidden while
/// it has no text. Errors are announced assertively to screen readers, everything else politely. Replaces raw red text.
/// </summary>
public sealed class InlineMessage : Border
{
    private readonly TextBlock _icon, _text, _detail;
    private readonly Button _action;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(InlineMessage),
        new FrameworkPropertyMetadata("", (d, _) => ((InlineMessage)d).Render()));
    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(nameof(Detail), typeof(string), typeof(InlineMessage),
        new FrameworkPropertyMetadata("", (d, _) => ((InlineMessage)d).Render()));
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(MessageKind), typeof(InlineMessage),
        new FrameworkPropertyMetadata(MessageKind.Error, (d, _) => ((InlineMessage)d).Render()));
    public static readonly DependencyProperty ActionTextProperty = DependencyProperty.Register(nameof(ActionText), typeof(string), typeof(InlineMessage),
        new FrameworkPropertyMetadata("", (d, _) => ((InlineMessage)d).Render()));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string Detail { get => (string)GetValue(DetailProperty); set => SetValue(DetailProperty, value); }
    public MessageKind Kind { get => (MessageKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    /// <summary>Label of the optional action (e.g. "Try again"); the action is hidden while empty.</summary>
    public string ActionText { get => (string)GetValue(ActionTextProperty); set => SetValue(ActionTextProperty, value); }
    public event RoutedEventHandler? ActionClick;

    public InlineMessage()
    {
        var res = Application.Current.Resources;
        CornerRadius = (CornerRadius)res["R.Control"];
        BorderThickness = new Thickness(1);
        Padding = new Thickness(10, 7, 10, 7);
        Margin = new Thickness(0, 8, 0, 0);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _icon = new TextBlock { FontFamily = (FontFamily)res["F.Icons"], FontSize = 13, Margin = new Thickness(0, 2, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = (Brush)res["B.Text"] };
        _detail = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Foreground = (Brush)res["B.TextDim"], Margin = new Thickness(0, 2, 0, 0) };
        texts.Children.Add(_text); texts.Children.Add(_detail);
        _action = new Button { Style = (Style)res["Btn.Link"], Margin = new Thickness(8, -2, -4, -2), VerticalAlignment = VerticalAlignment.Center };
        _action.Click += (s, e) => ActionClick?.Invoke(this, e);
        Grid.SetColumn(texts, 1); Grid.SetColumn(_action, 2);
        grid.Children.Add(_icon); grid.Children.Add(texts); grid.Children.Add(_action);
        Child = grid;
        Render();
    }

    /// <summary>Shows a message (convenience for code).</summary>
    public void Show(string text, MessageKind kind, string? detail = null, string? actionText = null)
    {
        Kind = kind; Detail = detail ?? ""; ActionText = actionText ?? ""; Text = text;
    }

    public void Clear() { Text = ""; Detail = ""; ActionText = ""; }

    private void Render()
    {
        if (_text == null) return;
        var res = Application.Current.Resources;
        var (glyph, key) = Kind switch
        {
            MessageKind.Error => ("", "B.Error"),
            MessageKind.Warning => ("", "B.Thinking"),
            MessageKind.Success => ("", "B.Listening"),
            _ => ("", "B.Accent"),
        };
        var accent = ((SolidColorBrush)res[key]).Color;
        _icon.Text = glyph;
        _icon.Foreground = new SolidColorBrush(accent);
        Background = new SolidColorBrush(Color.FromArgb(0x1F, accent.R, accent.G, accent.B));
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, accent.R, accent.G, accent.B));
        _text.Text = Text ?? "";
        _detail.Text = Detail ?? "";
        _detail.Visibility = string.IsNullOrEmpty(Detail) ? Visibility.Collapsed : Visibility.Visible;
        _action.Content = ActionText;
        _action.Visibility = string.IsNullOrEmpty(ActionText) ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(_action, ActionText ?? "");
        var visible = !string.IsNullOrWhiteSpace(Text);
        Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        var announcement = string.IsNullOrEmpty(Detail) ? Text : Text + ". " + Detail;
        AutomationProperties.SetName(_text, announcement ?? "");
        AutomationProperties.SetLiveSetting(_text, Kind == MessageKind.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        if (visible && IsLoaded && UIElementAutomationPeer.FromElement(_text) is { } peer)
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
