using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace InterviewAssistant.App.Views;

public enum ToastKind { Success, Info, Warning, Error }

/// <summary>
/// Small, non-blocking in-app notifications: stacked (max 3), auto-dismissed, de-duplicated (the same text within a few
/// seconds is shown once), announced to screen readers, click to dismiss. Hosts never cover the answer text: each
/// window places its host away from critical content.
/// </summary>
public sealed class ToastHost : StackPanel
{
    private readonly Dictionary<string, DateTime> _recent = new();
    public int MaxVisible { get; set; } = 3;

    public ToastHost()
    {
        Orientation = Orientation.Vertical;
        IsHitTestVisible = true;
        VerticalAlignment = VerticalAlignment.Bottom;
        HorizontalAlignment = HorizontalAlignment.Center;
        Margin = new Thickness(12);
    }

    public void Show(string text, ToastKind kind = ToastKind.Info, int? milliseconds = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var now = DateTime.UtcNow;
        foreach (var k in _recent.Where(kv => now - kv.Value > TimeSpan.FromSeconds(4)).Select(kv => kv.Key).ToList()) _recent.Remove(k);
        if (_recent.ContainsKey(text)) return;   // no spam: identical message already on screen
        _recent[text] = now;

        var res = Application.Current.Resources;
        var (glyph, brushKey) = kind switch
        {
            ToastKind.Success => ("", "B.Listening"),
            ToastKind.Warning => ("", "B.Thinking"),
            ToastKind.Error => ("", "B.Error"),
            _ => ("", "B.Accent"),
        };
        var accent = (Brush)res[brushKey];
        var row = new DockPanel();
        var icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)res["F.Icons"], FontSize = 13, Foreground = accent, Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)res["B.Text"], FontSize = 12.5, MaxWidth = 420 });
        var card = new Border
        {
            Child = row, Padding = new Thickness(14, 9, 16, 9), Margin = new Thickness(0, 6, 0, 0), CornerRadius = new CornerRadius(10),
            Background = (Brush)res["B.Surface3"], BorderBrush = accent, BorderThickness = new Thickness(1, 1, 1, 1),
            Opacity = 0, RenderTransform = new TranslateTransform(0, 8), Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Click to dismiss", Effect = res["E.Shadow"] as System.Windows.Media.Effects.Effect,
        };
        AutomationProperties.SetName(card, text);
        AutomationProperties.SetLiveSetting(card, kind == ToastKind.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        card.MouseLeftButtonUp += (_, _) => Dismiss(card);

        while (Children.Count >= MaxVisible) Children.RemoveAt(0);
        Children.Add(card);
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        ((TranslateTransform)card.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(card)?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);

        var duration = milliseconds ?? kind switch { ToastKind.Error => 7000, ToastKind.Warning => 5500, _ => 3200 };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(duration) };
        timer.Tick += (_, _) => { timer.Stop(); Dismiss(card); };
        timer.Start();
    }

    private void Dismiss(Border card)
    {
        if (!Children.Contains(card)) return;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) => Children.Remove(card);
        card.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Currently visible toast texts (self-test).</summary>
    public IReadOnlyList<string> VisibleTexts => Children.OfType<Border>().Select(b => AutomationProperties.GetName(b)).ToList();
}
