using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;

namespace InterviewAssistant.App.Views;

/// <summary>
/// A card or row that keyboard focus can land on. WPF's Border has no automation peer, so a focusable Border with an
/// AutomationProperties.Name is silent in Narrator/NVDA; this one is a UIA Group (or list item) with that name.
/// </summary>
public class AccessibleBorder : Border
{
    public static readonly DependencyProperty IsListItemProperty =
        DependencyProperty.Register(nameof(IsListItem), typeof(bool), typeof(AccessibleBorder), new PropertyMetadata(false));

    /// <summary>A row of a list (the pre-interview checks, the health rows) rather than a stand-alone card.</summary>
    public bool IsListItem { get => (bool)GetValue(IsListItemProperty); set => SetValue(IsListItemProperty, value); }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(AccessibleBorder owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() =>
            owner.IsListItem ? AutomationControlType.ListItem : AutomationControlType.Group;
        protected override string GetClassNameCore() => owner.IsListItem ? "ListItem" : "Group";
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
    }
}

/// <summary>
/// An audio level meter: a UIA progress bar with a read-only 0–100 value (a plain Border is invisible to screen readers).
/// </summary>
public sealed class LevelMeter : Border
{
    public static readonly DependencyProperty LevelProperty =
        DependencyProperty.Register(nameof(Level), typeof(double), typeof(LevelMeter), new PropertyMetadata(0.0));

    /// <summary>Level from 0 to 1 (the same value the bar's width is bound to).</summary>
    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(LevelMeter owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ProgressBar;
        protected override string GetClassNameCore() => "LevelMeter";
        public override object? GetPattern(PatternInterface p) => p == PatternInterface.RangeValue ? this : base.GetPattern(p);
        public double Value => Math.Round(Math.Clamp(owner.Level, 0, 1) * 100);
        public bool IsReadOnly => true;
        public double Maximum => 100;
        public double Minimum => 0;
        public double LargeChange => 10;
        public double SmallChange => 1;
        public void SetValue(double value) => throw new InvalidOperationException("The level meter is read-only.");
    }
}

/// <summary>
/// Live regions that actually speak. <c>AutomationProperties.LiveSetting</c> alone does nothing: UIA clients announce a
/// live region only when the element raises LiveRegionChanged. <c>v:LiveRegion.Mode="Polite"</c> sets the live setting
/// and raises the event whenever the TextBlock's text (or any other element's accessible name) changes to a new,
/// non-empty value — the same value twice is not re-announced.
/// </summary>
public static class LiveRegion
{
    public static readonly DependencyProperty ModeProperty = DependencyProperty.RegisterAttached(
        "Mode", typeof(AutomationLiveSetting), typeof(LiveRegion), new PropertyMetadata(AutomationLiveSetting.Off, OnModeChanged));

    public static AutomationLiveSetting GetMode(DependencyObject d) => (AutomationLiveSetting)d.GetValue(ModeProperty);
    public static void SetMode(DependencyObject d, AutomationLiveSetting v) => d.SetValue(ModeProperty, v);

    // mirrors the announced text through a self-binding: no event subscriptions, nothing to leak
    private static readonly DependencyProperty MirrorProperty = DependencyProperty.RegisterAttached(
        "Mirror", typeof(string), typeof(LiveRegion), new PropertyMetadata(null, OnMirrorChanged));

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        var mode = (AutomationLiveSetting)e.NewValue;
        AutomationProperties.SetLiveSetting(fe, mode);
        if (mode == AutomationLiveSetting.Off) { BindingOperations.ClearBinding(fe, MirrorProperty); return; }
        var path = fe is TextBlock ? new PropertyPath(TextBlock.TextProperty) : new PropertyPath(AutomationProperties.NameProperty);
        BindingOperations.SetBinding(fe, MirrorProperty, new Binding { Source = fe, Path = path, Mode = BindingMode.OneWay });
    }

    private static void OnMirrorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement { IsLoaded: true, IsVisible: true } fe || string.IsNullOrWhiteSpace(e.NewValue as string)) return;
        if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)) return;
        // after layout, so a screen reader reads the new text rather than the old one
        fe.Dispatcher.BeginInvoke(() =>
            (UIElementAutomationPeer.FromElement(fe) ?? UIElementAutomationPeer.CreatePeerForElement(fe))?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged),
            System.Windows.Threading.DispatcherPriority.Background);
    }
}
