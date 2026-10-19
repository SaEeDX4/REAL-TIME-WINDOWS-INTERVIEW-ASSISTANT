using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace InterviewAssistant.App.Views;

/// <summary>One option of an export/format choice (texts already localized).</summary>
public sealed record ExportChoice(string Key, string Title, string Description);

/// <summary>
/// A short list of mutually exclusive options shown as cards (title + one-line description) inside an in-app dialog.
/// One tab stop; arrow keys move the selection; screen readers read the title and the description of each option.
/// </summary>
public partial class ExportChoiceList : UserControl
{
    private readonly List<(RadioButton Button, ExportChoice Choice)> _items = new();

    public ExportChoiceList(IReadOnlyList<ExportChoice> choices, string groupName, string? selectedKey = null)
    {
        InitializeComponent();
        AutomationProperties.SetName(OptionsGroup, groupName);
        var group = "export-" + Guid.NewGuid().ToString("n");
        foreach (var c in choices)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = c.Title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("B.Text"), TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock { Text = c.Description, FontSize = 12, Foreground = (Brush)FindResource("B.TextDim"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            var b = new RadioButton { Style = (Style)Resources["ChoiceCard"], GroupName = group, Content = text, Tag = c.Key };
            AutomationProperties.SetName(b, c.Title);
            AutomationProperties.SetHelpText(b, c.Description);
            _items.Add((b, c));
            Options.Children.Add(b);
        }
        var initial = _items.FindIndex(i => i.Choice.Key == selectedKey);
        if (_items.Count > 0) _items[Math.Max(0, initial)].Button.IsChecked = true;
        RovingRadios.Attach(Options, () => Buttons);
    }

    public string? SelectedKey => _items.FirstOrDefault(i => i.Button.IsChecked == true).Choice?.Key;

    /// <summary>The checked option's control (initial focus of the dialog).</summary>
    public RadioButton? SelectedButton => _items.FirstOrDefault(i => i.Button.IsChecked == true).Button;

    internal IReadOnlyList<RadioButton> Buttons => _items.Select(i => i.Button).ToList();
}
