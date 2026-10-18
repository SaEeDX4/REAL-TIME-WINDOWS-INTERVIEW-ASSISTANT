using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace InterviewAssistant.App.Views;

/// <summary>
/// The one form-field layout of the app (rc.5 design system): label above the field (with an optional ⓘ info tip and an
/// optional accessory such as "Import file…" at the end of the label row), the field itself, and an optional hint below.
/// Every labelled TextBox / PasswordBox / ComboBox in Home, Settings and the auth card sits in a FormField, so labels,
/// field starts, field widths, spacing and the label ↔ field accessibility link are identical everywhere and mirror in
/// RTL. Field widths come from tokens only: a field stretches to its column, or — <see cref="IsShort"/> — to
/// <c>S.FieldShortWidth</c> at the reading-order start (hotkeys, channel, font size). The template is in Themes/Theme.xaml.
/// </summary>
public sealed class FormField : ContentControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(FormField),
        new PropertyMetadata("", (d, e) => AutomationProperties.SetName(d, e.NewValue as string ?? "")));
    public static readonly DependencyProperty InfoProperty = DependencyProperty.Register(nameof(Info), typeof(string), typeof(FormField), new PropertyMetadata(""));
    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(nameof(Hint), typeof(string), typeof(FormField), new PropertyMetadata(""));
    public static readonly DependencyProperty AccessoryProperty = DependencyProperty.Register(nameof(Accessory), typeof(object), typeof(FormField), new PropertyMetadata(null));
    public static readonly DependencyProperty IsShortProperty = DependencyProperty.Register(nameof(IsShort), typeof(bool), typeof(FormField), new PropertyMetadata(false));

    /// <summary>The field's label (also its accessible name and the "labeled by" of the input inside).</summary>
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    /// <summary>Secondary explanation behind an ⓘ next to the label (empty = no info tip).</summary>
    public string Info { get => (string)GetValue(InfoProperty); set => SetValue(InfoProperty, value); }
    /// <summary>A short line under the field that must stay visible (empty = none).</summary>
    public string Hint { get => (string)GetValue(HintProperty); set => SetValue(HintProperty, value); }
    /// <summary>Optional element at the end of the label row (e.g. a secondary "Import file…" button).</summary>
    public object? Accessory { get => GetValue(AccessoryProperty); set => SetValue(AccessoryProperty, value); }
    /// <summary>The value is short (a hotkey, a channel, a size): the field is <c>S.FieldShortWidth</c> wide, not the full column.</summary>
    public bool IsShort { get => (bool)GetValue(IsShortProperty); set => SetValue(IsShortProperty, value); }

    static FormField()
    {
        FocusableProperty.OverrideMetadata(typeof(FormField), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(FormField), new FrameworkPropertyMetadata(false));
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        LinkLabel();
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        LinkLabel();
    }

    /// <summary>Screen readers announce the label for the input inside (also when the input sits in a small panel with its error).</summary>
    private void LinkLabel()
    {
        if (GetTemplateChild("PART_Label") is not TextBlock label) return;
        foreach (var input in Inputs(Content as DependencyObject))
            if (AutomationProperties.GetLabeledBy(input) == null) AutomationProperties.SetLabeledBy(input, label);
    }

    private static IEnumerable<Control> Inputs(DependencyObject? root)
    {
        if (root == null) yield break;
        if (root is TextBox or PasswordBox or ComboBox or Slider) { yield return (Control)root; yield break; }
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var c in Inputs(child)) yield return c;
    }
}
