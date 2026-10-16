using System.Collections.Concurrent;
using System.Net.WebSockets;
using InterviewAssistant.Client;
using InterviewAssistant.Contracts;
using InterviewAssistant.Core.Providers;

namespace InterviewAssistant.Backend.Tests;

/// <summary>The API on a real Kestrel socket, so the desktop's own ClientWebSocket-based transcriber can connect.</summary>
public sealed class KestrelApiFactory : ApiFactory
{
    public KestrelApiFactory() => UseKestrel(0);
    public Uri BaseAddress { get { StartServer(); return ClientOptions.BaseAddress; } }
}

/// <summary>
/// The real desktop pieces (CloudSession + OpenAiRealtimeTranscriber) against the real relay: transcripts flow, pause
/// closes the connection with the lease, resume reconnects through a new lease, and an ended lease stops transcription.
/// </summary>
public class RelayEndToEndTests : IClassFixture<KestrelApiFactory>
{
    private readonly KestrelApiFactory _f;
    public RelayEndToEndTests(KestrelApiFactory f) => _f = f;

    private static async Task Until(Func<bool> condition, string what, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Timed out waiting for: " + what);
            await Task.Delay(50);
        }
    }

    private static readonly byte[] Frame = new byte[4800];   // 100 ms of 24 kHz PCM16 silence

    [Fact]
    public async Task DesktopTranscriber_StreamsThroughTheRelay_AndPauseResumeReconnectsThroughANewLease()
    {
        var user = Guid.NewGuid();
        var http = new HttpClient { BaseAddress = _f.BaseAddress };
        var api = new BackendClient(http, (_, _) => Task.FromResult<string?>(_f.Token(user)), "2.1.0-rc.3");
        var device = await api.RegisterDeviceAsync(new RegisterDeviceRequest("inst-" + Guid.NewGuid().ToString("n"), "PC", "windows-x64", "2.1.0-rc.3"), default);

        CloudSession? lease = await CloudSession.StartAsync(api, device.Id, null, "en", default, _f.Time, runLoop: false);
        var opts = new RealtimeTranscriberOptions { ServerConfiguredSession = true, Endpoint = lease.RelayEndpoint.ToString() };
        foreach (var (k, v) in lease.RelayHeaders) opts.Headers[k] = v;
        var statuses = new ConcurrentQueue<ConnectionStatus>();
        var segments = new ConcurrentQueue<string>();
        await using var t = new OpenAiRealtimeTranscriber(ct => lease is { Active: true } l ? l.NextConnectionCredentialAsync(ct) : Task.FromResult<string?>(null), opts);
        t.StatusChanged += (s, _) => statuses.Enqueue(s);
        t.SegmentCompleted += (_, text) => segments.Enqueue(text);

        var before = _f.Upstream.Connections.Count;
        var run = new CancellationTokenSource();
        await t.StartAsync(run.Token);
        await Until(() => t.Status == ConnectionStatus.Connected && _f.Upstream.Connections.Count == before + 1, "relay connected");
        var upstream = _f.Upstream.Last;
        Assert.StartsWith("ek_test_", upstream.Credential.ClientSecret);   // minted and used on the server only

        // Audio goes up as input_audio_buffer.append; the provider's transcript comes back down.
        t.SendAudio(Frame);
        var up = await upstream.Socket.Receive(TimeSpan.FromSeconds(5));
        Assert.Contains("\"input_audio_buffer.append\"", up!.Text);
        await upstream.Socket.SendText("{\"type\":\"conversation.item.input_audio_transcription.completed\",\"item_id\":\"i1\",\"transcript\":\"Why this role?\"}");
        await Until(() => segments.Contains("Why this role?"), "transcript relayed");

        // Pause: the app suspends transcription and ends the lease (paused time is never billed).
        await t.StopAsync();
        await lease.StopAsync();
        Assert.Null(await lease.NextConnectionCredentialAsync(default));

        // Resume: a new lease, the engine's new run token (the old one is cancelled) — transcription reconnects.
        run.Cancel();
        lease = await CloudSession.StartAsync(api, device.Id, null, "en", default, _f.Time, runLoop: false);
        opts.Endpoint = lease.RelayEndpoint.ToString();
        var run2 = new CancellationTokenSource();
        var beforeResume = _f.Upstream.Connections.Count;
        await t.StartAsync(run2.Token);
        await Until(() => t.Status == ConnectionStatus.Connected && _f.Upstream.Connections.Count == beforeResume + 1, "reconnected after resume");
        var upstream2 = _f.Upstream.Last;
        await upstream2.Socket.SendText("{\"type\":\"conversation.item.input_audio_transcription.completed\",\"item_id\":\"i2\",\"transcript\":\"Tell me about a conflict.\"}");
        await Until(() => segments.Contains("Tell me about a conflict."), "transcript after resume");

        // The lease ends on the server (here: the user stops on another path). The relay closes, the transcriber
        // cannot reconnect without an active lease and stops instead of retrying forever.
        await lease.StopAsync();
        await Until(() => t.Status == ConnectionStatus.Failed, "transcriber stopped after the lease ended");
        var received = await upstream2.Socket.Receive(TimeSpan.FromSeconds(5));
        Assert.True(received == null || received.Type == WebSocketMessageType.Close);
        run2.Cancel();
    }

    [Fact]
    public async Task TranscriberLoop_RestartsAfterItsTokenWasCancelled()
    {
        int calls = 0;
        await using var t = new OpenAiRealtimeTranscriber(_ => { Interlocked.Increment(ref calls); return Task.FromResult<string?>(null); },
            new RealtimeTranscriberOptions { ServerConfiguredSession = true });
        var first = new CancellationTokenSource();
        await t.StartAsync(first.Token);
        await Until(() => calls == 1 && t.Status == ConnectionStatus.Failed, "first loop ended");
        first.Cancel();
        await t.StartAsync(new CancellationTokenSource().Token);   // e.g. Resume after a cloud pause
        await Until(() => calls == 2, "second loop started");
    }
}
