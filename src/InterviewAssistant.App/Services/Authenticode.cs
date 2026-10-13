using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace InterviewAssistant.App.Services;

/// <summary>
/// Windows Authenticode checks for the updater: a file counts as signed only when WinVerifyTrust accepts its embedded
/// signature (chain to a trusted root, not revoked, not tampered). The publisher is compared by certificate subject,
/// because short-lived signing certificates (e.g. Azure Artifact Signing) rotate thumbprints between releases.
/// </summary>
public static class Authenticode
{
    /// <summary>The signer subject when <paramref name="path"/> carries a valid, trusted signature; otherwise null.</summary>
    public static string? TrustedSigner(string path)
    {
        if (!File.Exists(path) || !Verify(path)) return null;
        try
        {
#pragma warning disable SYSLIB0057 // reads the signer from a signed PE; there is no loader replacement for this
            using var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return string.IsNullOrWhiteSpace(cert.Subject) ? null : cert.Subject;
        }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    /// <summary>
    /// Extracts <paramref name="exeName"/> from a downloaded Velopack package and returns its trusted signer, or null
    /// when the package is missing, the exe is absent, or its signature is not valid.
    /// </summary>
    public static string? TrustedSignerInPackage(string nupkgPath, string exeName)
    {
        if (!File.Exists(nupkgPath)) return null;
        var tmp = Path.Combine(Path.GetTempPath(), "ia-verify-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tmp);
        try
        {
            using var zip = ZipFile.OpenRead(nupkgPath);
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').StartsWith("lib/", StringComparison.OrdinalIgnoreCase)
                                                        && e.Name.Equals(exeName, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return null;
            var file = Path.Combine(tmp, exeName);
            entry.ExtractToFile(file);
            return TrustedSigner(file);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
        finally { try { Directory.Delete(tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static bool Verify(string path)
    {
        var file = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(file, filePtr, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,                 // WTD_UI_NONE
                fdwRevocationChecks = 1,        // WTD_REVOKE_WHOLECHAIN
                dwUnionChoice = 1,              // WTD_CHOICE_FILE
                pFile = filePtr,
                dwStateAction = 1,              // WTD_STATEACTION_VERIFY
                dwProvFlags = 0x80,             // WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT
            };
            var action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            data.dwStateAction = 2;             // WTD_STATEACTION_CLOSE
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return result == 0;
        }
        finally { Marshal.FreeHGlobal(filePtr); }
    }

    private static Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
