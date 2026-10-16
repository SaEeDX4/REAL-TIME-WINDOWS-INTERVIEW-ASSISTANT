using System.Net.Http;
using InterviewAssistant.App.UiState;
using InterviewAssistant.Client;
using InterviewAssistant.Core.Languages;
using Xunit;

namespace InterviewAssistant.Tests;

/// <summary>
/// RC4 error-state matrix for the Account page: slow, offline, reconnect, one endpoint timing out, 500, cancelled and
/// retry-success. Each section fails on its own with section-specific copy; correlation ids never reach the user text
/// of an inline section (they are logged).
/// </summary>
public class ErrorStateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 14, 5, 0, TimeSpan.Zero);
    private static string En(string key) => Loc.Format("en", key);

    [Fact]
    public void Offline_SaysCheckYourConnection()
    {
        Assert.Equal(En("svc_unreachable"), ErrorCopy.Describe(new BackendException(0, "network", "", null), includeReference: false));
        Assert.Equal(En("svc_unreachable"), ErrorCopy.Describe(new HttpRequestException("No such host"), includeReference: false));
    }

    [Fact]
    public void OneEndpointTimingOut_IsTheTimeoutCopy_WithoutTheCorrelationIdInline()
    {
        var ex = new BackendException(504, "timeout", "timeout", "c0ffee12-3456-7890-abcd-ef0123456789");
        var inline = ErrorCopy.Describe(ex, includeReference: false);
        Assert.Equal(En("svc_timeout"), inline);
        Assert.DoesNotContain("c0ffee12", inline);
        Assert.Contains("c0ffee12", ErrorCopy.Describe(ex, includeReference: true));   // dialogs/toasts keep a short reference
        Assert.Equal(En("svc_timeout"), ErrorCopy.Describe(new TaskCanceledException(), includeReference: false));   // client-side timeout
    }

    [Fact]
    public void ServerError500_IsTemporarilyUnavailable_NeverARawMessage()
    {
        var text = ErrorCopy.Describe(new BackendException(500, "", "System.NullReferenceException at …", "abcdef1234567890"), includeReference: false);
        Assert.Equal(En("svc_unavailable"), text);
        Assert.DoesNotContain("Exception", text);
    }

    [Fact]
    public void UserCancellation_IsNotAFailure()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.True(ErrorCopy.IsUserCancellation(new OperationCanceledException(cts.Token), cts.Token));
        Assert.False(ErrorCopy.IsUserCancellation(new TaskCanceledException(), CancellationToken.None));   // an HTTP timeout is
    }

    [Fact]
    public void Slow_ShowsLoadingOnlyUntilDataExists_ThenKeepsTheData()
    {
        var devices = new SectionState("devices");
        var t = devices.Begin();
        Assert.True(devices.IsLoading);
        Assert.False(devices.HasData);
        devices.Succeed(t, T0);
        var t2 = devices.Begin();                     // a slow refresh: the list on screen stays
        Assert.True(devices.IsLoading && devices.HasData);
        devices.Succeed(t2, T0.AddMinutes(1));
        Assert.Equal(SectionStatus.Ready, devices.Status);
    }

    [Fact]
    public void OfflineThenReconnect_StaleNoteThenCleared()
    {
        var s = new AccountSections();
        var a = s.Summary.Begin(); s.Summary.Succeed(a, T0);
        var d = s.Devices.Begin(); s.Devices.Succeed(d, T0);
        // connection drops: both refreshes fail, the data stays with a "saved data from" note
        var a2 = s.Summary.Begin(); var d2 = s.Devices.Begin();
        s.Summary.Fail(a2, En("svc_unreachable")); s.Devices.Fail(d2, En("svc_unreachable"));
        Assert.True(s.Summary.IsStale && s.Devices.IsStale);
        Assert.Contains("Showing saved data from", s.Devices.StaleNote(t => t.ToString("HH:mm"))!);
        // reconnect: the next loads clear errors and notes by themselves
        var a3 = s.Summary.Begin(); var d3 = s.Devices.Begin();
        s.Summary.Succeed(a3, T0.AddMinutes(5)); s.Devices.Succeed(d3, T0.AddMinutes(5));
        Assert.All(new[] { s.Summary, s.Devices }, x => { Assert.Null(x.Error); Assert.False(x.IsStale); Assert.Null(x.StaleNote(t => "")); });
    }

    [Fact]
    public void RetrySuccess_AfterA500_ClearsOnlyThatSection()
    {
        var s = new AccountSections();
        var b = s.Billing.Begin(); s.Billing.Fail(b, En("svc_unavailable"));
        var d = s.Devices.Begin(); s.Devices.Fail(d, En("svc_timeout"));
        var retry = s.Billing.Begin(); s.Billing.Succeed(retry, T0);
        Assert.Equal(SectionStatus.Ready, s.Billing.Status);
        Assert.Equal(SectionStatus.Failed, s.Devices.Status);   // independent: still offers its own "Try again"
    }

    [Fact]
    public void SectionFailureTitles_AreSectionSpecific_InEveryLanguage()
    {
        foreach (var l in LanguageRegistry.All)
        {
            var titles = new[] { "acct_summary_failed", "acct_devices_failed", "acct_billing_failed" }.Select(k => Loc.Format(l.Code, k)).ToList();
            Assert.Equal(3, titles.Distinct().Count());
            Assert.DoesNotContain(Loc.Format(l.Code, "svc_timeout"), titles);
        }
    }
}

public class NavigationGuardOrderTests
{
    [Fact]
    public async Task DiscardForTheFirstEditor_ThenStayForTheSecond_DiscardsNothing()
    {
        var answers = new Queue<LeaveDecision>(new[] { LeaveDecision.Discard, LeaveDecision.Stay });
        var nav = new NavigationGuard((_, _) => Task.FromResult(answers.Dequeue()));
        var profile = nav.Register(new DirtyStateGuard("profile"));
        var interview = nav.Register(new DirtyStateGuard("interview"));
        int discarded = 0;
        profile.Discard = () => discarded++;
        interview.Discard = () => discarded++;
        profile.MarkDirty(); interview.MarkDirty();
        Assert.False(await nav.CanProceedAsync(ChangeReason.SignOut));
        Assert.Equal(0, discarded);
        Assert.True(profile.IsDirty && interview.IsDirty);
    }
}

public class RelayConnectionToastTests
{
    private static ConnectionToast Run(params InterviewAssistant.Core.Providers.ConnectionStatus[] events)
    {
        var now = DateTimeOffset.UnixEpoch;
        var gate = new ConnectionToastGate(() => now);
        bool up = true;
        var last = ConnectionToast.None;
        foreach (var e in events)
        {
            if (RelayConnection.Transition(up, e) is { } t) { up = t.Up; var r = gate.Report(up, t.Silent); if (r != ConnectionToast.None) last = r; }
            now = now.AddSeconds(5);
            var p = gate.Poll(); if (p != ConnectionToast.None) last = p;
        }
        return last;
    }

    [Fact]
    public void ReconnectThenConnected_SaysRestored() =>
        Assert.Equal(ConnectionToast.Restored, Run(InterviewAssistant.Core.Providers.ConnectionStatus.Reconnecting, InterviewAssistant.Core.Providers.ConnectionStatus.Connected));

    [Fact]
    public void ReconnectThenFailed_NeverSaysRestored() =>
        Assert.Equal(ConnectionToast.Lost, Run(InterviewAssistant.Core.Providers.ConnectionStatus.Reconnecting, InterviewAssistant.Core.Providers.ConnectionStatus.Failed));

    [Fact]
    public void AnswerOrErrorStatusDuringTheOutage_DoesNotCountAsRestored()
    {
        // Only transcriber events move the state; the status line (Answering, ApiError) is not an input at all.
        Assert.Null(RelayConnection.Transition(false, InterviewAssistant.Core.Providers.ConnectionStatus.Reconnecting));
        Assert.Null(RelayConnection.Transition(true, InterviewAssistant.Core.Providers.ConnectionStatus.Connected));
    }
}
