namespace InterviewAssistant.App.UiState;

/// <summary>Removal of an account's data on this PC after the account was deleted (plain class, unit-tested).</summary>
public static class LocalData
{
    /// <summary>A folder that could not be removed, and the exception type (never its message, which holds the path).</summary>
    public readonly record struct Failure(string Folder, string Error);

    /// <summary>
    /// Deletes each existing folder (null entries are skipped) and keeps going after a failure. Returns the first folder
    /// that could not be removed, or null when everything is gone — success is reported only when it is true.
    /// </summary>
    public static Failure? Delete(params string?[] folders)
    {
        Failure? first = null;
        foreach (var dir in folders)
        {
            if (string.IsNullOrEmpty(dir)) continue;
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { first ??= new Failure(dir, ex.GetType().Name); }
        }
        return first;
    }
}
