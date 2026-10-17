using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using InterviewAssistant.Core.Languages;
using InterviewAssistant.Core.Persistence;

namespace InterviewAssistant.App.Views;

/// <summary>
/// The data export center's in-app dialogs (built on <see cref="AppDialog"/>/<see cref="DialogHost"/>): the format
/// choice for "Export my data…" and the profile "Export…", and the preview before importing a profile backup.
/// </summary>
public static class ExportDialog
{
    public const string AccountHtml = "html", AccountJson = "json";
    public const string ProfileHtml = "profile-html", ProfileBackup = "backup", ProfileRawZip = "raw-zip";

    /// <summary>"Readable report (HTML) — recommended" or "Raw data (JSON) — advanced"; null when cancelled.</summary>
    public static Task<string?> ChooseAccountFormatAsync(Window owner) => ChooseAsync(owner, Loc.T("exp_dlg_account_title"), Loc.T("exp_dlg_account_message"), new[]
    {
        new ExportChoice(AccountHtml, Loc.T("exp_opt_html"), Loc.T("exp_opt_html_desc")),
        new ExportChoice(AccountJson, Loc.T("exp_opt_json"), Loc.T("exp_opt_json_desc")),
    });

    /// <summary>Readable profile (HTML), backup &amp; transfer (.iaprofile) or raw data (ZIP); null when cancelled.</summary>
    public static Task<string?> ChooseProfileFormatAsync(Window owner, string profileName) => ChooseAsync(owner, Loc.T("exp_dlg_profile_title"), Loc.F("exp_dlg_profile_message", profileName), new[]
    {
        new ExportChoice(ProfileHtml, Loc.T("exp_opt_profile_html"), Loc.T("exp_opt_profile_html_desc")),
        new ExportChoice(ProfileBackup, Loc.T("exp_opt_backup"), Loc.T("exp_opt_backup_desc")),
        new ExportChoice(ProfileRawZip, Loc.T("exp_opt_raw_zip"), Loc.T("exp_opt_raw_zip_desc")),
    });

    /// <summary>A choice between export formats: Continue (default, Enter) / Cancel (Escape). The first option is preselected.</summary>
    public static async Task<string?> ChooseAsync(Window owner, string title, string message, IReadOnlyList<ExportChoice> choices)
    {
        var list = new ExportChoiceList(choices, Loc.T("exp_choice_group"));
        var result = await AppDialog.ShowAsync(owner, new DialogRequest
        {
            Title = title, Message = message, Tone = DialogTone.Question, Body = list, MaxWidth = 520, InitialFocus = list.SelectedButton,
            Actions = new[]
            {
                new DialogAction { Text = Loc.T("dlg_cancel"), Result = AppDialogResult.Cancel, IsCancel = true },
                new DialogAction { Text = Loc.T("dlg_continue"), Result = AppDialogResult.Primary, Role = DialogButtonRole.Primary, IsDefault = true },
            },
        });
        return result == AppDialogResult.Primary ? list.SelectedKey : null;
    }

    /// <summary>What a backup contains, before anything is written: Import (default) / Cancel.</summary>
    public static async Task<bool> ConfirmImportAsync(Window owner, ProfileBundlePreview preview)
    {
        var res = Application.Current.Resources;
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var culture = Loc.Culture;
        void Row(string labelKey, string value, bool technical = false)
        {
            var r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = Loc.T(labelKey), Foreground = (Brush)res["B.TextDim"], FontSize = 12.5, Margin = new Thickness(0, 3, 0, 3), TextWrapping = TextWrapping.Wrap };
            var text = new TextBlock { Text = value, Foreground = (Brush)res["B.Text"], FontSize = 13, Margin = new Thickness(0, 3, 0, 3), TextWrapping = TextWrapping.Wrap };
            if (technical) { text.FlowDirection = FlowDirection.LeftToRight; text.HorizontalAlignment = HorizontalAlignment.Left; }
            AutomationProperties.SetLabeledBy(text, label);
            Grid.SetRow(label, r); Grid.SetRow(text, r); Grid.SetColumn(text, 2);
            grid.Children.Add(label); grid.Children.Add(text);
        }
        Row("exp_prev_profile", preview.ProfileName.Length > 0 ? preview.ProfileName : Loc.T("profile_unnamed"));
        Row("exp_prev_facts", preview.Facts.ToString("N0", culture));
        Row("exp_prev_interviews", preview.Interviews.ToString("N0", culture));
        Row("exp_prev_packs", preview.Packs.ToString("N0", culture));
        Row("exp_prev_reports", preview.Reports.ToString("N0", culture));
        if (preview.CreatedUtc > DateTime.MinValue.AddDays(1))
            Row("exp_prev_created", DateTime.SpecifyKind(preview.CreatedUtc, DateTimeKind.Utc).ToLocalTime().ToString("g", culture));
        if (preview.AppVersion.Length > 0) Row("exp_prev_app", preview.AppVersion, technical: true);
        var result = await AppDialog.ShowAsync(owner, new DialogRequest
        {
            Title = Loc.T("exp_import_title"), Message = Loc.T("exp_import_message"), Tone = DialogTone.Question, Body = grid, MaxWidth = 480,
            Actions = new[]
            {
                new DialogAction { Text = Loc.T("dlg_cancel"), Result = AppDialogResult.Cancel, IsCancel = true },
                new DialogAction { Text = Loc.T("exp_import_btn"), Result = AppDialogResult.Primary, Role = DialogButtonRole.Primary, IsDefault = true },
            },
        });
        return result == AppDialogResult.Primary;
    }

    /// <summary>Plain-language reason a backup was refused (never the technical detail).</summary>
    public static string Describe(ProfileBundleError error) => Loc.T(error switch
    {
        ProfileBundleError.NewerVersion => "exp_import_newer",
        ProfileBundleError.TooLarge => "exp_import_too_large",
        _ => "exp_import_invalid",
    });
}
