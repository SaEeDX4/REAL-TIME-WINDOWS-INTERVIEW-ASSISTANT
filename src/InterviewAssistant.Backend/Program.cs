using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using InterviewAssistant.Backend.Data;
using InterviewAssistant.Backend.Endpoints;
using InterviewAssistant.Backend.Infrastructure;
using InterviewAssistant.Backend.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

builder.Services.Configure<AuthOptions>(cfg.GetSection("Auth"));
builder.Services.Configure<OpenAiOptions>(cfg.GetSection("OpenAI"));
builder.Services.Configure<PaddleOptions>(cfg.GetSection("Paddle"));
builder.Services.Configure<SafetyOptions>(cfg.GetSection("Safety"));
builder.Services.Configure<PlanCatalog>(cfg.GetSection("PlanCatalog"));
builder.Services.Configure<ProductOptions>(cfg.GetSection("Product"));

// ---- Fail fast on misconfiguration (production requires real secrets/ids; development and tests are lenient) ----
var production = builder.Environment.IsProduction();
var configErrors = ConfigValidation.Validate(cfg, production,
    cfg.GetSection("Auth").Get<AuthOptions>() ?? new(), cfg.GetSection("OpenAI").Get<OpenAiOptions>() ?? new(),
    cfg.GetSection("Paddle").Get<PaddleOptions>() ?? new(), cfg.GetSection("PlanCatalog").Get<PlanCatalog>() ?? new(),
    cfg.GetSection("Product").Get<ProductOptions>() ?? new());
if (configErrors.Count > 0)
    throw new InvalidOperationException("Invalid configuration:\n - " + string.Join("\n - ", configErrors));

if (production)
{
    // One JSON object per line: Railway/any log pipeline can filter by level, category and structured fields.
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole(o => { o.IncludeScopes = true; o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ"; o.UseUtcTimestamp = true; });
}
// No cookies or antiforgery are used; keep ASP.NET data-protection keys in memory instead of unencrypted files.
builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();

// Behind Railway's (or any) TLS-terminating proxy: real client IP for rate limits, https scheme for HSTS.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.ForwardLimit = 1;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});
// The container listens on 8080 (current public-domain target); when the platform injects a different $PORT (Railway's
// healthcheck/router), listen there as well so neither path can break.
if (Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } platformPort && int.TryParse(platformPort, out var pp) && pp != 8080 && pp is > 0 and < 65536)
    builder.WebHost.UseUrls("http://+:8080", $"http://+:{pp}");
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = cfg.GetValue("Safety:MaxRequestBodyBytes", 4 * 1024 * 1024));
builder.Services.AddHttpClient("jwks", c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<JwksProvider>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<JwksProvider>());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMemoryCache();
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(cfg.GetConnectionString("Db")));
builder.Services.AddHttpClient("openai", c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddHttpClient<IRealtimeSecretIssuer, OpenAiRealtimeSecretIssuer>("openai-secrets", c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHttpClient<IBillingProvider, PaddleBillingProvider>("paddle", c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddSingleton<IAnswerProviderFactory, OpenAiAnswerProviderFactory>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<EntitlementService>();
builder.Services.AddScoped<UsageService>();
builder.Services.AddScoped<DeviceService>();
builder.Services.AddScoped<SessionService>();
builder.Services.AddScoped<ConfigService>();
builder.Services.AddScoped<DocumentService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<BillingWebhookProcessor>();
builder.Services.AddHostedService<SessionSweeper>();

// ---- Authentication: Supabase-issued JWTs (JWKS asymmetric keys, or legacy HS256 secret) ----
// Resolved lazily from DI so configuration overrides (tests, environment) and the injected clock are honoured.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<AuthOptions>, TimeProvider, IHostEnvironment, JwksProvider>((o, authOptions, clock, env, jwks) =>
{
    var auth = authOptions.Value;
    o.RequireHttpsMetadata = !env.IsDevelopment() && !env.IsEnvironment("Testing");
    o.MapInboundClaims = false;
    o.TimeProvider = clock;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = auth.Issuer,
        ValidateAudience = true, ValidAudience = auth.Audience,
        ValidateLifetime = true, RequireExpirationTime = true, ClockSkew = TimeSpan.FromSeconds(30),
        ValidateIssuerSigningKey = true,
        LifetimeValidator = (nbf, exp, _, p) =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            return exp.HasValue && exp.Value.ToUniversalTime() + p.ClockSkew > now && (!nbf.HasValue || nbf.Value.ToUniversalTime() - p.ClockSkew <= now);
        },
    };
    if (!string.IsNullOrEmpty(auth.JwtSecret))
        o.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(auth.JwtSecret));
    else if (!string.IsNullOrEmpty(auth.JwksUrl))
        o.TokenValidationParameters.IssuerSigningKeyResolver = (_, _, kid, _) => jwks.Resolve(kid);
    o.Events = new JwtBearerEvents
    {
        // Reason only — the token itself is never logged.
        OnAuthenticationFailed = ctx =>
        {
            ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("auth")
                .LogWarning("JWT rejected: {Reason}", ctx.Exception.GetType().Name);
            return Task.CompletedTask;
        },
    };
});
builder.Services.AddAuthorization();

// ---- Rate limits: per-user answer generation + per-IP anonymous endpoints ----
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("answers", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ctx.User.FindFirstValue("sub") ?? "anon",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = ctx.RequestServices.GetRequiredService<IOptions<SafetyOptions>>().Value.AnswersPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("anon", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "ip",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 240, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    // Every authenticated route: a generous per-user ceiling (normal use is a few requests per minute).
    o.AddPolicy("user", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ctx.User.FindFirstValue("sub") ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 300, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    // Routes that mint transcription credentials or call billing: tight per-user bucket.
    o.AddPolicy("credentials", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ctx.User.FindFirstValue("sub") ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    // Paddle may burst-deliver; per source IP so an unsigned flood from one address cannot starve Paddle's deliveries.
    o.AddPolicy("webhooks", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "paddle",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ratelimit").LogWarning("Rate limit hit on {Path}", ctx.HttpContext.Request.Path.Value);
        await ctx.HttpContext.Response.WriteAsJsonAsync(new InterviewAssistant.Contracts.ApiError("rate_limited", "Too many requests — please wait a moment.", ctx.HttpContext.Items["cid"] as string), ct);
    };
});
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

var app = builder.Build();
app.UseForwardedHeaders();
app.UseApiPipeline();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("startup");
// Railway uses Database__MigrateOnStart; Database__MigrateOnStartup is accepted too. Migrations are additive and EF holds a
// migration lock, so concurrent instances cannot apply them twice. Production data is never dropped.
if (cfg.GetValue("Database:MigrateOnStartup", false) || cfg.GetValue("Database:MigrateOnStart", false))
{
    // The database may still be starting (deploy order, failover): retry with backoff instead of crash-looping.
    for (int attempt = 1; ; attempt++)
    {
        try
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
            if (pending.Count > 0) { startupLog.LogInformation("Applying {Count} migration(s): {Names}", pending.Count, string.Join(", ", pending)); await db.Database.MigrateAsync(); }
            else startupLog.LogInformation("Database schema up to date");
            break;
        }
        catch (Exception ex) when (attempt < 6 && ex is Npgsql.NpgsqlException or System.Net.Sockets.SocketException or TimeoutException)
        {
            startupLog.LogWarning("Database not reachable for migrations (attempt {Attempt}/6: {Error}); retrying", attempt, ex.GetType().Name);
            await Task.Delay(TimeSpan.FromSeconds(2 << attempt));
        }
    }
}
await app.Services.GetRequiredService<JwksProvider>().RefreshAsync(CancellationToken.None);
{
    var a = app.Services.GetRequiredService<IOptions<AuthOptions>>().Value;
    var p = app.Services.GetRequiredService<IOptions<PaddleOptions>>().Value;
    var plans = app.Services.GetRequiredService<IOptions<PlanCatalog>>().Value;
    startupLog.LogInformation("Starting in {Environment}: auth {AuthMode} (desktop sign-in {SignIn}), billing {Billing} (checkout page {Checkout}), plans {Plans}",
        app.Environment.EnvironmentName, a.JwksUrl.Length > 0 ? "JWKS" : "HS256", a.PublishableKey.Length > 0 ? "configured" : "NOT CONFIGURED (set Auth__PublishableKey)",
        p.Environment, p.ClientToken.Length > 0 ? "configured" : "NOT CONFIGURED (set Paddle__ClientToken)", string.Join(", ", plans.Plans.Select(x => x.Code)));
}

// Liveness: no dependencies. "version" = deployed commit (Railway injects RAILWAY_GIT_COMMIT_SHA) so CI can verify the rollout.
var deployedVersion = (Environment.GetEnvironmentVariable("RAILWAY_GIT_COMMIT_SHA") ?? Environment.GetEnvironmentVariable("APP_VERSION") ?? "dev") is var v && v.Length > 12 ? v[..12] : v;
app.MapGet("/health", () => Results.Ok(new { status = "ok", version = deployedVersion }));
// Readiness: database reachable and no pending migrations (used by staging smoke tests and load balancers).
app.MapGet("/health/ready", async (AppDbContext db, JwksProvider jwks, CancellationToken ct) =>
{
    if (!await db.Database.CanConnectAsync(ct)) return Results.Json(new { status = "database_unreachable" }, statusCode: 503);
    if (jwks.Enabled && jwks.KeyCount == 0) return Results.Json(new { status = "auth_keys_unavailable" }, statusCode: 503);
    var pending = (await db.Database.GetPendingMigrationsAsync(ct)).Count();
    return pending == 0 ? Results.Ok(new { status = "ready" }) : Results.Json(new { status = "migrations_pending", pending }, statusCode: 503);
});
app.MapApi();
app.Run();

public partial class Program { }
