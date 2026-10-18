using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using InterviewAssistant.Core.Domain;

namespace InterviewAssistant.Core.Persistence;

/// <summary>Why a profile backup was rejected (the desktop maps each to a plain-language message).</summary>
public enum ProfileBundleError
{
    /// <summary>Not a ZIP, or no iaprofile manifest.</summary>
    NotABundle,
    /// <summary>Created by a newer app (manifest version above <see cref="ProfileBundle.FormatVersion"/>).</summary>
    NewerVersion,
    /// <summary>An entry does not match the SHA-256 or size recorded in the manifest, or entries were added/removed.</summary>
    Tampered,
    /// <summary>Over a size or entry-count limit (also zip bombs).</summary>
    TooLarge,
    /// <summary>An entry name tries to leave the bundle (path traversal, absolute path, drive letter, backslash).</summary>
    UnsafePath,
    /// <summary>Unexpected entries or JSON that is not a valid profile, interview, prepared pack or report.</summary>
    Invalid,
}

public sealed class ProfileBundleException : Exception
{
    public ProfileBundleError Error { get; }
    public ProfileBundleException(ProfileBundleError error, string detail) : base($"{error}: {detail}") => Error = error;
}

/// <summary>Limits for reading an untrusted bundle. The defaults are far above any real profile.</summary>
public sealed record ProfileBundleLimits(long MaxFileBytes = 64L << 20, long MaxEntryBytes = 32L << 20, long MaxTotalBytes = 160L << 20, int MaxEntries = 4000)
{
    public static readonly ProfileBundleLimits Default = new();
}

public sealed class ProfileBundleCounts
{
    [JsonPropertyName("facts")] public int Facts { get; set; }
    [JsonPropertyName("interviews")] public int Interviews { get; set; }
    [JsonPropertyName("packs")] public int Packs { get; set; }
    [JsonPropertyName("sessions")] public int Sessions { get; set; }
    [JsonPropertyName("reports")] public int Reports { get; set; }
}

public sealed class ProfileBundleEntry
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
}

public sealed class ProfileBundleManifest
{
    [JsonPropertyName("format")] public string Format { get; set; } = ProfileBundle.FormatName;
    [JsonPropertyName("version")] public int Version { get; set; } = ProfileBundle.FormatVersion;
    [JsonPropertyName("appVersion")] public string AppVersion { get; set; } = "";
    [JsonPropertyName("createdUtc")] public DateTime CreatedUtc { get; set; }
    [JsonPropertyName("profileName")] public string ProfileName { get; set; } = "";
    [JsonPropertyName("counts")] public ProfileBundleCounts Counts { get; set; } = new();
    [JsonPropertyName("entries")] public List<ProfileBundleEntry> Entries { get; set; } = new();
}

/// <summary>What the import preview shows (counts come from the validated content, not from the manifest's claims).</summary>
public sealed record ProfileBundlePreview(string ProfileName, int Facts, int Interviews, int Packs, int Reports, DateTime CreatedUtc, string AppVersion);

/// <summary>A validated bundle, ready to preview and import.</summary>
public sealed class ProfileBundleContent
{
    public required ProfileBundleManifest Manifest { get; init; }
    public required JsonObject Profile { get; init; }
    public required IReadOnlyList<JsonObject> Targets { get; init; }
    public required IReadOnlyList<JsonObject> Packs { get; init; }
    public required IReadOnlyList<JsonObject> Sessions { get; init; }
    public required ProfileBundlePreview Preview { get; init; }
}

/// <summary>
/// ".iaprofile" backup &amp; transfer bundle: a ZIP holding manifest.json (format "iaprofile", version 1, app version,
/// creation time, counts and the SHA-256 of every other entry), profile.json, targets/*.json, packs/*.json and
/// sessions/*.json. Unlike the DPAPI-encrypted workspace (readable only by the same Windows user on the same PC), the
/// bundle can be restored on another PC. Secrets are filtered on write. Reading treats the file as untrusted input:
/// size and entry limits, a strict entry-name allow-list (no traversal), hash verification and JSON validation, and
/// importing always creates a NEW profile with new ids (nothing on the PC is overwritten).
/// </summary>
public static class ProfileBundle
{
    public const string FormatName = "iaprofile";
    public const int FormatVersion = 1;
    public const string FileExtension = ".iaprofile";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions Typed = new() { MaxDepth = 64 };
    private static readonly JsonNodeOptions NodeOptions = new();
    private static readonly JsonDocumentOptions DocOptions = new() { MaxDepth = 64 };
    private static readonly Regex EntryName = new(@"^(?:manifest\.json|profile\.json|(?:targets|packs|sessions)/[A-Za-z0-9\-]{1,64}\.json)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SafeId = new(@"^[A-Za-z0-9\-]{1,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // ------------------------------------------------------------------ write

    /// <summary>Writes the profile with its interviews, prepared packs and sessions (reports) to <paramref name="output"/>.</summary>
    public static ProfileBundleManifest Write(WorkspaceStore store, string profileId, Stream output, string appVersion, DateTime? nowUtc = null)
    {
        var profile = store.GetProfile(profileId) ?? throw new KeyNotFoundException("profile");
        var targets = store.Targets(profileId);
        var sessions = store.Sessions().Where(s => s.ProfileId == profileId).ToList();
        var entries = new List<(string Path, byte[] Bytes)> { ("profile.json", Json(profile)) };
        int packs = 0;
        foreach (var t in targets)
        {
            entries.Add(($"targets/{t.Id}.json", Json(t)));
            if (store.GetPack(t.Id) is { } pack) { entries.Add(($"packs/{t.Id}.json", Json(pack))); packs++; }
        }
        foreach (var s in sessions) entries.Add(($"sessions/{s.Id}.json", Json(s)));

        var manifest = new ProfileBundleManifest
        {
            AppVersion = appVersion, CreatedUtc = nowUtc ?? DateTime.UtcNow, ProfileName = profile.Name,
            Counts = new ProfileBundleCounts
            {
                Facts = profile.Facts.Count(f => f.IsReviewable), Interviews = targets.Count, Packs = packs,
                Sessions = sessions.Count, Reports = sessions.Count(s => s.ReportJson.Length > 0),
            },
            Entries = entries.Select(e => new ProfileBundleEntry { Path = e.Path, Size = e.Bytes.Length, Sha256 = Hash(e.Bytes) }).ToList(),
        };
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        void Add(string name, byte[] bytes) { using var w = zip.CreateEntry(name, CompressionLevel.Optimal).Open(); w.Write(bytes); }
        Add("manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, Indented));
        foreach (var (path, bytes) in entries) Add(path, bytes);
        return manifest;
    }

    private static byte[] Json<T>(T value) => Encoding.UTF8.GetBytes(SecretFilter.ToJson(value));

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // ------------------------------------------------------------------ read (untrusted)

    /// <summary>Reads and fully validates a bundle. Throws <see cref="ProfileBundleException"/> for anything not acceptable.</summary>
    public static ProfileBundleContent Read(Stream input, ProfileBundleLimits? limits = null)
    {
        limits ??= ProfileBundleLimits.Default;
        var buffer = ReadBounded(input, limits.MaxFileBytes, () => new ProfileBundleException(ProfileBundleError.TooLarge, "file"));
        ZipArchive zip;
        try { zip = new ZipArchive(new MemoryStream(buffer, writable: false), ZipArchiveMode.Read); }
        catch (InvalidDataException ex) { throw new ProfileBundleException(ProfileBundleError.NotABundle, ex.GetType().Name); }
        using (zip)
        {
            IReadOnlyCollection<ZipArchiveEntry> all;
            try { all = zip.Entries; }
            catch (InvalidDataException ex) { throw new ProfileBundleException(ProfileBundleError.NotABundle, ex.GetType().Name); }
            if (all.Count > limits.MaxEntries) throw new ProfileBundleException(ProfileBundleError.TooLarge, "entries");

            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var e in all)
            {
                var name = e.FullName;
                if (IsUnsafe(name)) throw new ProfileBundleException(ProfileBundleError.UnsafePath, "entry name");
                if (!EntryName.IsMatch(name)) throw new ProfileBundleException(ProfileBundleError.Invalid, "unexpected entry");
                if (!seen.Add(name)) throw new ProfileBundleException(ProfileBundleError.Invalid, "duplicate entry");
                if (e.Length > limits.MaxEntryBytes) throw new ProfileBundleException(ProfileBundleError.TooLarge, "entry");
                byte[] bytes;
                try
                {
                    using var s = e.Open();
                    bytes = ReadBounded(s, Math.Min(limits.MaxEntryBytes, limits.MaxTotalBytes - total), () => new ProfileBundleException(ProfileBundleError.TooLarge, "entry content"));
                }
                catch (InvalidDataException ex) { throw new ProfileBundleException(ProfileBundleError.NotABundle, ex.GetType().Name); }
                total += bytes.Length;
                files[name] = bytes;
            }

            if (!files.TryGetValue("manifest.json", out var manifestBytes)) throw new ProfileBundleException(ProfileBundleError.NotABundle, "no manifest");
            var manifest = ParseManifest(manifestBytes);

            // The manifest lists exactly the other entries, each with its size and SHA-256.
            var listed = new Dictionary<string, ProfileBundleEntry>(StringComparer.Ordinal);
            foreach (var m in manifest.Entries)
                if (m.Path == "manifest.json" || !EntryName.IsMatch(m.Path ?? "") || !listed.TryAdd(m.Path!, m)) throw new ProfileBundleException(ProfileBundleError.Tampered, "manifest entries");
            if (listed.Count != files.Count - 1) throw new ProfileBundleException(ProfileBundleError.Tampered, "entry set");
            foreach (var (name, bytes) in files)
            {
                if (name == "manifest.json") continue;
                if (!listed.TryGetValue(name, out var m)) throw new ProfileBundleException(ProfileBundleError.Tampered, "unlisted entry");
                if (m.Size != bytes.Length || !string.Equals(m.Sha256, Hash(bytes), StringComparison.OrdinalIgnoreCase))
                    throw new ProfileBundleException(ProfileBundleError.Tampered, "hash");
            }

            // ---- JSON sanity and references
            if (!files.TryGetValue("profile.json", out var profileBytes)) throw new ProfileBundleException(ProfileBundleError.Invalid, "no profile");
            var profileNode = ParseObject(profileBytes);
            var profile = Deserialize<CandidateProfileRecord>(profileNode);
            if (!SafeId.IsMatch(profile.Id)) throw new ProfileBundleException(ProfileBundleError.Invalid, "profile id");

            var targets = new List<JsonObject>();
            var targetIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (name, bytes) in files.Where(f => f.Key.StartsWith("targets/", StringComparison.Ordinal)).OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                var node = ParseObject(bytes);
                var t = Deserialize<InterviewTarget>(node);
                if (t.Id != IdOf(name) || t.ProfileId != profile.Id) throw new ProfileBundleException(ProfileBundleError.Invalid, "interview reference");
                targetIds.Add(t.Id);
                targets.Add(node);
            }
            var packs = new List<JsonObject>();
            foreach (var (name, bytes) in files.Where(f => f.Key.StartsWith("packs/", StringComparison.Ordinal)).OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                var node = ParseObject(bytes);
                var p = Deserialize<PreparedPack>(node);
                if (p.TargetId != IdOf(name) || !targetIds.Contains(p.TargetId)) throw new ProfileBundleException(ProfileBundleError.Invalid, "pack reference");
                packs.Add(node);
            }
            var sessions = new List<JsonObject>();
            int reports = 0;
            foreach (var (name, bytes) in files.Where(f => f.Key.StartsWith("sessions/", StringComparison.Ordinal)).OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                var node = ParseObject(bytes);
                var s = Deserialize<StoredSession>(node);
                if (s.Id != IdOf(name) || s.ProfileId != profile.Id) throw new ProfileBundleException(ProfileBundleError.Invalid, "session reference");
                if (s.TargetId.Length > 0 && !SafeId.IsMatch(s.TargetId)) throw new ProfileBundleException(ProfileBundleError.Invalid, "session interview");
                if (s.ReportJson.Length > 0) reports++;
                sessions.Add(node);
            }

            var c = manifest.Counts;
            if (c.Interviews != targets.Count || c.Packs != packs.Count || c.Sessions != sessions.Count)
                throw new ProfileBundleException(ProfileBundleError.Tampered, "counts");

            var preview = new ProfileBundlePreview(Clip(profile.Name, 200), profile.Facts.Count(f => f.IsReviewable), targets.Count, packs.Count, reports,
                manifest.CreatedUtc, Clip(manifest.AppVersion, 64));
            return new ProfileBundleContent { Manifest = manifest, Profile = profileNode, Targets = targets, Packs = packs, Sessions = sessions, Preview = preview };
        }
    }

    /// <summary>Path traversal and other names that could escape an extraction folder (the reader never extracts, but rejects them anyway).</summary>
    internal static bool IsUnsafe(string name) =>
        name.Length == 0 || name.Length > 200 || name.Contains("..", StringComparison.Ordinal) || name.Contains('\\') || name.Contains(':') ||
        name.StartsWith('/') || name.Any(char.IsControl) || Path.IsPathRooted(name);

    private static string IdOf(string entryName) => Path.GetFileNameWithoutExtension(entryName[(entryName.IndexOf('/') + 1)..]);

    private static string Clip(string? s, int max)
    {
        var t = new string((s ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return t.Length > max ? t[..max] : t;
    }

    private static ProfileBundleManifest ParseManifest(byte[] bytes)
    {
        JsonObject node;
        try { node = ParseObject(bytes); }
        catch (ProfileBundleException) { throw new ProfileBundleException(ProfileBundleError.NotABundle, "manifest json"); }
        if (node["format"] is not JsonValue f || !f.TryGetValue<string>(out var format) || format != FormatName)
            throw new ProfileBundleException(ProfileBundleError.NotABundle, "format");
        if (node["version"] is not JsonValue v || !v.TryGetValue<int>(out var version) || version < 1)
            throw new ProfileBundleException(ProfileBundleError.Invalid, "version");
        if (version > FormatVersion) throw new ProfileBundleException(ProfileBundleError.NewerVersion, $"version {version}");
        var manifest = Deserialize<ProfileBundleManifest>(node);
        manifest.Entries ??= new();
        manifest.Counts ??= new();
        return manifest;
    }

    private static JsonObject ParseObject(byte[] bytes)
    {
        try
        {
            return JsonNode.Parse(bytes, NodeOptions, DocOptions) as JsonObject ?? throw new ProfileBundleException(ProfileBundleError.Invalid, "not an object");
        }
        catch (JsonException) { throw new ProfileBundleException(ProfileBundleError.Invalid, "json"); }
    }

    private static T Deserialize<T>(JsonNode node) where T : class
    {
        try { return node.Deserialize<T>(Typed) ?? throw new ProfileBundleException(ProfileBundleError.Invalid, typeof(T).Name); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException or FormatException)
        { throw new ProfileBundleException(ProfileBundleError.Invalid, typeof(T).Name); }
    }

    private static byte[] ReadBounded(Stream s, long max, Func<Exception> tooLarge)
    {
        if (s.CanSeek && s.Length - s.Position > max) throw tooLarge();
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
        {
            if (ms.Length + n > max) throw tooLarge();
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ import

    /// <summary>
    /// Imports a validated bundle as a NEW profile: new ids for the profile, its interviews and its sessions, with every
    /// reference (interview → profile, pack → interview, session → profile/interview, report → session) remapped.
    /// Nothing existing is overwritten; on a failure the partly imported profile is removed again. Returns the new profile.
    /// </summary>
    public static CandidateProfileRecord Import(WorkspaceStore store, ProfileBundleContent content, string? name = null)
    {
        var newProfileId = NewId();
        while (store.GetProfile(newProfileId) != null) newProfileId = NewId();
        var targetMap = content.Targets.ToDictionary(t => (string)t["Id"]!, _ => NewId(), StringComparer.Ordinal);

        var profileNode = content.Profile.DeepClone().AsObject();
        profileNode["Id"] = newProfileId;
        if (!string.IsNullOrWhiteSpace(name)) profileNode["Name"] = name;
        var profile = Deserialize<CandidateProfileRecord>(profileNode);
        try
        {
            store.SaveProfile(profile);
            foreach (var t in content.Targets)
            {
                var node = t.DeepClone().AsObject();
                node["Id"] = targetMap[(string)t["Id"]!];
                node["ProfileId"] = newProfileId;
                store.SaveTarget(Deserialize<InterviewTarget>(node));
            }
            foreach (var p in content.Packs)
            {
                var node = p.DeepClone().AsObject();
                var targetId = targetMap[(string)p["TargetId"]!];
                node["TargetId"] = targetId;
                node["ProfileId"] = newProfileId;
                if (node["Context"] is JsonObject ctx) { ctx["profile_id"] = newProfileId; ctx["target_id"] = targetId; }
                store.SavePack(Deserialize<PreparedPack>(node));
            }
            foreach (var s in content.Sessions)
            {
                var node = s.DeepClone().AsObject();
                var sessionId = NewId();
                node["Id"] = sessionId;
                node["ProfileId"] = newProfileId;
                var oldTarget = s["TargetId"]?.GetValue<string>() ?? "";
                node["TargetId"] = targetMap.TryGetValue(oldTarget, out var mapped) ? mapped : "";
                if (s["ReportJson"]?.GetValue<string>() is { Length: > 0 } report) node["ReportJson"] = RemapReport(report, sessionId);
                store.SaveSession(Deserialize<StoredSession>(node));
            }
        }
        catch
        {
            try { store.DeleteProfile(newProfileId); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            throw;
        }
        return store.GetProfile(newProfileId) ?? profile;
    }

    private static string RemapReport(string reportJson, string sessionId)
    {
        try
        {
            if (JsonNode.Parse(reportJson, NodeOptions, DocOptions) is not JsonObject r) return reportJson;
            if (r.ContainsKey("SessionId")) r["SessionId"] = sessionId;
            return r.ToJsonString(Indented);
        }
        catch (JsonException) { return reportJson; }   // an unreadable report stays as it was (shown as unreadable, like before)
    }

    private static string NewId() => Guid.NewGuid().ToString("n");
}
