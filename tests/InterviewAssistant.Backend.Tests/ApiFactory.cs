using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using InterviewAssistant.Backend.Data;
using InterviewAssistant.Backend.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace InterviewAssistant.Backend.Tests;

/// <summary>Real ASP.NET pipeline + real PostgreSQL (fresh database per fixture, created by the real migrations).</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string Issuer = "https://test-project.supabase.co/auth/v1";
    public const string JwtSecret = "test-jwt-secret-that-is-long-enough-for-hs256-0123456789"; // fake-credential: test fixture
    public const string ServerOpenAiKey = "sk-server-only-key-ABCDEFGHIJKLMNOP"; // fake-credential: test fixture
    public const string WebhookSecret = "pdl_ntfset_test_secret"; // fake-credential: test fixture
    public static readonly Guid AdminId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    public readonly string Database = "ia_test_" + Guid.NewGuid().ToString("n")[..12];
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero));
    public StubHandler OpenAi { get; } = new();
    public StubHandler Paddle { get; } = new();

    public static string PgHost => Environment.GetEnvironmentVariable("IA_TEST_PG") ?? "Host=localhost;Username=ia_test;Password=ia_test";
    private string ConnectionString => $"{PgHost};Database={Database}";

    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        b.UseEnvironment("Testing");
        b.UseSetting("ConnectionStrings:Db", ConnectionString);
        b.UseSetting("Auth:Issuer", Issuer);
        b.UseSetting("Auth:JwtSecret", JwtSecret);
        b.UseSetting("Auth:AdminUserIds:0", AdminId.ToString());
        b.UseSetting("OpenAI:ApiKey", ServerOpenAiKey);
        b.UseSetting("Paddle:ApiKey", "pdl_sdbx_apikey_test");
        b.UseSetting("Paddle:WebhookSecret", WebhookSecret);
        b.UseSetting("PlanCatalog:Plans:1:PaddlePriceIds:0", "pri_test_pro");
        b.UseSetting("Safety:AnswersPerMinute", "5");
        b.UseSetting("Safety:PerAccountDailyLiveMinutes", "240");
        b.ConfigureTestServices(s =>
        {
            s.AddSingleton<TimeProvider>(Time);
            foreach (var d in s.Where(d => d.ImplementationType == typeof(SessionSweeper)).ToList()) s.Remove(d);
            s.AddHttpClient("openai").ConfigurePrimaryHttpMessageHandler(() => OpenAi);
            s.AddHttpClient<IRealtimeSecretIssuer, OpenAiRealtimeSecretIssuer>("openai-secrets").ConfigurePrimaryHttpMessageHandler(() => OpenAi);
            s.AddHttpClient<IBillingProvider, PaddleBillingProvider>("paddle").ConfigurePrimaryHttpMessageHandler(() => Paddle);
        });
    }

    public async Task InitializeAsync()
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync(); // the real migrations
        OpenAi.Respond = req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/realtime/client_secrets")) return Json(new { value = "ek_test_" + Guid.NewGuid().ToString("n")[..8], expires_at = Time.GetUtcNow().AddMinutes(10).ToUnixTimeSeconds() });
            if (path.EndsWith("/chat/completions"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"MODE: HYPOTHETICAL\\n• I'd start with the goal.\\n\"}}]}\n\ndata: [DONE]\n\n") };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        Paddle.Respond = req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/transactions") return Json(new { data = new { id = "txn_1", checkout = new { url = "https://sandbox-checkout.paddle.test/txn_1" } } });
            if (path.EndsWith("/portal-sessions")) return Json(new { data = new { urls = new { general = new { overview = "https://sandbox-customer-portal.paddle.test/cpl_1" } } } });
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        NpgsqlDrop();
    }

    private void NpgsqlDrop()
    {
        using var conn = new Npgsql.NpgsqlConnection($"{PgHost};Database=postgres");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP DATABASE IF EXISTS {Database} WITH (FORCE)";
        cmd.ExecuteNonQuery();
    }

    public static HttpResponseMessage Json(object o) => new(HttpStatusCode.OK) { Content = JsonContent.Create(o) };

    public string Token(Guid userId, string? email = null, DateTime? expires = null, string? issuer = null, string? secret = null, string audience = "authenticated")
    {
        var now = Time.GetUtcNow().UtcDateTime;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer ?? Issuer, Audience = audience,
            Subject = new ClaimsIdentity(new[] { new Claim("sub", userId.ToString()), new Claim("email", email ?? $"{userId:n}@example.test"), new Claim("role", "authenticated") }),
            NotBefore = now.AddMinutes(-1), IssuedAt = now.AddMinutes(-1), Expires = expires ?? now.AddHours(1),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret ?? JwtSecret)), SecurityAlgorithms.HmacSha256),
        });
    }

    public HttpClient ClientFor(Guid userId, string? version = "2.0.0")
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(userId));
        if (version != null) c.DefaultRequestHeaders.Add("X-Client-Version", version);
        return c;
    }
}

public sealed class StubHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
    public ConcurrentQueue<(HttpRequestMessage Request, string Body)> Requests { get; } = new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Enqueue((request, request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct)));
        return Respond(request);
    }
}
