using System.Globalization;
using System.Text.Json;
using System.Windows;
using InterviewAssistant.App.Services;
using InterviewAssistant.App.UiState;
using InterviewAssistant.App.ViewModels;
using InterviewAssistant.App.Views;
using InterviewAssistant.Client;
using InterviewAssistant.Core.Domain;
using InterviewAssistant.Core.Languages;
using InterviewAssistant.Core.Persistence;
using InterviewAssistant.Core.Reports;
using Microsoft.Win32;

namespace InterviewAssistant.App;

/// <summary>
/// Data export center: "Export my data…" (Account), the profile "Export…" and "Import backup…" (Profiles). Every
/// export goes through <see cref="SecretFilter"/>; a backup is imported only after validation and a preview, always as
/// a new profile.
/// </summary>
public partial class HomeWindow
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private static string FileStamp() => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>File-name part from a user-chosen name: letters/digits kept (any script), everything else becomes "-".</summary>
    private static string FileSlug(string name)
    {
        var chars = name.Trim().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = string.Join("-", new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "profile" : slug.Length > 40 ? slug[..40] : slug;
    }

    private async Task ExportAccountDataAsync()
    {
        if (_vm.Cloud.Api == null) return;
        var format = await ExportDialog.ChooseAccountFormatAsync(this);
        if (format == null || _vm.Cloud.Api == null) return;
        if (!await ReauthenticateAsync("reauth_reason_export", PrivacyMessage)) return;
        var html = format == ExportDialog.AccountHtml;
        var dlg = new SaveFileDialog
        {
            FileName = $"interview-assistant-account-{FileStamp()}{(html ? ".html" : ".json")}",
            Filter = html ? T("file_filter_html") + " (*.html)|*.html" : T("file_filter_json") + " (*.json)|*.json",
        };
        if (dlg.ShowDialog(this) != true || _vm.Cloud.Api == null) return;
        JsonElement export;
        try { export = await _vm.Cloud.Api.ExportAsync(CancellationToken.None); }
        catch (BackendException ex)
        {
            AppLog.Warn($"Export failed: {ex.Code} (ref {ex.CorrelationId})");
            PrivacyMessage.Show(CloudService.Describe(ex, includeReference: false), MessageKind.Error);
            return;
        }
        // Raw JSON stays the service's format, minus anything that looks like a credential.
        var clean = SecretFilter.Clean(export);
        string content;
        if (html)
        {
            var account = _vm.Cloud.Account;
            var context = account == null ? null : new AccountExportContext(account.Entitlements.PlanName, MainViewModel.BillingLabel(account));
            content = AccountExportRenderer.Render(clean, Loc.Language, Branding.ProductName, context);
        }
        else content = JsonSerializer.Serialize(clean, IndentedJson);
        try { await File.WriteAllTextAsync(dlg.FileName, content); }
        catch (Exception ex) when (IsStorageError(ex))
        {
            AppLog.Error("Account export could not be saved", ex);
            PrivacyMessage.Show(T("exp_err_save"), MessageKind.Error);
            return;
        }
        PrivacyMessage.Clear();
        Notify(T("toast_account_exported"), ToastKind.Success);
    }

    private async Task ExportProfileAsync()
    {
        if (_profile == null || Store == null) return;
        // The export reads the saved profile: pending edits are saved (or discarded) first.
        if (!await _nav.CanProceedAsync(ChangeReason.ReloadData, _profileGuard) || _profile == null || Store == null) return;
        var name = _profile.PreferredName.Length > 0 ? _profile.PreferredName : _profile.Name.Length > 0 ? _profile.Name : T("profile_unnamed");
        var format = await ExportDialog.ChooseProfileFormatAsync(this, name);
        if (format == null || _profile == null || Store == null) return;
        var (ext, filter) = format switch
        {
            ExportDialog.ProfileHtml => (".html", T("file_filter_html") + " (*.html)|*.html"),
            ExportDialog.ProfileBackup => (ProfileBundle.FileExtension, T("exp_file_filter_backup") + $" (*{ProfileBundle.FileExtension})|*{ProfileBundle.FileExtension}"),
            _ => (".zip", T("file_filter_zip") + " (*.zip)|*.zip"),
        };
        var suffix = format == ExportDialog.ProfileRawZip ? "-raw-data" : format == ExportDialog.ProfileBackup ? "-backup" : "";
        var dlg = new SaveFileDialog { FileName = $"{FileSlug(name)}{suffix}-{FileStamp()}{ext}", Filter = filter };
        if (dlg.ShowDialog(this) != true || _profile == null || Store == null) return;
        var profileId = _profile.Id;
        try
        {
            switch (format)
            {
                case ExportDialog.ProfileHtml:
                    var saved = Store.GetProfile(profileId) ?? _profile;
                    await File.WriteAllTextAsync(dlg.FileName, ProfileExportRenderer.Render(saved, Loc.Language, Branding.ProductName));
                    break;
                case ExportDialog.ProfileBackup:
                    var store = Store;
                    await Task.Run(() => { using var fs = File.Create(dlg.FileName); ProfileBundle.Write(store, profileId, fs, BuildInfo.Version); });
                    break;
                default:
                    using (var fs = File.Create(dlg.FileName)) Store.ExportProfile(profileId, fs);
                    break;
            }
        }
        catch (Exception ex) when (IsStorageError(ex) || ex is KeyNotFoundException)
        {
            AppLog.Error("Profile export failed", ex);
            Notify(T("err_profile_export"), ToastKind.Error);
            return;
        }
        Notify(T(format == ExportDialog.ProfileBackup ? "exp_toast_backup_saved" : "toast_profile_exported"), ToastKind.Success);
    }

    private async void ImportProfileBackup_Click(object sender, RoutedEventArgs e) => await ImportProfileBackupAsync();

    private async Task ImportProfileBackupAsync()
    {
        if (Store == null) return;
        if (!await _nav.CanProceedAsync(ChangeReason.SwitchItem, _profileGuard, _targetGuard) || await ProfileLimitReachedAsync() || Store == null) return;
        var dlg = new OpenFileDialog
        {
            Title = T("exp_import_title"), CheckFileExists = true,
            Filter = T("exp_file_filter_backup") + $" (*{ProfileBundle.FileExtension})|*{ProfileBundle.FileExtension}",
        };
        if (dlg.ShowDialog(this) != true || Store == null) return;

        ProfileBundleContent content;
        try
        {
            if (new FileInfo(dlg.FileName).Length > ProfileBundleLimits.Default.MaxFileBytes) throw new ProfileBundleException(ProfileBundleError.TooLarge, "file");
            content = await Task.Run(() => { using var fs = File.OpenRead(dlg.FileName); return ProfileBundle.Read(fs); });
        }
        catch (ProfileBundleException ex)
        {
            AppLog.Warn($"Profile backup rejected: {ex.Error}");
            await AlertAsync(ExportDialog.Describe(ex.Error), DialogTone.Warning);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Profile backup could not be read ({ex.GetType().Name})");
            await AlertAsync(T("err_file_read"), DialogTone.Warning);
            return;
        }

        if (!await ExportDialog.ConfirmImportAsync(this, content.Preview) || Store == null) return;
        if (await ProfileLimitReachedAsync() || Store == null) return;   // the plan may have changed while the preview was open
        var original = content.Preview.ProfileName;
        var name = original.Length > 0 && Store.Profiles().Any(p => string.Equals(p.Name, original, StringComparison.CurrentCultureIgnoreCase)) ? F("exp_imported_name", original) : null;
        CandidateProfileRecord imported;
        try { imported = ProfileBundle.Import(Store, content, name); }
        catch (Exception ex) when (IsStorageError(ex) || ex is ProfileBundleException or ArgumentException)
        {
            AppLog.Error("Profile backup import failed", ex);
            await AlertAsync(T("exp_import_failed"), DialogTone.Warning);
            return;
        }
        AppLog.Info($"Profile backup imported: {content.Preview.Interviews} interviews, {content.Preview.Packs} packs, {content.Preview.Reports} reports");
        RefreshProfiles(imported.Id, reload: true);
        RefreshReports();
        ShowPage("profiles");
        RefreshChecklist();
        Notify(T("exp_toast_imported"), ToastKind.Success, F("exp_toast_imported_detail", content.Preview.Interviews, content.Preview.Reports));
    }
}
