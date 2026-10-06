using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace InterviewAssistant.App.Views;

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c) => (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class StringToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c) => !string.IsNullOrWhiteSpace(value as string) ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Maps 0..1 to a width fraction for the audio meter.</summary>
public sealed class LevelToWidth : IValueConverter
{
    public double Max { get; set; } = 56;
    public object Convert(object value, Type t, object p, CultureInfo c) => Math.Max(2, (value is double d ? d : 0) * Max);
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class Multiply : IValueConverter
{
    public double Factor { get; set; } = 1.45;
    public object Convert(object value, Type t, object p, CultureInfo c) => (value is double d ? d : 16) * Factor;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
