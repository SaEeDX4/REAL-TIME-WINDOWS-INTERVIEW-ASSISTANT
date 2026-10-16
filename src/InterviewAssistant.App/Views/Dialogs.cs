using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using InterviewAssistant.Core.Domain;
using InterviewAssistant.Core.Languages;

namespace InterviewAssistant.App.Views;

/// <summary>Building blocks for the forms shown inside <see cref="AppDialog"/> (same controls and spacing as the pages).</summary>
internal static class DialogForm
{
    public static TextBlock Label(string text, UIElement target)
    {
        var l = new TextBlock { Text = text, Style = (Style)Application.Current.Resources["T.FieldLabel"] };
        if (target is FrameworkElement fe) AutomationProperties.SetLabeledBy(fe, l);
        return l;
    }

    public static TextBlock Hint(string text) => new()
    {
        Text = text, Foreground = (Brush)Application.Current.Resources["B.TextFaint"], FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
        Margin = (Thickness)Application.Current.Resources["S.HintMargin"],
    };

    /// <summary>E-mail addresses and passwords read left-to-right also in Arabic/Persian.</summary>
    public static PasswordBox Password(string nameKey)
    {
        var box = new PasswordBox { FlowDirection = FlowDirection.LeftToRight };
        AutomationProperties.SetName(box, Loc.T(nameKey));
        Field.ClearOnEdit(box);
        return box;
    }
}

public sealed record FactEditResult(FactKind Kind, string Text, string Company, string Title, string Period, bool Confirmed);

/// <summary>Add or edit one profile fact. The user's own statement is confirmed by default; it can be left "to review".</summary>
public static class FactEditor
{
    private static readonly FactKind[] Kinds = { FactKind.Role, FactKind.Achievement, FactKind.Skill, FactKind.Education, FactKind.Language, FactKind.Certification, FactKind.Project, FactKind.Summary, FactKind.Other };

    public static async Task<FactEditResult?> ShowAsync(Window owner, HomeWindow.FactRow? row)
    {
        var res = Application.Current.Resources;
        var f = row?.Fact;
        var body = new StackPanel();
        var kind = new ComboBox { IsEnabled = f == null };
        foreach (var k in Kinds) kind.Items.Add(new ComboBoxItem { Content = Loc.T("fact_kind_" + k.ToString().ToLowerInvariant()), Tag = k });
        kind.SelectedIndex = Math.Max(0, Array.IndexOf(Kinds, f?.Kind ?? FactKind.Achievement));
        AutomationProperties.SetName(kind, Loc.T("fact_kind"));
        body.Children.Add(DialogForm.Label(Loc.T("fact_kind"), kind)); body.Children.Add(kind);

        var text = new TextBox { Style = (Style)res["Input.Multiline"], MaxLength = 800, Text = f?.Text ?? "", MaxHeight = 180 };
        AutomationProperties.SetName(text, Loc.T("fact_text"));
        Field.ClearOnEdit(text);
        body.Children.Add(DialogForm.Label(Loc.T("fact_text"), text)); body.Children.Add(text);

        var roleGrid = new Grid();
        roleGrid.ColumnDefinitions.Add(new ColumnDefinition());
        roleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = (GridLength)res["S.ColumnGap"] });
        roleGrid.ColumnDefinitions.Add(new ColumnDefinition());
        roleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = (GridLength)res["S.ColumnGap"] });
        roleGrid.ColumnDefinitions.Add(new ColumnDefinition());
        TextBox Field_(int col, string labelKey, string value)
        {
            var sp = new StackPanel();
            var tb = new TextBox { Text = value, MaxLength = 120 };
            AutomationProperties.SetName(tb, Loc.T(labelKey));
            sp.Children.Add(DialogForm.Label(Loc.T(labelKey), tb)); sp.Children.Add(tb);
            Grid.SetColumn(sp, col); roleGrid.Children.Add(sp);
            return tb;
        }
        var company = Field_(0, "fact_company", f?.Company ?? "");
        var title = Field_(2, "fact_title", f?.Title ?? "");
        var period = Field_(4, "fact_period", f?.Period ?? "");
        body.Children.Add(roleGrid);

        var confirm = new CheckBox { Style = (Style)res["Check"], Content = Loc.T("fact_confirm_label"), IsChecked = row?.Confirmed ?? true, Margin = new Thickness(0, 14, 0, 0) };
        body.Children.Add(confirm);
        body.Children.Add(DialogForm.Hint(Loc.T("fact_confirm_hint")));
        var error = new InlineMessage();
        body.Children.Add(error);

        FactEditResult? result = null;
        var choice = await AppDialog.ShowAsync(owner, new DialogRequest
        {
            Title = Loc.T(f == null ? "fact_add_title" : "fact_edit_title"), Tone = DialogTone.Info, Body = body, MaxWidth = 560, InitialFocus = text,
            Actions = new[]
            {
                new DialogAction { Text = Loc.T("dlg_cancel"), Result = AppDialogResult.Cancel, IsCancel = true },
                new DialogAction
                {
                    Text = Loc.T("dlg_save"), Role = DialogButtonRole.Primary, IsDefault = true,
                    Validate = () =>
                    {
                        var k = (FactKind)((ComboBoxItem)kind.SelectedItem).Tag;
                        if (text.Text.Trim().Length < 2 && !(k == FactKind.Role && title.Text.Trim().Length > 0))
                        {
                            error.Show(Loc.T("fact_text_required"), MessageKind.Error);
                            Field.SetHasError(text, true);
                            text.Focus();
                            return Task.FromResult(false);
                        }
                        result = new FactEditResult(k, text.Text.Trim(), company.Text.Trim(), title.Text.Trim(), period.Text.Trim(), confirm.IsChecked == true);
                        return Task.FromResult(true);
                    },
                },
            },
        });
        return choice == AppDialogResult.Primary ? result : null;
    }
}

/// <summary>Password prompts (re-authentication before sensitive actions, choosing a new password).</summary>
public static class Prompt
{
    /// <summary>
    /// Asks for the current password and checks it with <paramref name="verify"/> (null = correct, else the error to show)
    /// while the dialog stays open: a wrong password is reported inside the dialog and the user can retry.
    /// </summary>
    public static async Task<bool> CurrentPasswordAsync(Window owner, string reason, string email, Func<string, Task<string?>> verify)
    {
        var res = Application.Current.Resources;
        var body = new StackPanel();
        if (email.Length > 0) body.Children.Add(new TextBlock { Text = email, Foreground = (Brush)res["B.Text"], FontSize = 12.5, FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Left });
        var box = DialogForm.Password("password_current");
        body.Children.Add(DialogForm.Label(Loc.T("password_current"), box)); body.Children.Add(box);
        var error = new InlineMessage();
        body.Children.Add(error);
        var choice = await AppDialog.ShowAsync(owner, new DialogRequest
        {
            Title = Loc.T("reauth_title"), Message = reason, Tone = DialogTone.Info, Body = body, InitialFocus = box,
            Actions = new[]
            {
                new DialogAction { Text = Loc.T("dlg_cancel"), Result = AppDialogResult.Cancel, IsCancel = true },
                new DialogAction
                {
                    Text = Loc.T("dlg_continue"), Role = DialogButtonRole.Primary, IsDefault = true,
                    Validate = async () =>
                    {
                        error.Clear();
                        if (box.Password.Length == 0) { error.Show(Loc.T("err_password_empty"), MessageKind.Error); Field.SetHasError(box, true); box.Focus(); return false; }
                        var problem = await verify(box.Password);
                        if (problem == null) return true;
                        error.Show(problem, MessageKind.Error);
                        Field.SetHasError(box, true);
                        box.Clear(); box.Focus();
                        return false;
                    },
                },
            },
        });
        return choice == AppDialogResult.Primary;
    }

    /// <summary>New password (twice) with inline validation; null when cancelled.</summary>
    public static async Task<string?> NewPasswordAsync(Window owner)
    {
        var body = new StackPanel();
        var box = DialogForm.Password("password_new");
        var again = DialogForm.Password("confirm_password");
        var error = new InlineMessage();
        body.Children.Add(DialogForm.Label(Loc.T("password_new"), box)); body.Children.Add(box);
        body.Children.Add(DialogForm.Label(Loc.T("confirm_password"), again)); body.Children.Add(again);
        body.Children.Add(DialogForm.Hint(Loc.T("password_rules")));
        body.Children.Add(error);
        string? result = null;
        var choice = await AppDialog.ShowAsync(owner, new DialogRequest
        {
            Title = Loc.T("password_choose_new"), Tone = DialogTone.Info, Body = body, InitialFocus = box,
            Actions = new[]
            {
                new DialogAction { Text = Loc.T("dlg_cancel"), Result = AppDialogResult.Cancel, IsCancel = true },
                new DialogAction
                {
                    Text = Loc.T("set_password"), Role = DialogButtonRole.Primary, IsDefault = true,
                    Validate = () =>
                    {
                        if (box.Password.Length < 8) { error.Show(Loc.T("err_password_short"), MessageKind.Error); Field.SetHasError(box, true); box.Focus(); return Task.FromResult(false); }
                        if (box.Password != again.Password) { error.Show(Loc.T("err_password_mismatch"), MessageKind.Error); Field.SetHasError(again, true); again.Focus(); return Task.FromResult(false); }
                        result = box.Password;
                        return Task.FromResult(true);
                    },
                },
            },
        });
        return choice == AppDialogResult.Primary ? result : null;
    }
}
