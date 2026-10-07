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
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

builder.Services.Configure<AuthOptions>(cfg.GetSection("Auth"));
builder.Services.Configure<OpenAiOptions>(cfg.GetSection("OpenAI"));
builder.Services.Configure<PaddleOptions>(cfg.GetSection("Paddle"));
builder.Services.Configure<SafetyOptions>(cfg.GetSection("Safety"));
builder.Services.Configure<PlanCatalog>(cfg.GetSection("PlanCatalog"));
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
    .Configure<IOptions<AuthOptions>, TimeProvider, IHostEnvironment>((o, authOptions, clock, env) =>
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
        o.TokenValidationParameters.IssuerSigningKeyResolver = (_, _, kid, _) => JwksCache.GetKeys(auth.JwksUrl, kid);
});
builder.Services.AddAuthorization();

// ---- Rate limits: per-user answer generation + per-IP anonymous endpoints ----
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.OnRejected = async (ctx, ct) => await ctx.HttpContext.Response.WriteAsJsonAsync(new InterviewAssistant.Contracts.ApiError("rate_limited", "Too many requests — slow down.", ctx.HttpContext.Items["cid"] as string), ct);
    o.AddPolicy("answers", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ctx.User.FindFirstValue("sub") ?? "anon",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = ctx.RequestServices.GetRequiredService<IOptions<SafetyOptions>>().Value.AnswersPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("anon", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "ip",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 240, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

var app = builder.Build();
app.UseApiPipeline();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

if (cfg.GetValue("Database:MigrateOnStartup", false))
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
// Readiness: database reachable and no pending migrations (used by staging smoke tests and load balancers).
app.MapGet("/health/ready", async (AppDbContext db, CancellationToken ct) =>
{
    if (!await db.Database.CanConnectAsync(ct)) return Results.Json(new { status = "database_unreachable" }, statusCode: 503);
    var pending = (await db.Database.GetPendingMigrationsAsync(ct)).Count();
    return pending == 0 ? Results.Ok(new { status = "ready" }) : Results.Json(new { status = "migrations_pending", pending }, statusCode: 503);
});
app.MapApi();
app.Run();

public partial class Program { }
