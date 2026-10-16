using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace InterviewAssistant.App.Views;

/// <summary>
/// Placeholder text for the shared input templates (Themes/Theme.xaml): <c>v:Watermark.Text="{v:Loc type_hint}"</c>.
/// The template shows it inside the field's content area with the text's own inset, never hit-testable, and only while
/// the field is empty and not focused — so typing always starts at the caret, never "inside" the placeholder.
/// PasswordBox has no bindable Password: <see cref="TrackPasswordProperty"/> (set by the PasswordBox style) keeps
/// <see cref="IsEmptyProperty"/> current without ever copying the password into a string.
/// </summary>
public static class Watermark
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Watermark), new FrameworkPropertyMetadata(""));
    public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string value) => d.SetValue(TextProperty, value);

    public static readonly DependencyProperty IsEmptyProperty = DependencyProperty.RegisterAttached(
        "IsEmpty", typeof(bool), typeof(Watermark), new FrameworkPropertyMetadata(true));
    public static bool GetIsEmpty(DependencyObject d) => (bool)d.GetValue(IsEmptyProperty);
    public static void SetIsEmpty(DependencyObject d, bool value) => d.SetValue(IsEmptyProperty, value);

    public static readonly DependencyProperty TrackPasswordProperty = DependencyProperty.RegisterAttached(
        "TrackPassword", typeof(bool), typeof(Watermark), new FrameworkPropertyMetadata(false, OnTrackPasswordChanged));
    public static bool GetTrackPassword(DependencyObject d) => (bool)d.GetValue(TrackPasswordProperty);
    public static void SetTrackPassword(DependencyObject d, bool value) => d.SetValue(TrackPasswordProperty, value);

    private static void OnTrackPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box) return;
        box.PasswordChanged -= OnPasswordChanged;
        if (e.NewValue is true)
        {
            box.PasswordChanged += OnPasswordChanged;
            Update(box);
        }
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs e) => Update((PasswordBox)sender);

    private static void Update(PasswordBox box)
    {
        using var secure = box.SecurePassword;   // length only; the password itself is never materialized
        SetIsEmpty(box, secure.Length == 0);
    }
}

/// <summary>
/// Field-level validation state for the shared form controls: <c>Field.SetHasError(box, true)</c> turns the outline
/// red (with the focus ring in the error colour) until the user fixes the value. The message itself is shown by an
/// <see cref="InlineMessage"/> next to the field.
/// </summary>
public static class Field
{
    public static readonly DependencyProperty HasErrorProperty = DependencyProperty.RegisterAttached(
        "HasError", typeof(bool), typeof(Field), new FrameworkPropertyMetadata(false));
    public static bool GetHasError(DependencyObject d) => (bool)d.GetValue(HasErrorProperty);
    public static void SetHasError(DependencyObject d, bool value) => d.SetValue(HasErrorProperty, value);

    /// <summary>Clears the error state as soon as the user edits the field again.</summary>
    public static void ClearOnEdit(Control control)
    {
        switch (control)
        {
            case TextBox t: t.TextChanged += (_, _) => SetHasError(t, false); break;
            case PasswordBox p: p.PasswordChanged += (_, _) => SetHasError(p, false); break;
            case ComboBox c: c.SelectionChanged += (_, _) => SetHasError(c, false); break;
        }
    }
}

/// <summary>Live Windows high-contrast state for style triggers (updates when the user switches themes).</summary>
public sealed class HighContrast : INotifyPropertyChanged
{
    public static HighContrast Instance { get; } = new();

    private HighContrast() => SystemParameters.StaticPropertyChanged += (_, e) =>
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOn)));
    };

    public bool IsOn => SystemParameters.HighContrast;
    public event PropertyChangedEventHandler? PropertyChanged;
}
