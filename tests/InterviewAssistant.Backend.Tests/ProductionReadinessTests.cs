using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InterviewAssistant.Backend.Infrastructure;
using InterviewAssistant.Contracts;
using Microsoft.Extensions.Configuration;

namespace InterviewAssistant.Backend.Tests;

/// <summary>Billing lifecycle, billing state, public config and checkout surfaces as a paying customer would hit them.</summary>
public class ProductionReadinessTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public ProductionReadinessTests(ApiFactory f) => _f = f;

    private async Task<(Guid Id, HttpClient C)> NewUser() { var id = Guid.NewGuid(); var c = _f.ClientFor(id); (await c.GetAsync("/api/v1/me")).EnsureSuccessStatusCode(); return (id, c); }
    private Task<AccountDto> Me(HttpClient c) => c.GetAsync("/api/v1/me").ContinueWith(t => t.Result.Read<AccountDto>()).Unwrap();
    private Task<HttpResponseMessage> Event(string type, object data, DateTimeOffset? at = null) => _f.PaddleEvent("evt_" + Guid.NewGuid().ToString("n"), type, data, at);

    [Fact]
    public async Task PlansArePublicProductionNamedAndPricedFromPaddle()
    {
        var plans = await (await _f.CreateClient().GetAsync("/api/v1/billing/plans")).Read<List<PlanDto>>();
        Assert.Equal(new[] { "trial", "pro" }, plans.Select(p => p.Code));
        Assert.Equal(new[] { "Trial", "Pro" }, plans.Select(p => p.Name));
        Assert.DoesNotContain(plans, p => p.Name.Contains("seed", StringComparison.OrdinalIgnoreCase) || p.Code.Contains("dev"));
        Assert.Equal("USD 19.99 / month", plans.Single(p => p.Code == "pro").PriceDisplay);
        Assert.Null(plans.Single(p => p.Code == "trial").PriceDisplay);
        Assert.Equal(30, plans.Single(p => p.Code == "trial").Entitlements.LiveMinutesPerPeriod);
    }

    [Fact]
    public async Task PublicConfigCarriesOnlyPublishableValues()
    {
        var raw = await _f.CreateClient().GetStringAsync("/api/v1/config");
        var cfg = JsonSerializer.Deserialize<RuntimeConfig>(raw, Helpers.Json)!;
        Assert.True(cfg.Auth!.Available);
        Assert.Equal("https://test-project.supabase.co", cfg.Auth.SupabaseUrl);
        Assert.Equal(ApiFactory.PublishableKey, cfg.Auth.PublishableKey);
        Assert.Equal("https://api.example.test/auth/confirmed", cfg.Auth.EmailConfirmedRedirectUrl);
        Assert.Equal("https://example.test/privacy", cfg.Links!.PrivacyPolicy);
        Assert.Null(cfg.Links.Terms);                                       // not configured → not invented
        Assert.Equal("sandbox", cfg.BillingEnvironment);
        Assert.Equal("gpt-4o-transcribe", cfg.TranscriptionModel);
        foreach (var secret in new[] { ApiFactory.ServerOpenAiKey, ApiFactory.WebhookSecret, ApiFactory.JwtSecret, "pdl_sdbx_apikey_test" })
            Assert.DoesNotContain(secret, raw);
    }

    [Fact]
    public async Task TrialUserSeesTrialThenExhaustedState()
    {
        var (_, c) = await NewUser();
        Assert.Equal(BillingStates.Trial, (await Me(c)).BillingState);
        var dev = await c.RegisterDevice();
        var s = await (await c.StartSession(dev.Id)).Read<StartSessionResponse>();
        for (int i = 1; i <= 40; i++)
        {
            _f.Time.Advance(TimeSpan.FromSeconds(60));
            var hb = await (await c.PostAsJsonAsync($"/api/v1/sessions/{s.SessionId}/heartbeat", new HeartbeatRequest(i))).Read<HeartbeatResponse>();
            if (!hb.Active) break;
        }
        Assert.Equal(BillingStates.TrialExhausted, (await Me(c)).BillingState);
    }

    [Theory]
    [InlineData("subscription.created", "active", null, BillingStates.Active)]
    [InlineData("subscription.activated", "active", null, BillingStates.Active)]
    [InlineData("subscription.trialing", "trialing", null, BillingStates.Active)]
    [InlineData("subscription.updated", "active", "cancel", BillingStates.Canceling)]
    [InlineData("subscription.past_due", "past_due", null, BillingStates.PastDue)]
    [InlineData("subscription.paused", "paused", null, BillingStates.Paused)]
    [InlineData("subscription.canceled", "canceled", null, BillingStates.Expired)]
    public async Task EveryPaddleSubscriptionEventMapsToTheRightStateAndEntitlement(string type, string status, string? scheduled, string expectedState)
    {
        var (id, c) = await NewUser();
        var now = _f.Time.GetUtcNow();
        var r = await Event(type, Helpers.SubscriptionData(id, status, sub: "sub_" + Guid.NewGuid().ToString("n"), start: now.AddDays(-1), end: now.AddDays(29), scheduled: scheduled));
        Assert.Contains("processed", await r.Content.ReadAsStringAsync());
        var me = await Me(c);
        Assert.Equal(expectedState, me.BillingState);
        var paid = expectedState is BillingStates.Active or BillingStates.Canceling or BillingStates.PastDue;
        Assert.Equal(paid ? "pro" : "trial", me.Entitlements.PlanCode);
    }

    [Fact]
    public async Task ResumedSubscriptionRestoresPro()
    {
        var (id, c) = await NewUser();
        var sub = "sub_r" + id.ToString("n");
        var t = _f.Time.GetUtcNow();
        (await Event("subscription.paused", Helpers.SubscriptionData(id, "paused", sub: sub), t)).EnsureSuccessStatusCode();
        Assert.Equal("trial", (await Me(c)).Entitlements.PlanCode);
        (await Event("subscription.resumed", Helpers.SubscriptionData(id, "active", sub: sub), t.AddMinutes(1))).EnsureSuccessStatusCode();
        var me = await Me(c);
        Assert.Equal("pro", me.Entitlements.PlanCode);
        Assert.Equal(BillingStates.Active, me.BillingState);
    }

    [Fact]
    public async Task TransactionCompletedLinksCustomerOnlyForAKnownUser()
    {
        var (id, c) = await NewUser();
        var linked = await Event("transaction.completed", new { id = "txn_1", customer_id = "ctm_link", subscription_id = (string?)null, custom_data = new { user_id = id.ToString() } });
        Assert.Contains("processed", await linked.Content.ReadAsStringAsync());
        var portal = await c.PostAsync("/api/v1/billing/portal", null);
        Assert.Equal(HttpStatusCode.OK, portal.StatusCode);                  // customer linked → portal available
        var unknown = await Event("transaction.completed", new { id = "txn_2", customer_id = "ctm_x", custom_data = new { user_id = Guid.NewGuid().ToString() } });
        Assert.Contains("ignored", await unknown.Content.ReadAsStringAsync());
        var none = await Event("transaction.completed", new { id = "txn_3", customer_id = "ctm_y" });
        Assert.Contains("ignored", await none.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("{\"event_type\":\"subscription.created\"}")]
    [InlineData("{\"event_id\":123,\"event_type\":\"subscription.created\"}")]
    public async Task MalformedButCorrectlySignedPayloadsAreRejectedWith400(string body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/paddle") { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        req.Headers.Add("Paddle-Signature", InterviewAssistant.Backend.Services.PaddleSignature.Sign(body, ApiFactory.WebhookSecret, _f.Time.GetUtcNow().ToUnixTimeSeconds()));
        var r = await _f.CreateClient().SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task WrongTypedFieldsNeverCrashTheProcessor()
    {
        var (id, _) = await NewUser();
        var r = await Event("subscription.updated", new { id = 42, status = new[] { "x" }, customer_id = true, custom_data = new { user_id = id.ToString() }, items = "nope", current_billing_period = 7 });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task CheckoutIsRefusedWhenAlreadySubscribedAndPortalIsOffered()
    {
        var (id, c) = await NewUser();
        (await Event("subscription.activated", Helpers.SubscriptionData(id, "active", sub: "sub_a" + id.ToString("n")))).EnsureSuccessStatusCode();
        var r = await c.PostAsJsonAsync("/api/v1/billing/checkout", new CheckoutRequest("pro"));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("already_subscribed", (await r.Error()).Code);
    }

    [Fact]
    public async Task CheckoutCarriesTheAuthenticatedUserAndIgnoresClientSuppliedIds()
    {
        var (id, c) = await NewUser();
        var r = await c.PostAsync("/api/v1/billing/checkout", new StringContent("{\"planCode\":\"pro\",\"userId\":\"" + Guid.NewGuid() + "\"}", System.Text.Encoding.UTF8, "application/json"));
        r.EnsureSuccessStatusCode();
        var sent = _f.Paddle.Requests.Last(x => x.Request.RequestUri!.AbsolutePath == "/transactions").Body;
        Assert.Contains($"\"user_id\":\"{id}\"", sent);
    }

    [Fact]
    public async Task PortalWithoutBillingAccountExplainsWhy()
    {
        var (_, c) = await NewUser();
        var r = await c.PostAsync("/api/v1/billing/portal", null);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("no_billing_account", (await r.Error()).Code);
    }

    [Fact]
    public async Task LegacyDevelopmentPlanRowsResolveToPro()
    {
        var (id, c) = await NewUser();
        (await Event("subscription.activated", Helpers.SubscriptionData(id, "active", sub: "sub_l" + id.ToString("n")))).EnsureSuccessStatusCode();
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InterviewAssistant.Backend.Data.AppDbContext>();
            var sub = db.Subscriptions.Single(s => s.UserId == id);
            sub.PlanCode = "pro-dev";
            await db.SaveChangesAsync();
        }
        Assert.Equal("pro", (await Me(c)).Entitlements.PlanCode);
    }

    [Fact]
    public async Task CheckoutPageOpensPaddleOnlyForAValidTransaction()
    {
        var http = _f.CreateClient();
        var ok = await http.GetAsync("/pay?_ptxn=txn_01abc");
        var html = await ok.Content.ReadAsStringAsync();
        Assert.Contains("Paddle.Initialize", html);
        Assert.Contains("Paddle.Environment.set('sandbox')", html);
        Assert.Contains("test_clienttoken123", html);
        var csp = ok.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("nonce-", csp);
        Assert.Contains("https://cdn.paddle.com", csp);
        var bad = await (await http.GetAsync("/pay?_ptxn=%3Cscript%3E")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Paddle.Initialize", bad);
        Assert.DoesNotContain("<script>", bad.Replace("<script src", "").Replace("<script nonce", ""));
        var confirmed = await (await http.GetAsync("/auth/confirmed")).Content.ReadAsStringAsync();
        Assert.Contains("E-mail confirmed", confirmed);
    }

    [Fact]
    public async Task ConfigValidationCatchesProductionMistakes()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Db"] = "Host=x" }).Build();
        var plans = new PlanCatalog { Plans = { new PlanDefinition { Code = "trial", Name = "Trial", MaxDevices = 1, MaxConcurrentSessions = 1 }, new PlanDefinition { Code = "pro-dev", Name = "Pro (development seed)", Purchasable = true, PaddlePriceIds = { "pri_SET_IN_ENVIRONMENT" }, MaxDevices = 1, MaxConcurrentSessions = 1 } } };
        var errors = ConfigValidation.Validate(cfg, production: true,
            new AuthOptions { Issuer = "https://p.supabase.co/auth/v1", JwtSecret = "x" }, new OpenAiOptions(),
            new PaddleOptions { ApiKey = "pdl_live_apikey_x", ApiBaseUrl = "https://sandbox-api.paddle.com" }, plans, new ProductOptions { TermsUrl = "http://insecure" }); // fake-credential: test fixture
        foreach (var expected in new[] { "Auth__JwksUrl is required in production", "Auth__JwtSecret must not be set", "OpenAI__ApiKey", "Paddle__WebhookSecret", "LIVE key", "development name", "no real Paddle price id", "Product__TermsUrl" })
            Assert.Contains(errors, e => e.Contains(expected));
        Assert.Empty(ConfigValidation.Validate(cfg, production: false, new AuthOptions { Issuer = "https://p.supabase.co/auth/v1", JwtSecret = "x" }, new OpenAiOptions(), new PaddleOptions(),
            new PlanCatalog { Plans = { new PlanDefinition { Code = "trial", Name = "Trial", MaxDevices = 1, MaxConcurrentSessions = 1 } } }, new ProductOptions()));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task TranscriptionFallsBackWhenTheProviderRejectsTheModel()
    {
        var original = _f.OpenAi.Respond;
        _f.OpenAi.Respond = req => req.RequestUri!.AbsolutePath.EndsWith("client_secrets") && req.Content!.ReadAsStringAsync().Result.Contains("\"gpt-4o-transcribe\"")
            ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"code\":\"model_not_found\"}}") }
            : original(req);
        try
        {
            var (id, c) = await NewUser();
            var s = await (await c.StartSession((await c.RegisterDevice()).Id)).Read<StartSessionResponse>();
            using (await _f.OpenRelay(id, s.SessionId)) { }
            Assert.Equal("gpt-4o-mini-transcribe", _f.Upstream.Last.Credential.Model);
        }
        finally { _f.OpenAi.Respond = original; }
    }
}
