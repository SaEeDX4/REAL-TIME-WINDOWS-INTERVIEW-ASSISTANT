using System.Text.Json;
using InterviewAssistant.Backend.Data;
using InterviewAssistant.Backend.Infrastructure;
using InterviewAssistant.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InterviewAssistant.Backend.Services;

/// <summary>
/// Server-managed runtime config (model routing, timeouts, feature flags, maintenance, minimum client version).
/// Operators change rows in runtime_config (admin API) — no desktop release needed when a model changes.
/// Disabled models are skipped in favour of the configured fallbacks (kill switch).
/// </summary>
public sealed class ConfigService
{
    private readonly AppDbContext _db;
    private readonly SafetyOptions _safety;
    public ConfigService(AppDbContext db, IOptions<SafetyOptions> safety) { _db = db; _safety = safety.Value; }

    public static readonly RuntimeConfig Defaults = new(
        "gpt-live-transcribe", new[] { "gpt-4o-transcribe", "gpt-4o-mini-transcribe" },
        "gpt-5.4-mini", new[] { "gpt-4.1-mini" }, "gpt-5.4-mini", 260, 8000,
        new Dictionary<string, bool> { ["coach_mode"] = true, ["adaptive_interruptions"] = true, ["report_export"] = true, ["prep_ai_enrichment"] = true },
        false, null, "2.0.0", 72);

    public async Task<RuntimeConfig> GetAsync(CancellationToken ct)
    {
        var rows = await _db.Config.AsNoTracking().ToDictionaryAsync(r => r.Key, r => r.ValueJson, ct);
        T Get<T>(string key, T fallback) { try { return rows.TryGetValue(key, out var j) ? JsonSerializer.Deserialize<T>(j) ?? fallback : fallback; } catch (JsonException) { return fallback; } }
        var disabled = Get("disabled_models", new List<string>());
        string Pick(string primary, IReadOnlyList<string> fallbacks) => new[] { primary }.Concat(fallbacks).FirstOrDefault(m => !disabled.Contains(m)) ?? primary;
        var tModel = Get("transcription_model", Defaults.TranscriptionModel);
        var tFall = Get<List<string>>("transcription_fallbacks", Defaults.TranscriptionFallbacks.ToList());
        var aModel = Get("answer_model", Defaults.AnswerModel);
        var aFall = Get<List<string>>("answer_fallbacks", Defaults.AnswerFallbacks.ToList());
        var features = new Dictionary<string, bool>(Defaults.Features);
        foreach (var kv in Get("features", new Dictionary<string, bool>())) features[kv.Key] = kv.Value;
        return new RuntimeConfig(Pick(tModel, tFall), tFall.Where(m => !disabled.Contains(m)).ToList(), Pick(aModel, aFall), aFall.Where(m => !disabled.Contains(m)).ToList(),
            Get("preparation_model", Defaults.PreparationModel), Get("answer_max_tokens", Defaults.AnswerMaxTokens), Get("first_token_timeout_ms", Defaults.FirstTokenTimeoutMs),
            features, Get("maintenance", false), Get<string?>("maintenance_message", null), Get("minimum_client_version", _safety.MinimumClientVersion), _safety.OfflineGraceHours);
    }

    public async Task SetAsync(string key, JsonElement value, string actor, TimeProvider time, CancellationToken ct)
    {
        var row = await _db.Config.FirstOrDefaultAsync(c => c.Key == key, ct);
        if (row == null) _db.Config.Add(row = new ConfigEntry { Key = key });
        row.ValueJson = value.GetRawText(); row.UpdatedUtc = time.GetUtcNow().UtcDateTime; row.UpdatedBy = actor;
        await _db.SaveChangesAsync(ct);
    }

    public static bool VersionAtLeast(string? client, string minimum) =>
        Version.TryParse((client ?? "0.0.0").Split('-', '+')[0], out var c) && Version.TryParse(minimum, out var m) && c >= m;
}
