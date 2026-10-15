using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using InterviewAssistant.Core.Languages;

namespace InterviewAssistant.App.Views;

/// <summary>
/// Bindable view of <see cref="Loc"/>: XAML binds to <c>[key]</c>, so changing the UI language refreshes every open
/// window immediately (no restart). Also exposes the layout direction (Persian/Arabic are right-to-left).
/// </summary>
public sealed class LocSource : INotifyPropertyChanged
{
    public static LocSource Instance { get; } = new();
    private LocSource() => Loc.LanguageChanged += () =>
    {
        if (Application.Current?.Dispatcher is { } d && !d.CheckAccess()) d.BeginInvoke(Refresh);
        else Refresh();
    };

    public string this[string key] => Loc.T(key);
    public FlowDirection FlowDirection => Loc.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    /// <summary>Text alignment that follows the reading direction (left in LTR, right in RTL).</summary>
    public TextAlignment TextAlignment => TextAlignment.Left;   // WPF mirrors Left under RightToLeft automatically

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FlowDirection)));
    }
}

/// <summary><c>Text="{v:Loc start_title}"</c> — a live-updating localized string.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }
    public LocExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = LocSource.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}

/// <summary><c>FlowDirection="{v:LocFlow}"</c> — the UI language's reading direction, live.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocFlowExtension : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding(nameof(LocSource.FlowDirection)) { Source = LocSource.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
