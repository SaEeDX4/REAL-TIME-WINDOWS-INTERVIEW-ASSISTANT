using System.IO.Compression;
using System.Text.Json;
using InterviewAssistant.Core.Domain;

namespace InterviewAssistant.Core.Persistence;

/// <summary>Encrypts data at rest. Desktop uses Windows DPAPI; tests use <see cref="NoProtection"/>.</summary>
public interface IDataProtector
{
    byte[] Protect(byte[] plain);
    byte[] Unprotect(byte[] cipher);
}

public sealed class NoProtection : IDataProtector
{
    public byte[] Protect(byte[] plain) => plain;
    public byte[] Unprotect(byte[] cipher) => cipher;
}

/// <summary>
/// Local multi-profile workspace (offline-first). Layout:
///   profiles/{profileId}.bin, targets/{targetId}.bin, packs/{targetId}.bin, sessions/{sessionId}.bin
/// Every file is JSON encrypted with the supplied protector. Ids are validated (hex) so file names can never
/// be influenced by user input (no path traversal).
/// </summary>
public sealed class WorkspaceStore
{
    private readonly string _root;
    private readonly IDataProtector _protector;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly object _lock = new();

    public WorkspaceStore(string root, IDataProtector protector)
    {
        _root = root;
        _protector = protector;
        foreach (var d in new[] { "profiles", "targets", "packs", "sessions" }) Directory.CreateDirectory(Path.Combine(root, d));
    }

    public string Root => _root;

    /// <summary>The interview opened in the Live Assistant for THIS workspace (ids only, no content).</summary>
    public (string? ProfileId, string? TargetId) GetActive()
    {
        try
        {
            var p = Path.Combine(_root, "active.json");
            if (!File.Exists(p)) return (null, null);
            var d = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(p));
            string? Id(string k) => d != null && d.TryGetValue(k, out var v) && v != null && v.Length <= 64 && v.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? v : null;
            return (Id("profile"), Id("target"));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return (null, null); }
    }

    public void SetActive(string? profileId, string? targetId)
    {
        lock (_lock) File.WriteAllText(Path.Combine(_root, "active.json"), JsonSerializer.Serialize(new Dictionary<string, string?> { ["profile"] = profileId, ["target"] = targetId }));
    }

    private static string SafeId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new ArgumentException("Invalid id", nameof(id));
        return id;
    }

    private string PathFor(string folder, string id) => Path.Combine(_root, folder, SafeId(id) + ".bin");

    private void Write<T>(string folder, string id, T value)
    {
        var bytes = _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(value, Json));
        var path = PathFor(folder, id);
        lock (_lock)
        {
            var tmp = path + ".tmp";
            // Flushed to disk before the atomic rename, so a power loss can never leave an empty file behind.
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
    }

    /// <summary>Files moved aside because they could not be decrypted or parsed (see <see cref="FileQuarantined"/>).</summary>
    public int QuarantinedFiles => _quarantined;
    private int _quarantined;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _unreadable = new();
    /// <summary>Raised with the folder name ("profiles", "sessions", …) when a damaged file is moved aside.</summary>
    public event Action<string>? FileQuarantined;

    private T? Read<T>(string folder, string id) where T : class
    {
        var path = PathFor(folder, id);
        if (!File.Exists(path) || _unreadable.ContainsKey(path)) return null;
        try { return JsonSerializer.Deserialize<T>(_protector.Unprotect(File.ReadAllBytes(path)), Json); }
        catch (Exception ex) when (ex is JsonException or System.Security.Cryptography.CryptographicException or NotSupportedException or ArgumentException)
        {
            // One damaged file (disk error, or encrypted for another Windows user) must not hide every other item:
            // it is renamed to *.corrupt (kept for support, never deleted) and skipped.
            lock (_lock)
            {
                try { File.Move(path, path + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".corrupt", overwrite: true); }
                catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException) { _unreadable[path] = 0; }   // could not move: skip it from now on
            }
            Interlocked.Increment(ref _quarantined);
            FileQuarantined?.Invoke(folder);
            return null;
        }
    }

    private IEnumerable<T> All<T>(string folder) where T : class
    {
        foreach (var f in Directory.GetFiles(Path.Combine(_root, folder), "*.bin"))
        {
            var v = Read<T>(folder, Path.GetFileNameWithoutExtension(f));
            if (v != null) yield return v;
        }
    }

    // ---- profiles
    public void SaveProfile(CandidateProfileRecord p) { ProfileMigration.Migrate(p); p.UpdatedUtc = DateTime.UtcNow; Write("profiles", p.Id, p); }
    /// <summary>Loads a profile, upgrading older schemas in memory (persisted on the next save; nothing is lost).</summary>
    public CandidateProfileRecord? GetProfile(string id) { var p = Read<CandidateProfileRecord>("profiles", id); if (p != null) ProfileMigration.Migrate(p); return p; }
    public IReadOnlyList<CandidateProfileRecord> Profiles() => All<CandidateProfileRecord>("profiles").Select(p => { ProfileMigration.Migrate(p); return p; }).OrderBy(p => p.CreatedUtc).ToList();

    /// <summary>Deletes the profile and everything owned by it (targets, packs, sessions).</summary>
    public void DeleteProfile(string id)
    {
        foreach (var t in Targets(id)) DeleteTarget(t.Id);
        foreach (var s in Sessions().Where(s => s.ProfileId == id)) DeleteSession(s.Id);
        Delete("profiles", id);
    }

    // ---- targets
    public void SaveTarget(InterviewTarget t) { t.UpdatedUtc = DateTime.UtcNow; Write("targets", t.Id, t); }
    public InterviewTarget? GetTarget(string id) => Read<InterviewTarget>("targets", id);
    public IReadOnlyList<InterviewTarget> Targets(string profileId) => All<InterviewTarget>("targets").Where(t => t.ProfileId == profileId).OrderBy(t => t.CreatedUtc).ToList();
    public void DeleteTarget(string id) { Delete("packs", id); Delete("targets", id); }

    // ---- packs
    public void SavePack(PreparedPack p) => Write("packs", p.TargetId, p);
    public PreparedPack? GetPack(string targetId) => Read<PreparedPack>("packs", targetId);

    // ---- sessions (reports + memory snapshots)
    public void SaveSession(StoredSession s) => Write("sessions", s.Id, s);
    public StoredSession? GetSession(string id) => Read<StoredSession>("sessions", id);
    public IReadOnlyList<StoredSession> Sessions() => All<StoredSession>("sessions").OrderByDescending(s => s.StartedUtc).ToList();
    public void DeleteSession(string id) => Delete("sessions", id);

    /// <summary>Removes sessions older than the retention period. Returns number removed.</summary>
    public int ApplyRetention(TimeSpan retention, DateTime nowUtc)
    {
        int n = 0;
        foreach (var s in Sessions().Where(s => nowUtc - s.StartedUtc > retention)) { DeleteSession(s.Id); n++; }
        return n;
    }

    private void Delete(string folder, string id)
    {
        var p = PathFor(folder, id);
        lock (_lock) if (File.Exists(p)) File.Delete(p);
    }

    /// <summary>
    /// "Raw data (ZIP, advanced)": the profile with everything it owns — the same set <see cref="DeleteProfile"/> removes
    /// (interviews, prepared packs, sessions/reports) — as a plain (unencrypted) ZIP of JSON files, for other tools and
    /// data-portability requests. Passed through <see cref="SecretFilter"/>. Not importable: the restorable backup with
    /// a manifest and hashes is <see cref="ProfileBundle"/> (.iaprofile).
    /// </summary>
    public void ExportProfile(string profileId, Stream output)
    {
        var profile = GetProfile(profileId) ?? throw new KeyNotFoundException("profile");
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        void Text(string name, string text)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            w.Write(text);
        }
        void Add<T>(string name, T v) => Text(name, SecretFilter.ToJson(v));
        Text("README.txt", "Export format v1 (raw data): profile.json, targets/*.json, packs/*.json, sessions/*.json. Source document text is included in profile/target JSON. "
            + "Passwords, tokens and keys are never included. To restore or move a profile to another PC, use the .iaprofile backup instead.");
        Add("profile.json", profile);
        foreach (var t in Targets(profileId))
        {
            Add($"targets/{t.Id}.json", t);
            var pack = GetPack(t.Id);
            if (pack != null) Add($"packs/{t.Id}.json", pack);
        }
        foreach (var s in Sessions().Where(s => s.ProfileId == profileId)) Add($"sessions/{s.Id}.json", s);
    }
}

/// <summary>Persisted record of a finished (or interrupted) interview session.</summary>
public sealed class StoredSession
{
    public string Id { get; init; } = Guid.NewGuid().ToString("n");
    public string ProfileId { get; init; } = "";
    public string TargetId { get; init; } = "";
    public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
    public DateTime? EndedUtc { get; set; }
    public string MemoryJson { get; set; } = "";
    public string ReportJson { get; set; } = "";
}
