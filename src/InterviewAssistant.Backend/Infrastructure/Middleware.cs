using System.Text.Json;
using System.Text.RegularExpressions;
using InterviewAssistant.Backend.Services;
using InterviewAssistant.Contracts;
using Microsoft.Extensions.Caching.Memory;

namespace InterviewAssistant.Backend.Infrastructure;

public static class Middleware
{
    private static readonly Regex SafeCorrelation = new(@"^[A-Za-z0-9\-]{8,64}$", RegexOptions.Compiled);

    /// <summary>Correlation id (desktop → backend → provider logs), security headers, structured errors, client-version gate.</summary>
    public static IApplicationBuilder UseApiPipeline(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var cid = ctx.Request.Headers["X-Correlation-Id"].ToString();
        if (!SafeCorrelation.IsMatch(cid)) cid = Guid.NewGuid().ToString("n");
        ctx.Items["cid"] = cid;
        ctx.Response.Headers["X-Correlation-Id"] = cid;
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["X-Frame-Options"] = "DENY";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        ctx.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
        ctx.Response.Headers["Cache-Control"] = "no-store";
        if (ctx.Request.IsHttps) ctx.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
        var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("api");
        using var scope = log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = cid });
        try
        {
            var path = ctx.Request.Path.Value ?? "";
            if (path.StartsWith("/api/v1/") && !path.StartsWith("/api/v1/config") && !path.StartsWith("/api/v1/webhooks") && !path.StartsWith("/api/v1/billing/plans") && !path.StartsWith("/api/v1/admin"))
            {
                var cfg = await ctx.RequestServices.GetRequiredService<IMemoryCache>().GetOrCreateAsync("cfg", async e =>
                {
                    e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(15);
                    return await ctx.RequestServices.GetRequiredService<ConfigService>().GetAsync(ctx.RequestAborted);
                });
                var clientVersion = ctx.Request.Headers["X-Client-Version"].ToString();
                if (clientVersion.Length > 0 && !ConfigService.VersionAtLeast(clientVersion, cfg!.MinimumClientVersion))
                    throw new ApiException(426, "client_update_required", $"Please update the app (minimum version {cfg.MinimumClientVersion}).");
            }
            await next();
        }
        catch (ApiException ex)
        {
            if (ctx.Response.HasStarted) { log.LogWarning("API error after response started: {Code}", ex.Code); return; }
            ctx.Response.StatusCode = ex.Status;
            await ctx.Response.WriteAsJsonAsync(new ApiError(ex.Code, ex.Message, cid));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Unhandled error");   // message + stack only; request bodies are never logged
            if (ctx.Response.HasStarted) return;
            ctx.Response.StatusCode = 500;
            await ctx.Response.WriteAsJsonAsync(new ApiError("internal_error", "Something went wrong. Please try again.", cid));
        }
    });

    /// <summary>Redacts secrets/PII from arbitrary strings before logging.</summary>
    public static string Redact(string s) => Regex.Replace(Regex.Replace(Regex.Replace(s,
        @"(sk-|ek_|pdl_|Bearer\s+)[A-Za-z0-9_\-\.]{6,}", "$1***"),
        @"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", "***@***"),
        @"eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+", "***jwt***");

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
