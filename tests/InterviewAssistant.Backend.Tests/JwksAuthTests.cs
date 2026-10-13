using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace InterviewAssistant.Backend.Tests;

/// <summary>Production auth path: asymmetric (RS256) Supabase tokens verified against the JWKS.</summary>
public sealed class JwksApiFactory : ApiFactory
{
    public RSA Signing { get; } = RSA.Create(2048);
    public RSA Rogue { get; } = RSA.Create(2048);
    public StubHandler Jwks { get; } = new();

    protected override void Customize(IWebHostBuilder b)
    {
        b.UseSetting("Auth:JwtSecret", "");
        b.UseSetting("Auth:JwksUrl", "https://test-project.supabase.co/auth/v1/.well-known/jwks.json");
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(Signing.ExportParameters(false)) { KeyId = "key-1" });
        var json = JsonSerializer.Serialize(new { keys = new[] { new { kty = jwk.Kty, kid = jwk.Kid, use = "sig", alg = "RS256", n = jwk.N, e = jwk.E } } });
        Jwks.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        b.ConfigureTestServices(s => s.AddHttpClient("jwks").ConfigurePrimaryHttpMessageHandler(() => Jwks));
    }

    public string RsToken(Guid user, RSA key, string kid, string issuer = Issuer, string audience = "authenticated", DateTime? expires = null)
    {
        var now = Time.GetUtcNow().UtcDateTime;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer, Audience = audience, Subject = new ClaimsIdentity(new[] { new Claim("sub", user.ToString()), new Claim("email", "u@example.test") }),
            NotBefore = now.AddMinutes(-1), IssuedAt = now.AddMinutes(-1), Expires = expires ?? now.AddHours(1),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(key) { KeyId = kid }, SecurityAlgorithms.RsaSha256),
        });
    }
}

public class JwksAuthTests : IClassFixture<JwksApiFactory>
{
    private readonly JwksApiFactory _f;
    public JwksAuthTests(JwksApiFactory f) => _f = f;

    private async Task<HttpStatusCode> Call(string token)
    {
        var c = _f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (await c.GetAsync("/api/v1/me")).StatusCode;
    }

    [Fact] public async Task ValidRs256TokenIsAccepted() => Assert.Equal(HttpStatusCode.OK, await Call(_f.RsToken(Guid.NewGuid(), _f.Signing, "key-1")));
    [Fact] public async Task TokenFromUnknownKeyIsRejected() => Assert.Equal(HttpStatusCode.Unauthorized, await Call(_f.RsToken(Guid.NewGuid(), _f.Rogue, "rogue-key")));
    [Fact] public async Task TokenWithKnownKidButWrongKeyIsRejected() => Assert.Equal(HttpStatusCode.Unauthorized, await Call(_f.RsToken(Guid.NewGuid(), _f.Rogue, "key-1")));
    [Fact] public async Task ExpiredTokenIsRejected() => Assert.Equal(HttpStatusCode.Unauthorized, await Call(_f.RsToken(Guid.NewGuid(), _f.Signing, "key-1", expires: _f.Time.GetUtcNow().UtcDateTime.AddMinutes(-5))));
    [Fact] public async Task WrongIssuerIsRejected() => Assert.Equal(HttpStatusCode.Unauthorized, await Call(_f.RsToken(Guid.NewGuid(), _f.Signing, "key-1", issuer: "https://other.supabase.co/auth/v1")));
    [Fact] public async Task WrongAudienceIsRejected() => Assert.Equal(HttpStatusCode.Unauthorized, await Call(_f.RsToken(Guid.NewGuid(), _f.Signing, "key-1", audience: "anon")));

    [Fact]
    public async Task ReadinessRequiresSigningKeys()
    {
        var r = await _f.CreateClient().GetStringAsync("/health/ready");
        Assert.Contains("ready", r);
    }

    [Fact]
    public async Task IdentityComesFromTheVerifiedSubjectOnly()
    {
        var id = Guid.NewGuid();
        var c = _f.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _f.RsToken(id, _f.Signing, "key-1"));
        c.DefaultRequestHeaders.Add("X-User-Id", Guid.NewGuid().ToString());       // ignored
        var me = await (await c.GetAsync("/api/v1/me")).Read<InterviewAssistant.Contracts.AccountDto>();
        Assert.Equal(id, me.UserId);
    }
}
