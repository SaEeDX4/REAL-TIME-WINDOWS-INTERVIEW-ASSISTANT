using System.IO.Compression;
using System.Text;

namespace InterviewAssistant.Core.Ingestion;

public enum UploadType { Pdf, Docx, Text }

public sealed record UploadCheck(bool Ok, UploadType Type, string SafeFileName, string? Error);

/// <summary>Hook for anti-malware scanning (e.g. Windows AMSI on desktop, ClamAV on server). Default: allow.</summary>
public interface IUploadScanner
{
    bool IsClean(string safeFileName, byte[] content, out string? reason);
}

public sealed class AllowAllScanner : IUploadScanner
{
    public bool IsClean(string safeFileName, byte[] content, out string? reason) { reason = null; return true; }
}

/// <summary>
/// Validates untrusted uploads: size cap, extension allow-list, magic bytes, DOCX structure, text encoding,
/// and file-name sanitisation (the client file name is never used as a path).
/// </summary>
public static class UploadValidator
{
    public const int MaxBytes = 10 * 1024 * 1024;

    public static string SanitizeFileName(string? name)
    {
        var n = Path.GetFileName((name ?? "").Replace('\\', '/').Split('/').Last());
        var sb = new StringBuilder();
        foreach (var c in n) sb.Append(char.IsControl(c) || Path.GetInvalidFileNameChars().Contains(c) || c is ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c);
        var s = sb.ToString().Trim().TrimStart('.');
        if (s.Length == 0) s = "document";
        return s.Length > 120 ? s[..120] : s;
    }

    public static UploadCheck Validate(string? fileName, byte[] content, IUploadScanner? scanner = null)
    {
        var safe = SanitizeFileName(fileName);
        if (content.Length == 0) return new(false, UploadType.Text, safe, "File is empty.");
        if (content.Length > MaxBytes) return new(false, UploadType.Text, safe, $"File is larger than {MaxBytes / 1024 / 1024} MB.");
        var ext = Path.GetExtension(safe).ToLowerInvariant();
        UploadType type;
        switch (ext)
        {
            case ".pdf":
                if (!(content.Length > 5 && Encoding.ASCII.GetString(content, 0, 5) == "%PDF-")) return new(false, UploadType.Pdf, safe, "Not a valid PDF file.");
                type = UploadType.Pdf; break;
            case ".docx":
                if (!(content.Length > 4 && content[0] == 'P' && content[1] == 'K' && content[2] == 3 && content[3] == 4)) return new(false, UploadType.Docx, safe, "Not a valid DOCX file.");
                try
                {
                    using var zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
                    if (zip.GetEntry("word/document.xml") == null) return new(false, UploadType.Docx, safe, "DOCX has no document body.");
                    if (zip.Entries.Count > 2000 || zip.Entries.Sum(e => e.Length) > 200L * 1024 * 1024) return new(false, UploadType.Docx, safe, "DOCX is too large when unpacked.");
                }
                catch (InvalidDataException) { return new(false, UploadType.Docx, safe, "DOCX is corrupted."); }
                type = UploadType.Docx; break;
            case ".txt": case ".md":
                if (content.Contains((byte)0)) return new(false, UploadType.Text, safe, "Text file contains binary data.");
                try { new UTF8Encoding(false, true).GetString(content); }
                catch (DecoderFallbackException) { return new(false, UploadType.Text, safe, "Text file must be UTF-8."); }
                type = UploadType.Text; break;
            default:
                return new(false, UploadType.Text, safe, "Unsupported file type. Use PDF, DOCX or TXT.");
        }
        if (scanner != null && !scanner.IsClean(safe, content, out var why)) return new(false, type, safe, "Blocked by security scan: " + why);
        return new(true, type, safe, null);
    }
}
