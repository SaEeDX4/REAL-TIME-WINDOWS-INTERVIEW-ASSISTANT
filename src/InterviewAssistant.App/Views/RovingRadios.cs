using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace InterviewAssistant.App.Views;

/// <summary>
/// One Tab stop for a group of radio buttons; the arrow keys move the selection (and focus) like a native radio group,
/// mirrored in right-to-left layouts. Used by the export choices and the Interview Focus presets.
/// </summary>
public static class RovingRadios
{
    public static void Attach(Panel panel, Func<IReadOnlyList<RadioButton>> buttons)
    {
        KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Once);
        KeyboardNavigation.SetDirectionalNavigation(panel, KeyboardNavigationMode.Contained);
        panel.PreviewKeyDown += (_, e) =>
        {
            var items = buttons();
            var step = e.Key switch { Key.Down or Key.Right => 1, Key.Up or Key.Left => -1, _ => 0 };
            if (step == 0 || items.Count == 0) return;
            if (panel.FlowDirection == FlowDirection.RightToLeft && e.Key is Key.Left or Key.Right) step = -step;
            var i = Math.Max(0, items.ToList().FindIndex(b => b.IsChecked == true));
            var next = items[(i + step + items.Count) % items.Count];
            next.IsChecked = true;
            next.Focus();
            e.Handled = true;
        };
    }
}
