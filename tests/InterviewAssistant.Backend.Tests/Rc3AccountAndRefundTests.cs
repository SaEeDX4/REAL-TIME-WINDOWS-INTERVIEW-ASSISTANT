using System.Net;
using InterviewAssistant.Backend.Data;
using InterviewAssistant.Backend.Services;
using InterviewAssistant.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace InterviewAssistant.Backend.Tests;

/// <summary>
/// RC3 launch blockers: account deletion removes the sign-in identity itself (no "contact support"), and Paddle
/// refunds / chargebacks withdraw paid access — idempotently and safely under out-of-order delivery.
/// </summary>
public class Rc3AccountAndRefundTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public Rc3AccountAndRefundTests(ApiFactory f) => _f = f;

    private async Task<(Guid Id, HttpClient C)> NewUser() { var id = Guid.NewGuid(); var c = _f.ClientFor(id); (await c.GetAsync("/api/v1/me")).EnsureSuccessStatusCode(); return (id, c); }
    private async Task<AccountDto> Me(HttpClient c) => await (await c.GetAsync("/api/v1/me")).Read<AccountDto>();

    private async Task<UserAccount> User(Guid id)
    {
        using var scope = _f.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.AsNoTracking().FirstAsync(u => u.Id == id);
    }

    // ---------------- sign-in identity deletion ----------------

    [Fact]
    public async Task DeletingTheAccount_AlsoDeletesTheSignInIdentity()
    {
        var (id, c) = await NewUser();
        var r = await c.DeleteAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("deleted", (await r.Read<DeleteAccountResponse>()).Identity);

        var call = _f.Supabase.Requests.Last(x => x.Request.RequestUri!.AbsolutePath.EndsWith(id.ToString()));
        Assert.Equal(HttpMethod.Delete, call.Request.Method);
        Assert.Equal($"https://test-project.supabase.co/auth/v1/admin/users/{id}", call.Request.RequestUri!.ToString());
        Assert.Equal(ApiFactory.ServiceRoleKey, call.Request.Headers.GetValues("apikey").Single());
        Assert.Equal("Bearer " + ApiFactory.ServiceRoleKey, call.Request.Headers.Authorization!.ToString());
        Assert.DoesNotContain(ApiFactory.ServiceRoleKey, await r.Content.ReadAsStringAsync());   // never echoed to the client

        var u = await User(id);
        Assert.Equal("deleted", u.Status);
        Assert.NotNull(u.IdentityDeletedUtc);
        Assert.Null(u.IdentityDeletionPendingSinceUtc);
        Assert.Equal("account_deleted", (await (await c.GetAsync("/api/v1/me")).Error()).Code);   // the old token cannot recreate it
    }

    [Fact]
    public async Task ProviderOutage_QueuesTheIdentityDeletion_AndTheSweeperCompletesIt()
    {
        var (id, c) = await NewUser();
        var original = _f.Supabase.Respond;
        _f.Supabase.Respond = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        try
        {
            var r = await c.DeleteAsync("/api/v1/me");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);                          // the data deletion itself succeeded
            Assert.Equal("pending", (await r.Read<DeleteAccountResponse>()).Identity);
            var u = await User(id);
            Assert.NotNull(u.IdentityDeletionPendingSinceUtc);
            Assert.Null(u.IdentityDeletedUtc);
        }
        finally { _f.Supabase.Respond = original; }

        using (var scope = _f.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<IdentityDeletionService>().RetryPendingAsync(default) >= 1);
        var done = await User(id);
        Assert.NotNull(done.IdentityDeletedUtc);
        Assert.Null(done.IdentityDeletionPendingSinceUtc);
        Assert.True(done.IdentityDeletionAttempts >= 2);
    }

    [Fact]
    public async Task IdentityAlreadyGoneAtTheProvider_CountsAsDeleted()
    {
        var (id, c) = await NewUser();
        var original = _f.Supabase.Respond;
        _f.Supabase.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        try { Assert.Equal("deleted", (await (await c.DeleteAsync("/api/v1/me")).Read<DeleteAccountResponse>()).Identity); }
        finally { _f.Supabase.Respond = original; }
        Assert.NotNull((await User(id)).IdentityDeletedUtc);
    }

    private sealed class Unconfigured : IIdentityAdmin
    {
        public bool Configured => false;
        public int Calls;
        public Task<bool> DeleteUserAsync(Guid userId, CancellationToken ct) { Calls++; return Task.FromResult(false); }
    }

    [Fact]
    public async Task WithoutTheServerKey_TheDeletionStaysQueued_NeverSilentlyDropped()
    {
        var (id, c) = await NewUser();
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var admin = new Unconfigured();
        var svc = new IdentityDeletionService(db, admin, _f.Time, NullLogger<IdentityDeletionService>.Instance);
        var u = await db.Users.FirstAsync(x => x.Id == id);
        u.Status = "deleted"; u.IdentityDeletionPendingSinceUtc = _f.Time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync();
        Assert.Equal(IdentityDeletion.NotConfigured, await svc.CompleteAsync(u, default));
        Assert.Equal(0, await svc.RetryPendingAsync(default));
        Assert.Equal(0, admin.Calls);
        Assert.True(await svc.PendingCountAsync(default) >= 1);   // visible to operators (admin health) until the key is configured
    }

    // ---------------- refunds and chargebacks ----------------

    private static object Transaction(Guid user, string txn, string sub, DateTimeOffset billed) => new
    {
        id = txn, status = "completed", subscription_id = sub, customer_id = "ctm_" + user.ToString("n")[..8],
        custom_data = new { user_id = user.ToString() }, billed_at = billed.ToString("o"),
    };

    private static object Renewal(Guid user, string txn, string sub, DateTimeOffset billed) => new
    {
        id = txn, status = "completed", subscription_id = sub, customer_id = "ctm_" + user.ToString("n")[..8], billed_at = billed.ToString("o"),
    };

    private static object Adjustment(string id, string txn, string sub, string action, string status, string type = "full") => new
    {
        id, transaction_id = txn, subscription_id = sub, customer_id = "ctm_x", action, type, status,
        totals = new { total = "1999", currency_code = "USD" },
    };

    private async Task Send(string type, object data, DateTimeOffset? at = null, string? eventId = null) =>
        (await _f.PaddleEvent(eventId ?? "evt_" + Guid.NewGuid().ToString("n"), type, data, at)).EnsureSuccessStatusCode();

    private async Task<(Guid Id, HttpClient C, string Sub, string Txn)> PaidUser()
    {
        var (id, c) = await NewUser();
        var sub = "sub_" + id.ToString("n");
        var txn = "txn_" + id.ToString("n")[..12];
        var t0 = _f.Time.GetUtcNow();
        await Send("transaction.completed", Transaction(id, txn, sub, t0.AddMinutes(-10)), t0.AddMinutes(-10));
        await Send("subscription.created", Helpers.SubscriptionData(id, "active", sub: sub), t0.AddMinutes(-9));
        var me = await Me(c);
        Assert.Equal("pro", me.Entitlements.PlanCode);
        Assert.Equal(BillingStates.Active, me.BillingState);
        return (id, c, sub, txn);
    }

    [Fact]
    public async Task ApprovedFullRefund_RevokesPaidAccess_AndANewerPaymentRestoresIt()
    {
        var (id, c, sub, txn) = await PaidUser();
        var t = _f.Time.GetUtcNow();
        await Send("adjustment.created", Adjustment("adj_r1_" + id.ToString("n")[..6], txn, sub, "refund", "pending_approval"), t);
        Assert.Equal("pro", (await Me(c)).Entitlements.PlanCode);                    // a refund request alone changes nothing

        await Send("adjustment.updated", Adjustment("adj_r1_" + id.ToString("n")[..6], txn, sub, "refund", "approved"), t.AddMinutes(1));
        var me = await Me(c);
        Assert.Equal(BillingStates.Revoked, me.BillingState);
        Assert.Equal("trial", me.Entitlements.PlanCode);

        // A later subscription update (e.g. Paddle cancels the refunded subscription or just echoes it) keeps it revoked.
        await Send("subscription.updated", Helpers.SubscriptionData(id, "active", sub: sub), t.AddMinutes(2));
        Assert.Equal(BillingStates.Revoked, (await Me(c)).BillingState);

        // The customer pays again (renewal transaction without custom_data): access returns.
        await Send("transaction.completed", Renewal(id, "txn_new_" + id.ToString("n")[..6], sub, t.AddMinutes(3)), t.AddMinutes(3));
        me = await Me(c);
        Assert.Equal(BillingStates.Active, me.BillingState);
        Assert.Equal("pro", me.Entitlements.PlanCode);
    }

    [Fact]
    public async Task PartialRefundsAndCredits_KeepAccess()
    {
        var (id, c, sub, txn) = await PaidUser();
        var t = _f.Time.GetUtcNow();
        await Send("adjustment.created", Adjustment("adj_p_" + id.ToString("n")[..6], txn, sub, "refund", "approved", "partial"), t);
        await Send("adjustment.created", Adjustment("adj_c_" + id.ToString("n")[..6], txn, sub, "credit", "approved"), t.AddSeconds(1));
        await Send("adjustment.created", Adjustment("adj_w_" + id.ToString("n")[..6], txn, sub, "chargeback_warning", "approved"), t.AddSeconds(2));
        await Send("adjustment.updated", Adjustment("adj_x_" + id.ToString("n")[..6], txn, sub, "refund", "rejected"), t.AddSeconds(3));
        Assert.Equal(BillingStates.Active, (await Me(c)).BillingState);
    }

    [Fact]
    public async Task Chargeback_RevokesAccess_AndAnApprovedReversalRestoresIt()
    {
        var (id, c, sub, txn) = await PaidUser();
        var t = _f.Time.GetUtcNow();
        await Send("adjustment.created", Adjustment("adj_cb_" + id.ToString("n")[..6], txn, sub, "chargeback", "approved"), t);
        Assert.Equal(BillingStates.Revoked, (await Me(c)).BillingState);
        await Send("adjustment.created", Adjustment("adj_cbr_" + id.ToString("n")[..6], txn, sub, "chargeback_reverse", "approved"), t.AddDays(1));
        var me = await Me(c);
        Assert.Equal(BillingStates.Active, me.BillingState);
        Assert.Equal("pro", me.Entitlements.PlanCode);
    }

    [Fact]
    public async Task OutOfOrderDeliveries_NeverWinOverNewerAdjustmentState()
    {
        var (id, c, sub, txn) = await PaidUser();
        var adj = "adj_o_" + id.ToString("n")[..6];
        var t = _f.Time.GetUtcNow();
        // "approved" (t+1) arrives before "pending" (t): the late pending state is ignored.
        await Send("adjustment.updated", Adjustment(adj, txn, sub, "refund", "approved"), t.AddMinutes(1));
        await Send("adjustment.created", Adjustment(adj, txn, sub, "refund", "pending_approval"), t);
        Assert.Equal(BillingStates.Revoked, (await Me(c)).BillingState);
        // The refund is reversed (t+2); a delayed retry of the "approved" delivery (t+1, new event id) does not re-revoke.
        await Send("adjustment.updated", Adjustment(adj, txn, sub, "refund", "reversed"), t.AddMinutes(2));
        Assert.Equal(BillingStates.Active, (await Me(c)).BillingState);
        await Send("adjustment.updated", Adjustment(adj, txn, sub, "refund", "approved"), t.AddMinutes(1));
        Assert.Equal(BillingStates.Active, (await Me(c)).BillingState);
        // A late renewal-era payment older than the current one never replaces it.
        await Send("transaction.completed", Renewal(id, "txn_old_" + id.ToString("n")[..6], sub, t.AddDays(-40)), t.AddMinutes(3));
        using var scope = _f.Services.CreateScope();
        var s = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Subscriptions.AsNoTracking().FirstAsync(x => x.UserId == id);
        Assert.Equal(txn, s.CurrentTransactionId);
    }

    [Fact]
    public async Task DuplicateAdjustmentDeliveries_AreProcessedOnce()
    {
        var (id, c, sub, txn) = await PaidUser();
        var evt = "evt_adj_dup_" + id.ToString("n");
        var t = _f.Time.GetUtcNow();
        await Send("adjustment.created", Adjustment("adj_d_" + id.ToString("n")[..6], txn, sub, "chargeback", "approved"), t, evt);
        var dup = await _f.PaddleEvent(evt, "adjustment.created", Adjustment("adj_d_" + id.ToString("n")[..6], txn, sub, "chargeback", "approved"), t);
        Assert.Equal(HttpStatusCode.OK, dup.StatusCode);
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.WebhookEvents.CountAsync(e => e.EventId == evt));
        Assert.Equal(1, await db.BillingAdjustments.CountAsync(a => a.ProviderSubscriptionId == sub));
        Assert.Equal(BillingStates.Revoked, (await Me(c)).BillingState);
    }

    [Fact]
    public async Task RefundOfAnOlderPayment_DoesNotRevokeTheCurrentPeriod()
    {
        var (id, c, sub, oldTxn) = await PaidUser();
        var t = _f.Time.GetUtcNow();
        await Send("transaction.completed", Renewal(id, "txn_cur_" + id.ToString("n")[..6], sub, t.AddMinutes(1)), t.AddMinutes(1));
        await Send("adjustment.created", Adjustment("adj_old_" + id.ToString("n")[..6], oldTxn, sub, "refund", "approved"), t.AddMinutes(2));
        Assert.Equal(BillingStates.Active, (await Me(c)).BillingState);
    }

    [Fact]
    public async Task AdjustmentBeforeTheSubscriptionIsKnown_IsAppliedWhenTheSubscriptionArrives()
    {
        var (id, c) = await NewUser();
        var sub = "sub_late_" + id.ToString("n");
        var t = _f.Time.GetUtcNow();
        await Send("adjustment.created", Adjustment("adj_e_" + id.ToString("n")[..6], "txn_e_" + id.ToString("n")[..6], sub, "chargeback", "approved"), t);
        await Send("subscription.created", Helpers.SubscriptionData(id, "active", sub: sub), t.AddMinutes(1));
        Assert.Equal(BillingStates.Revoked, (await Me(c)).BillingState);
    }

    [Fact]
    public async Task RevokedAccount_CannotStartLiveSessionsOnThePaidAllowance()
    {
        var (id, c, sub, txn) = await PaidUser();
        await Send("adjustment.created", Adjustment("adj_l_" + id.ToString("n")[..6], txn, sub, "chargeback", "approved"), _f.Time.GetUtcNow());
        var me = await Me(c);
        Assert.Equal("trial", me.Entitlements.PlanCode);
        Assert.Equal(30 * 60, me.Usage.LiveSecondsRemaining);   // only the trial allowance remains, never the paid one
    }
}
