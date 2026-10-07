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
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
    }

    private T? Read<T>(string folder, string id) where T : class
    {
        var path = PathFor(folder, id);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<T>(_protector.Unprotect(File.ReadAllBytes(path)), Json);
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
    public void SaveProfile(CandidateProfileRecord p) { p.UpdatedUtc = DateTime.UtcNow; Write("profiles", p.Id, p); }
    public CandidateProfileRecord? GetProfile(string id) => Read<CandidateProfileRecord>("profiles", id);
    public IReadOnlyList<CandidateProfileRecord> Profiles() => All<CandidateProfileRecord>("profiles").OrderBy(p => p.CreatedUtc).ToList();

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

    /// <summary>Exports a profile with its targets, packs and sessions as a plain (unencrypted) ZIP of JSON files.</summary>
    public void ExportProfile(string profileId, Stream output)
    {
        var profile = GetProfile(profileId) ?? throw new KeyNotFoundException("profile");
        var pretty = new JsonSerializerOptions { WriteIndented = true };
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        void Add(string name, object v)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            w.Write(JsonSerializer.Serialize(v, pretty));
        }
        Add("README.txt", "Export format v1: profile.json, targets/*.json, packs/*.json, sessions/*.json. Source document text is included in profile/target JSON.");
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
