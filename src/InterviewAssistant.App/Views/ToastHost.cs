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
/// seconds is shown once), announced to screen readers (UIA notification), click or Escape to dismiss. Hosts never
/// cover the answer text: each window places its host away from critical content.
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
        Loaded += (_, _) =>
        {
            // Escape dismisses the newest toast (only when nothing else in the window handled the key first).
            if (_window != null || Window.GetWindow(this) is not { } w) return;
            _window = w;
            w.KeyDown += (_, e) =>
            {
                if (e.Key != System.Windows.Input.Key.Escape || e.Handled || Children.Count == 0) return;
                if (Children.OfType<Border>().LastOrDefault(b => b.Tag is not Dismissing) is { } newest) { Dismiss(newest); e.Handled = true; }
            };
        };
    }

    private Window? _window;

    public void Show(string text, ToastKind kind = ToastKind.Info, int? milliseconds = null) => Show(text, null, kind, milliseconds);

    /// <summary>Title line plus optional secondary text (e.g. "Connection lost" / "Prepared answers are still available").</summary>
    public void Show(string text, string? detail, ToastKind kind = ToastKind.Info, int? milliseconds = null)
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
        // Accent strip + icon chip: readable at a glance, never louder than the answer itself.
        var chip = new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(0x26, ((SolidColorBrush)accent).Color.R, ((SolidColorBrush)accent).Color.G, ((SolidColorBrush)accent).Color.B)),
            Child = new TextBlock { Text = glyph, FontFamily = (FontFamily)res["F.Icons"], FontSize = 11.5, Foreground = accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        DockPanel.SetDock(chip, Dock.Left);
        row.Children.Add(chip);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)res["B.Text"], FontSize = 12.5, FontWeight = FontWeights.SemiBold, MaxWidth = 380 };
        texts.Children.Add(title);
        if (!string.IsNullOrWhiteSpace(detail))
            texts.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)res["B.TextDim"], FontSize = 11.5, MaxWidth = 380, Margin = new Thickness(0, 2, 0, 0) });
        row.Children.Add(texts);
        var card = new Border
        {
            Child = row, Padding = new Thickness(12, 9, 16, 10), Margin = new Thickness(0, 6, 0, 0), CornerRadius = new CornerRadius(10),
            Background = (Brush)res["B.Surface3"], BorderBrush = (Brush)res["B.Border"], BorderThickness = new Thickness(1),
            Opacity = 0, RenderTransform = new TranslateTransform(0, 8), Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = InterviewAssistant.Core.Languages.Loc.T("toast_dismiss"), Effect = res["E.Shadow"] as System.Windows.Media.Effects.Effect,
        };
        var announcement = string.IsNullOrWhiteSpace(detail) ? text : text + ". " + detail;
        AutomationProperties.SetName(card, announcement);
        var live = kind == ToastKind.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite;
        AutomationProperties.SetLiveSetting(card, live);
        AutomationProperties.SetLiveSetting(title, live);
        card.MouseLeftButtonUp += (_, _) => Dismiss(card);
        // A Border has no automation peer of its own, so the announcement goes through the title TextBlock once it is in the tree.
        title.Loaded += (_, _) => Announce(title, announcement);

        while (Children.Count >= MaxVisible) Children.RemoveAt(0);
        Children.Add(card);
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        ((TranslateTransform)card.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        var duration = milliseconds ?? kind switch { ToastKind.Error => 7000, ToastKind.Warning => 5500, _ => detail == null ? 3200 : 4500 };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(duration) };
        timer.Tick += (_, _) => { timer.Stop(); Dismiss(card); };
        timer.Start();
    }

    private static void Announce(TextBlock title, string announcement)
    {
        if (System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(title) is not { } peer) return;
        peer.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
        peer.RaiseNotificationEvent(AutomationNotificationKind.Other, AutomationNotificationProcessing.ImportantMostRecent, announcement, "toast");
    }

    private void Dismiss(Border card)
    {
        if (!Children.Contains(card) || card.Tag is Dismissing) return;
        card.Tag = Dismissing.Instance;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) => Children.Remove(card);
        card.BeginAnimation(OpacityProperty, fade);
    }

    private sealed class Dismissing { public static readonly Dismissing Instance = new(); }

    /// <summary>Currently visible toast texts (self-test).</summary>
    public IReadOnlyList<string> VisibleTexts => Children.OfType<Border>().Select(b => AutomationProperties.GetName(b)).ToList();
}
