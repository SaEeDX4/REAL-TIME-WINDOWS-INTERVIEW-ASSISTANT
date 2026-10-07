using System.Security.Cryptography;
using System.Text;
using InterviewAssistant.Backend.Data;
using InterviewAssistant.Backend.Infrastructure;
using InterviewAssistant.Contracts;
using Microsoft.EntityFrameworkCore;

namespace InterviewAssistant.Backend.Services;

/// <summary>
/// Device access by random installation id (hashed; no hardware fingerprinting). Re-registering the same installation
/// (reinstall/upgrade) reuses the device; a new installation beyond the plan limit is rejected until the user revokes one.
/// </summary>
public sealed class DeviceService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _time;
    public DeviceService(AppDbContext db, TimeProvider time) { _db = db; _time = time; }

    public static string Hash(string installationId) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installationId.Trim()))).ToLowerInvariant();

    public async Task<DeviceDto> RegisterAsync(UserAccount user, EffectivePlan plan, RegisterDeviceRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.InstallationId) || req.InstallationId.Length is < 16 or > 128) throw ApiException.BadRequest("invalid_installation", "Invalid installation id.");
        var now = _time.GetUtcNow().UtcDateTime;
        var hash = Hash(req.InstallationId);
        var d = await _db.Devices.FirstOrDefaultAsync(x => x.UserId == user.Id && x.InstallationIdHash == hash, ct);
        if (d == null)
        {
            var active = await _db.Devices.CountAsync(x => x.UserId == user.Id && x.RevokedUtc == null, ct);
            if (active >= plan.Plan.MaxDevices)
                throw ApiException.Forbidden("device_limit", $"Your plan allows {plan.Plan.MaxDevices} device(s). Remove a device in Account → Devices to use this one.");
            d = new Device { Id = Guid.NewGuid(), UserId = user.Id, InstallationIdHash = hash, CreatedUtc = now };
            _db.Devices.Add(d);
        }
        else if (d.RevokedUtc != null)
        {
            var active = await _db.Devices.CountAsync(x => x.UserId == user.Id && x.RevokedUtc == null, ct);
            if (active >= plan.Plan.MaxDevices) throw ApiException.Forbidden("device_limit", "Device limit reached. Remove another device first.");
            d.RevokedUtc = null; // explicit re-activation by the owner
        }
        d.Name = Trunc(req.Name, 120); d.Platform = Trunc(req.Platform, 40); d.ClientVersion = Trunc(req.ClientVersion, 40); d.LastSeenUtc = now;
        await _db.SaveChangesAsync(ct);
        return ToDto(d, d.Id);
    }

    public async Task<List<DeviceDto>> ListAsync(Guid userId, Guid? current, CancellationToken ct) =>
        (await _db.Devices.AsNoTracking().Where(d => d.UserId == userId).OrderByDescending(d => d.LastSeenUtc).ToListAsync(ct)).Select(d => ToDto(d, current)).ToList();

    public async Task RevokeAsync(Guid userId, Guid deviceId, CancellationToken ct)
    {
        var d = await _db.Devices.FirstOrDefaultAsync(x => x.Id == deviceId && x.UserId == userId, ct) ?? throw ApiException.NotFound();
        var now = _time.GetUtcNow().UtcDateTime;
        d.RevokedUtc = now;
        foreach (var s in await _db.Sessions.Where(s => s.DeviceId == deviceId && s.Status == "active").ToListAsync(ct)) { s.Status = "stopped"; s.EndReason = "device_revoked"; s.EndedUtc = now; }
        await _db.SaveChangesAsync(ct);
    }

    public async Task<Device> RequireActiveAsync(Guid userId, Guid deviceId, CancellationToken ct)
    {
        var d = await _db.Devices.FirstOrDefaultAsync(x => x.Id == deviceId && x.UserId == userId, ct) ?? throw ApiException.Forbidden("device_unknown", "Register this device first.");
        if (d.RevokedUtc != null) throw ApiException.Forbidden("device_revoked", "This device was removed from your account.");
        d.LastSeenUtc = _time.GetUtcNow().UtcDateTime;
        return d;
    }

    private static DeviceDto ToDto(Device d, Guid? current) => new(d.Id, d.Name, d.Platform, d.ClientVersion, d.LastSeenUtc, d.RevokedUtc != null, d.Id == current);
    private static string Trunc(string? s, int n) => (s ?? "").Length > n ? s![..n] : s ?? "";
}
