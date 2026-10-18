using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using InterviewAssistant.Core.Languages;

namespace InterviewAssistant.App.Views;

/// <summary>
/// Account deletion, step 1 (in-app dialog on <see cref="DialogHost"/>): what will be deleted, what happens to the
/// subscription and to billing records, linked sign-in providers — "Keep account" is the default and Escape; "Delete
/// permanently" is the red action. Step 2 is the re-authentication; after the deletion a separate question decides about
/// the data stored on this PC (<see cref="AskDeleteLocalDataAsync"/>).
/// </summary>
public static class DeleteAccountDialog
{
    /// <param name="subscriptionBlocks">The subscription can still charge (the service would refuse): its note is a warning.</param>
    public static async Task<bool> ConfirmAsync(Window owner, bool subscriptionBlocks)
    {
        var res = Application.Current.Resources;
        var body = new StackPanel();
        var heading = new TextBlock { Text = Loc.T("del_will_be_deleted"), Style = (Style)res["T.Label"], Margin = new Thickness(0, 4, 0, 6) };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        body.Children.Add(heading);
        foreach (var key in new[] { "del_item_account", "del_item_plan", "del_item_devices", "del_item_service_data" })
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var bullet = new TextBlock { Text = "", FontFamily = (FontFamily)res["F.Icons"], FontSize = 11, Foreground = (Brush)res["B.Error"], Margin = new Thickness(0, 3, 10, 0), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(bullet, Dock.Left);
            row.Children.Add(bullet);
            row.Children.Add(new TextBlock { Text = Loc.T(key), TextWrapping = TextWrapping.Wrap, Foreground = (Brush)res["B.Text"], FontSize = 13 });
            body.Children.Add(row);
        }
        var subscription = new InlineMessage { Margin = new Thickness(0, 12, 0, 0) };
        subscription.Show(Loc.T(subscriptionBlocks ? "del_note_subscription_blocks" : "del_note_subscription"), subscriptionBlocks ? MessageKind.Warning : MessageKind.Info);
        body.Children.Add(subscription);
        foreach (var key in new[] { "del_note_paddle", "del_note_providers", "del_note_local" }) body.Children.Add(DialogForm.Hint(Loc.T(key)));

        var result = await AppDialog.ShowAsync(owner, new DialogRequest
        {
            Title = Loc.T("del_title"), Message = Loc.T("del_irreversible"), Tone = DialogTone.Danger, Body = body, MaxWidth = 520,
            Actions = new[]
            {
                new DialogAction { Text = Loc.T("del_keep"), Result = AppDialogResult.Cancel, Role = DialogButtonRole.Secondary, IsCancel = true, IsDefault = true },
                new DialogAction { Text = Loc.T("del_confirm"), Result = AppDialogResult.Primary, Role = DialogButtonRole.Destructive },
            },
        });
        return result == AppDialogResult.Primary;
    }

    /// <summary>After the deletion: keep (default, Escape) or delete this PC's copy of the account's profiles, interviews and reports.</summary>
    public static async Task<bool> AskDeleteLocalDataAsync(Window owner)
    {
        var result = await AppDialog.ShowAsync(owner, new DialogRequest
        {
            Title = Loc.T("del_local_title"), Message = Loc.T("del_local_message"), Tone = DialogTone.Question,
            Actions = new[]
            {
                new DialogAction { Text = Loc.T("del_local_keep"), Result = AppDialogResult.Cancel, Role = DialogButtonRole.Secondary, IsCancel = true, IsDefault = true },
                new DialogAction { Text = Loc.T("del_local_delete"), Result = AppDialogResult.Primary, Role = DialogButtonRole.Destructive },
            },
        });
        return result == AppDialogResult.Primary;
    }
}
