using System.Text;
using System.Text.Json;
using InterviewAssistant.Backend.Data;
using InterviewAssistant.Backend.Infrastructure;
using InterviewAssistant.Backend.Services;
using InterviewAssistant.Contracts;
using InterviewAssistant.Core.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InterviewAssistant.Backend.Endpoints;

public static class ApiEndpoints
{
    private sealed record Ctx(UserAccount User, EffectivePlan Plan, Subscription? Sub);

    private static async Task<Ctx> Resolve(HttpContext http)
    {
        var sp = http.RequestServices;
        var user = await sp.GetRequiredService<UserService>().GetOrCreateAsync(http.User, http.RequestAborted);
        var (plan, sub) = await sp.GetRequiredService<EntitlementService>().ResolveAsync(user, http.RequestAborted);
        return new Ctx(user, plan, sub);
    }

    public static void MapApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1");
        var authed = api.MapGroup("").RequireAuthorization();

        // ---------------- public ----------------
        api.MapGet("/config", async (ConfigService c, CancellationToken ct) => Results.Ok(await c.GetAsync(ct))).RequireRateLimiting("anon");
        api.MapGet("/billing/plans", (IOptions<PlanCatalog> p) => Results.Ok(p.Value.Plans.Select(x =>
            new PlanDto(x.Code, x.Name, EntitlementService.ToDto(new EffectivePlan(x, "catalog", DateTime.MinValue, DateTime.MaxValue, false)), x.Purchasable)))).RequireRateLimiting("anon");

        // ---------------- account ----------------
        authed.MapGet("/me", async (HttpContext http, UsageService usage, CancellationToken ct) =>
        {
            var c = await Resolve(http);
            var subDto = c.Sub == null ? null : new SubscriptionDto(c.Sub.PlanCode, c.Sub.Status, c.Sub.CurrentPeriodEndUtc, c.Sub.CancelAtPeriodEnd, c.Sub.GraceUntilUtc);
            return Results.Ok(new AccountDto(c.User.Id, c.User.Email, c.User.Status, EntitlementService.ToDto(c.Plan), await usage.SummaryAsync(c.User.Id, c.Plan, ct), subDto, c.User.CreatedUtc));
        });
        authed.MapGet("/me/export", async (HttpContext http, AccountService acc, CancellationToken ct) => Results.Json(await acc.ExportAsync((await Resolve(http)).User, ct)));
        authed.MapDelete("/me", async (HttpContext http, AccountService acc, CancellationToken ct) => { await acc.DeleteAsync((await Resolve(http)).User, ct); return Results.NoContent(); });

        // ---------------- devices ----------------
        authed.MapPost("/devices", async (HttpContext http, RegisterDeviceRequest req, DeviceService d, CancellationToken ct) =>
        { var c = await Resolve(http); return Results.Ok(await d.RegisterAsync(c.User, c.Plan, req, ct)); });
        authed.MapGet("/devices", async (HttpContext http, Guid? current, DeviceService d, CancellationToken ct) =>
            Results.Ok(await d.ListAsync((await Resolve(http)).User.Id, current, ct)));
        authed.MapDelete("/devices/{id:guid}", async (HttpContext http, Guid id, DeviceService d, CancellationToken ct) =>
        { await d.RevokeAsync((await Resolve(http)).User.Id, id, ct); return Results.NoContent(); });

        // ---------------- profiles / targets / reports (sync) ----------------
        foreach (var kind in DocumentService.Kinds)
        {
            var route = "/" + kind + "s";
            authed.MapGet(route, async (HttpContext http, Guid? parentId, DocumentService docs, CancellationToken ct) => Results.Ok(await docs.ListAsync((await Resolve(http)).User.Id, kind, parentId, ct)));
            authed.MapGet(route + "/{id:guid}", async (HttpContext http, Guid id, DocumentService docs, CancellationToken ct) => Results.Ok(await docs.GetAsync((await Resolve(http)).User.Id, kind, id, ct)));
            authed.MapPut(route + "/{id:guid}", async (HttpContext http, Guid id, UpsertDocumentRequest req, DocumentService docs, CancellationToken ct) =>
            { var c = await Resolve(http); return Results.Ok(await docs.UpsertAsync(c.User.Id, c.Plan, kind, id, req, ct)); });
            authed.MapDelete(route + "/{id:guid}", async (HttpContext http, Guid id, DocumentService docs, CancellationToken ct) =>
            { await docs.DeleteAsync((await Resolve(http)).User.Id, kind, id, ct); return Results.NoContent(); });
        }

        // ---------------- preparation jobs (quota) ----------------
        authed.MapPost("/preparation/jobs", async (HttpContext http, Guid? targetId, UsageService usage, AppDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var c = await Resolve(http);
            var summary = await usage.SummaryAsync(c.User.Id, c.Plan, ct);
            if (summary.PrepJobsRemaining <= 0) throw ApiException.Payment("prep_limit", "No interview preparations remain this month on your plan.");
            var job = new PreparationJob { Id = Guid.NewGuid(), UserId = c.User.Id, TargetId = targetId, CreatedUtc = time.GetUtcNow().UtcDateTime };
            db.PreparationJobs.Add(job);
            usage.Record(c.User.Id, UsageService.PrepJob, 1);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new PrepJobResponse(job.Id, summary.PrepJobsRemaining - 1));
        });

        // ---------------- live sessions ----------------
        authed.MapPost("/sessions", async (HttpContext http, StartSessionRequest req, SessionService s, AppDbContext db, CancellationToken ct) =>
        {
            var c = await Resolve(http);
            var prompt = await TranscriptionPromptAsync(db, c.User.Id, req.TargetId, ct);
            return Results.Ok(await s.StartAsync(c.User, c.Plan, req, prompt, ct));
        });
        authed.MapPost("/sessions/{id:guid}/heartbeat", async (HttpContext http, Guid id, HeartbeatRequest req, SessionService s, CancellationToken ct) =>
        { var c = await Resolve(http); return Results.Ok(await s.HeartbeatAsync(c.User, c.Plan, id, req.Sequence, ct)); });
        authed.MapPost("/sessions/{id:guid}/realtime-secret", async (HttpContext http, Guid id, string? language, SessionService s, AppDbContext db, CancellationToken ct) =>
        {
            var c = await Resolve(http);
            var target = await db.Sessions.Where(x => x.Id == id && x.UserId == c.User.Id).Select(x => x.TargetId).FirstOrDefaultAsync(ct);
            return Results.Ok(await s.RenewSecretAsync(c.User, c.Plan, id, await TranscriptionPromptAsync(db, c.User.Id, target, ct), language, ct));
        });
        authed.MapPost("/sessions/{id:guid}/stop", async (HttpContext http, Guid id, SessionService s, CancellationToken ct) =>
        { var c = await Resolve(http); await s.StopAsync(c.User, c.Plan, id, ct); return Results.NoContent(); });

        // ---------------- streamed answers (server key, server-chosen model) ----------------
        authed.MapPost("/answers/stream", async (HttpContext http, AnswerStreamRequest req, SessionService sessions, UsageService usage, ConfigService config,
            IAnswerProviderFactory providers, AppDbContext db, IOptions<SafetyOptions> safety, CancellationToken ct) =>
        {
            var c = await Resolve(http);
            if (req.Purpose == "live")
            {
                if (req.SessionId == null || !await sessions.IsActiveAsync(c.User.Id, req.SessionId.Value, ct)) throw ApiException.Forbidden("no_active_session", "Start an interview session first.");
            }
            else if (req.Purpose == "prep")
            {
                var job = req.PrepJobId == null ? null : await db.PreparationJobs.FirstOrDefaultAsync(j => j.Id == req.PrepJobId && j.UserId == c.User.Id, ct);
                if (job == null) throw ApiException.Forbidden("no_prep_job", "Start a preparation job first.");
                if (++job.AiRequests > 80) throw ApiException.TooMany("prep_ai_limit", "This preparation used its AI allowance.");
            }
            else throw ApiException.BadRequest("invalid_purpose", "Purpose must be 'live' or 'prep'.");

            if (await usage.SumAsync(c.User.Id, UsageService.AnswerRequest, usage.TodayUtc, usage.TodayUtc.AddDays(1), ct) >= safety.Value.AnswersPerDayPerAccount)
                throw ApiException.TooMany("daily_cap", "Daily answer limit reached for this account.");
            if (await usage.GlobalSumAsync(UsageService.AnswerRequest, usage.TodayUtc, ct) >= safety.Value.GlobalAnswersPerDay)
                throw new ApiException(503, "global_cap", "The service is at capacity. Prepared answers still work.");
            var messages = AnswerRequestGuard.Sanitize(req);
            var rc = await config.GetAsync(ct);
            usage.Record(c.User.Id, UsageService.AnswerRequest, 1, req.SessionId);
            await db.SaveChangesAsync(ct);

            var provider = providers.Create(rc.AnswerModel, rc.AnswerFallbacks, rc.FirstTokenTimeoutMs);
            var maxTokens = Math.Clamp(req.MaxTokens, 16, safety.Value.MaxAnswerTokens);
            await using var stream = provider.StreamAsync(messages, maxTokens, ct).GetAsyncEnumerator(ct);
            bool hasFirst;
            try { hasFirst = await stream.MoveNextAsync(); }   // errors before the first token → proper HTTP status
            catch (ProviderException ex) { throw new ApiException(ex.Kind == ProviderErrorKind.RateLimited ? 429 : 502, "ai_unavailable", "The AI service is temporarily unavailable."); }
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            async Task Write(string delta) { await http.Response.WriteAsync("data: " + JsonSerializer.Serialize(new { delta }) + "\n\n", ct); await http.Response.Body.FlushAsync(ct); }
            if (hasFirst)
            {
                await Write(stream.Current);
                try { while (await stream.MoveNextAsync()) await Write(stream.Current); }
                catch (ProviderException) { await http.Response.WriteAsync("event: error\ndata: {\"code\":\"ai_interrupted\"}\n\n", ct); }
            }
            await http.Response.WriteAsync("data: [DONE]\n\n", ct);
            return Results.Empty;
        }).RequireRateLimiting("answers");

        // ---------------- billing ----------------
        authed.MapPost("/billing/checkout", async (HttpContext http, CheckoutRequest req, IBillingProvider billing, IOptions<PlanCatalog> plans, CancellationToken ct) =>
        {
            var c = await Resolve(http);
            var plan = plans.Value.Plans.FirstOrDefault(p => p.Code == req.PlanCode && p.Purchasable) ?? throw ApiException.BadRequest("unknown_plan", "Unknown plan.");
            return Results.Ok(new UrlResponse(await billing.CreateCheckoutUrlAsync(c.User, plan, c.Sub?.ProviderCustomerId, ct)));
        });
        authed.MapPost("/billing/portal", async (HttpContext http, IBillingProvider billing, CancellationToken ct) =>
        {
            var c = await Resolve(http);
            if (c.Sub?.ProviderCustomerId == null) throw ApiException.NotFound();
            return Results.Ok(new UrlResponse(await billing.CreatePortalUrlAsync(c.Sub.ProviderCustomerId, c.Sub.ProviderSubscriptionId, ct)));
        });
        api.MapPost("/webhooks/paddle", async (HttpContext http, BillingWebhookProcessor proc, IOptions<PaddleOptions> opt, TimeProvider time, ILoggerFactory lf, CancellationToken ct) =>
        {
            http.Request.EnableBuffering();
            if (http.Request.ContentLength > 1_000_000) throw new ApiException(413, "too_large", "Payload too large.");
            using var reader = new StreamReader(http.Request.Body, Encoding.UTF8);
            var raw = await reader.ReadToEndAsync(ct);   // verify the EXACT raw body
            if (!PaddleSignature.Verify(http.Request.Headers["Paddle-Signature"], raw, opt.Value.WebhookSecret, time.GetUtcNow(), opt.Value.SignatureToleranceSeconds, out var reason))
            {
                lf.CreateLogger("webhooks").LogWarning("Rejected Paddle webhook: {Reason}", reason);
                return Results.Json(new ApiError("invalid_signature", "Invalid signature."), statusCode: 401);
            }
            var outcome = await proc.ProcessAsync(raw, ct);
            return Results.Ok(new { outcome = outcome.ToString().ToLowerInvariant() });
        }).RequireRateLimiting("anon");

        AdminEndpoints.Map(authed);
    }

    /// <summary>Transcription bias prompt from the user's own target document (role/company/vocabulary), never from other users.</summary>
    private static async Task<string> TranscriptionPromptAsync(AppDbContext db, Guid userId, Guid? targetId, CancellationToken ct)
    {
        if (targetId == null) return "Job interview. Professional business and technology vocabulary.";
        var json = await db.Documents.Where(d => d.Id == targetId && d.UserId == userId && d.Kind == "target").Select(d => d.DataJson).FirstOrDefaultAsync(ct);
        if (json == null) return "Job interview.";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var vocab = r.TryGetProperty("vocabulary", out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Take(60) : Enumerable.Empty<string>();
            var p = $"Job interview for a {S("jobTitle")} role{(S("company").Length > 0 ? " at " + S("company") : "")}. Terms: {string.Join(", ", vocab)}.";
            return p.Length > 1500 ? p[..1500] : p;
        }
        catch (JsonException) { return "Job interview."; }
    }
}

public static class AdminEndpoints
{
    /// <summary>Operator surface. Never returns résumé/profile CONTENT (only counts/metadata). Every call is audited.</summary>
    public static void Map(RouteGroupBuilder authed)
    {
        var admin = authed.MapGroup("/admin").AddEndpointFilter(async (ctx, next) =>
        {
            var users = ctx.HttpContext.RequestServices.GetRequiredService<UserService>();
            if (!users.IsAdmin(ctx.HttpContext.User)) throw ApiException.Forbidden("not_admin", "Administrator access required.");
            var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
            db.Audit.Add(new AuditEntry { ActorUserId = UserService.UserId(ctx.HttpContext.User), Action = ctx.HttpContext.Request.Method + " " + ctx.HttpContext.Request.Path, AtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
            return await next(ctx);
        });

        admin.MapGet("/users", async (string? email, AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Users.AsNoTracking().Where(u => email == null || u.Email == email).OrderByDescending(u => u.CreatedUtc).Take(50)
                .Select(u => new { u.Id, u.Email, u.Status, u.CreatedUtc }).ToListAsync(ct)));
        admin.MapGet("/users/{id:guid}", async (Guid id, AppDbContext db, EntitlementService ents, UsageService usage, CancellationToken ct) =>
        {
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound();
            var (plan, sub) = await ents.ResolveAsync(u, ct);
            return Results.Ok(new
            {
                user = new { u.Id, u.Email, u.Status, u.CreatedUtc }, entitlements = EntitlementService.ToDto(plan), subscription = sub,
                usage = await usage.SummaryAsync(u.Id, plan, ct),
                devices = await db.Devices.AsNoTracking().Where(d => d.UserId == id).Select(d => new { d.Id, d.Name, d.Platform, d.ClientVersion, d.LastSeenUtc, d.RevokedUtc }).ToListAsync(ct),
                sessions = await db.Sessions.AsNoTracking().Where(s => s.UserId == id).OrderByDescending(s => s.StartedUtc).Take(20).Select(s => new { s.Id, s.Status, s.StartedUtc, s.MeteredSeconds, s.EndReason, s.SecretsIssued }).ToListAsync(ct),
                documentCounts = await db.Documents.AsNoTracking().Where(d => d.UserId == id).GroupBy(d => d.Kind).Select(g => new { kind = g.Key, count = g.Count() }).ToListAsync(ct),
            });
        });
        admin.MapPost("/users/{id:guid}/disable", async (Guid id, AppDbContext db, TimeProvider time, CancellationToken ct) => await SetStatus(db, time, id, "disabled", ct));
        admin.MapPost("/users/{id:guid}/enable", async (Guid id, AppDbContext db, TimeProvider time, CancellationToken ct) => await SetStatus(db, time, id, "active", ct));
        admin.MapGet("/sessions/active", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Sessions.AsNoTracking().Where(s => s.Status == "active").Select(s => new { s.Id, s.UserId, s.DeviceId, s.StartedUtc, s.LastHeartbeatUtc, s.MeteredSeconds }).ToListAsync(ct)));
        admin.MapGet("/config", async (ConfigService c, AppDbContext db, CancellationToken ct) => Results.Ok(new { effective = await c.GetAsync(ct), overrides = await db.Config.AsNoTracking().ToListAsync(ct) }));
        admin.MapPut("/config/{key}", async (HttpContext http, string key, JsonElement value, ConfigService c, TimeProvider time, Microsoft.Extensions.Caching.Memory.IMemoryCache cache, CancellationToken ct) =>
        {
            var allowed = new[] { "transcription_model", "transcription_fallbacks", "answer_model", "answer_fallbacks", "preparation_model", "answer_max_tokens", "first_token_timeout_ms", "features", "disabled_models", "maintenance", "maintenance_message", "minimum_client_version" };
            if (!allowed.Contains(key)) throw ApiException.BadRequest("unknown_key", "Unknown config key.");
            await c.SetAsync(key, value, UserService.UserId(http.User).ToString(), time, ct);
            cache.Remove("cfg");
            return Results.Ok(await c.GetAsync(ct));
        });
        admin.MapGet("/health", async (AppDbContext db, CancellationToken ct) => Results.Ok(new
        {
            db = await db.Database.CanConnectAsync(ct),
            activeSessions = await db.Sessions.CountAsync(s => s.Status == "active", ct),
            failedWebhooks24h = await db.WebhookEvents.CountAsync(w => w.Status == "failed" && w.ReceivedUtc > DateTime.UtcNow.AddDays(-1), ct),
        }));
    }

    private static async Task<IResult> SetStatus(AppDbContext db, TimeProvider time, Guid id, string status, CancellationToken ct)
    {
        var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound();
        if (u.Status == "deleted") throw ApiException.Conflict("deleted", "Account was deleted.");
        var now = time.GetUtcNow().UtcDateTime;
        u.Status = status; u.UpdatedUtc = now;
        if (status == "disabled") foreach (var s in await db.Sessions.Where(s => s.UserId == id && s.Status == "active").ToListAsync(ct)) { s.Status = "stopped"; s.EndReason = "account_disabled"; s.EndedUtc = now; }
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { u.Id, u.Status });
    }
}
