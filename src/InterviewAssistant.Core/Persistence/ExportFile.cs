namespace InterviewAssistant.Core.Persistence;

/// <summary>
/// Writes an exported file through a temporary file next to the target, moved into place only when it is complete. A
/// failure (disk full, folder not writable) never leaves a truncated file under the chosen name, and the temporary file
/// is removed again. An existing file of that name stays as it was until the new one is complete.
/// </summary>
public static class ExportFile
{
    /// <summary>The temporary file used for <paramref name="path"/> (same folder, so the final move is a rename).</summary>
    public static string TempPathFor(string path) => path + "." + Guid.NewGuid().ToString("n")[..8] + ".tmp";

    public static async Task WriteAsync(string path, Func<Stream, Task> write)
    {
        var tmp = TempPathFor(path);
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await write(fs);
                await fs.FlushAsync();
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    public static Task WriteAsync(string path, Action<Stream> write) => WriteAsync(path, s => { write(s); return Task.CompletedTask; });

    public static Task WriteTextAsync(string path, string text) =>
        WriteAsync(path, async s => { await using var w = new StreamWriter(s, new System.Text.UTF8Encoding(false), leaveOpen: true); await w.WriteAsync(text); });

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort: the export already failed */ }
    }
}
