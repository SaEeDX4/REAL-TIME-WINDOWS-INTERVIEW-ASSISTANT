using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using InterviewAssistant.App.Services;
using InterviewAssistant.Core.Domain;
using InterviewAssistant.Core.Languages;

namespace InterviewAssistant.App.Views;

/// <summary>Small owned dialogs in the app's visual style (localized, keyboard friendly, RTL aware).</summary>
internal static class DialogShell
{
    public static Window Create(Window owner, string title, UIElement body, out StackPanel buttons)
    {
        var res = Application.Current.Resources;
        var titleText = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = (Brush)res["B.Text"], Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap };
        buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var panel = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        panel.Children.Add(titleText); panel.Children.Add(body); panel.Children.Add(buttons);
        var border = new Border { Background = (Brush)res["B.Bg"], BorderBrush = (Brush)res["B.Border"], BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Child = panel };
        var w = new Window
        {
            Owner = owner, Title = title, Content = border, SizeToContent = SizeToContent.Height, Width = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.None,
            AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, FlowDirection = Loc.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            FontFamily = (FontFamily)res["F.Ui"],
        };
        border.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) w.DragMove(); };
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.DialogResult = false; };
        return w;
    }

    public static Button AddButton(StackPanel buttons, string text, bool primary, Action onClick)
    {
        var b = new Button { Content = text, Style = (Style)Application.Current.Resources[primary ? "Btn.Primary" : "Btn.Base"], Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(16, 6, 16, 6), IsDefault = primary };
        b.Click += (_, _) => onClick();
        buttons.Children.Add(b);
        return b;
    }

    public static TextBlock Label(string text) => new()
    {
        Text = text, Style = (Style)Application.Current.Resources["T.Label"], Margin = new Thickness(0, 10, 0, 6),
    };
}

public sealed record FactEditResult(FactKind Kind, string Text, string Company, string Title, string Period, bool Confirmed);

/// <summary>Add or edit one profile fact. The user's own statement is confirmed by default; it can be left "to review".</summary>
public static class FactEditor
{
    private static readonly FactKind[] Kinds = { FactKind.Role, FactKind.Achievement, FactKind.Skill, FactKind.Education, FactKind.Language, FactKind.Certification, FactKind.Project, FactKind.Summary, FactKind.Other };

    public static FactEditResult? Show(Window owner, HomeWindow.FactRow? row)
    {
        var res = Application.Current.Resources;
        var f = row?.Fact;
        var body = new StackPanel();
        var kind = new ComboBox { Style = (Style)res["Combo"], IsEnabled = f == null };
        foreach (var k in Kinds) kind.Items.Add(new ComboBoxItem { Content = Loc.T("fact_kind_" + k.ToString().ToLowerInvariant()), Tag = k });
        kind.SelectedIndex = Math.Max(0, Array.IndexOf(Kinds, f?.Kind ?? FactKind.Achievement));
        AutomationProperties.SetName(kind, Loc.T("fact_kind"));
        body.Children.Add(DialogShell.Label(Loc.T("fact_kind"))); body.Children.Add(kind);

        var text = new TextBox { Style = (Style)res["Input"], AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64, MaxLength = 800, Text = f?.Text ?? "" };
        AutomationProperties.SetName(text, Loc.T("fact_text"));
        body.Children.Add(DialogShell.Label(Loc.T("fact_text"))); body.Children.Add(text);

        var roleGrid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        for (int i = 0; i < 3; i++) roleGrid.ColumnDefinitions.Add(new ColumnDefinition());
        TextBox Field(int col, string labelKey, string value)
        {
            var sp = new StackPanel { Margin = new Thickness(col == 0 ? 0 : 6, 0, 0, 0) };
            var tb = new TextBox { Style = (Style)res["Input"], Text = value, MaxLength = 120 };
            AutomationProperties.SetName(tb, Loc.T(labelKey));
            sp.Children.Add(DialogShell.Label(Loc.T(labelKey))); sp.Children.Add(tb);
            Grid.SetColumn(sp, col); roleGrid.Children.Add(sp);
            return tb;
        }
        var company = Field(0, "fact_company", f?.Company ?? "");
        var title = Field(1, "fact_title", f?.Title ?? "");
        var period = Field(2, "fact_period", f?.Period ?? "");
        body.Children.Add(roleGrid);

        var confirm = new CheckBox { Style = (Style)res["Check"], Content = Loc.T("fact_confirm_label"), IsChecked = row?.Confirmed ?? true, Margin = new Thickness(0, 14, 0, 0) };
        body.Children.Add(confirm);
        body.Children.Add(new TextBlock { Text = Loc.T("fact_confirm_hint"), Foreground = (Brush)res["B.TextFaint"], FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        var error = new TextBlock { Foreground = (Brush)res["B.Error"], FontSize = 12, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        body.Children.Add(error);

        var w = DialogShell.Create(owner, Loc.T(f == null ? "fact_add_title" : "fact_edit_title"), body, out var buttons);
        FactEditResult? result = null;
        DialogShell.AddButton(buttons, Loc.T("cancel"), false, () => w.DialogResult = false);
        DialogShell.AddButton(buttons, Loc.T("save"), true, () =>
        {
            var k = (FactKind)((ComboBoxItem)kind.SelectedItem).Tag;
            if (text.Text.Trim().Length < 2 && !(k == FactKind.Role && title.Text.Trim().Length > 0)) { error.Text = Loc.T("fact_text_required"); return; }
            result = new FactEditResult(k, text.Text.Trim(), company.Text.Trim(), title.Text.Trim(), period.Text.Trim(), confirm.IsChecked == true);
            w.DialogResult = true;
        });
        w.Loaded += (_, _) => { text.Focus(); text.CaretIndex = text.Text.Length; };
        return w.ShowDialog() == true ? result : null;
    }
}

/// <summary>Password prompts (re-authentication before sensitive actions, choosing a new password).</summary>
public static class Prompt
{
    public static string? CurrentPassword(Window owner, string reason, string email)
    {
        var res = Application.Current.Resources;
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = reason, Foreground = (Brush)res["B.TextDim"], TextWrapping = TextWrapping.Wrap, FontSize = 12.5 });
        if (email.Length > 0) body.Children.Add(new TextBlock { Text = email, Foreground = (Brush)res["B.Text"], FontSize = 12.5, Margin = new Thickness(0, 8, 0, 0), FlowDirection = FlowDirection.LeftToRight });
        var box = new PasswordBox { Style = (Style)res["PasswordInput"], FlowDirection = FlowDirection.LeftToRight };
        AutomationProperties.SetName(box, Loc.T("password_current"));
        body.Children.Add(DialogShell.Label(Loc.T("password_current"))); body.Children.Add(box);
        var w = DialogShell.Create(owner, Loc.T("reauth_title"), body, out var buttons);
        string? result = null;
        DialogShell.AddButton(buttons, Loc.T("cancel"), false, () => w.DialogResult = false);
        DialogShell.AddButton(buttons, Loc.T("continue"), true, () => { if (box.Password.Length == 0) return; result = box.Password; w.DialogResult = true; });
        w.Loaded += (_, _) => box.Focus();
        return w.ShowDialog() == true ? result : null;
    }

    public static string? NewPassword(Window owner)
    {
        var res = Application.Current.Resources;
        var body = new StackPanel();
        var box = new PasswordBox { Style = (Style)res["PasswordInput"], FlowDirection = FlowDirection.LeftToRight };
        var again = new PasswordBox { Style = (Style)res["PasswordInput"], FlowDirection = FlowDirection.LeftToRight };
        AutomationProperties.SetName(box, Loc.T("password_new")); AutomationProperties.SetName(again, Loc.T("confirm_password"));
        var error = new TextBlock { Foreground = (Brush)res["B.Error"], FontSize = 12, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        body.Children.Add(DialogShell.Label(Loc.T("password_new"))); body.Children.Add(box);
        body.Children.Add(DialogShell.Label(Loc.T("confirm_password"))); body.Children.Add(again);
        body.Children.Add(new TextBlock { Text = Loc.T("password_rules"), Foreground = (Brush)res["B.TextFaint"], FontSize = 11.5, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(error);
        var w = DialogShell.Create(owner, Loc.T("password_choose_new"), body, out var buttons);
        string? result = null;
        DialogShell.AddButton(buttons, Loc.T("cancel"), false, () => w.DialogResult = false);
        DialogShell.AddButton(buttons, Loc.T("set_password"), true, () =>
        {
            if (box.Password.Length < 8) { error.Text = Loc.T("err_password_short"); return; }
            if (box.Password != again.Password) { error.Text = Loc.T("err_password_mismatch"); return; }
            result = box.Password; w.DialogResult = true;
        });
        w.Loaded += (_, _) => box.Focus();
        return w.ShowDialog() == true ? result : null;
    }
}
