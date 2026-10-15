using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using InterviewAssistant.Core.Intelligence;
using InterviewAssistant.Core.Providers;
using Xunit;

namespace InterviewAssistant.Tests;

public class ProviderTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _f;
        public string? LastBody;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> f) => _f = f;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            LastBody = r.Content == null ? null : await r.Content.ReadAsStringAsync(ct);
            return _f(r);
        }
    }

    private static string Sse(params string[] deltas) =>
        string.Concat(deltas.Select(d => "data: " + new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["content"] = d } }) }.ToJsonString() + "\n\n")) + "data: [DONE]\n\n";

    private static readonly ChatMessage[] Msgs = { new("system", "s"), new("user", "u") };

    /// <summary>A body that delivers one chunk, then never sends anything again (a stalled upstream).</summary>
    private sealed class StallingStream : Stream
    {
        private readonly byte[] _first;
        private bool _sent;
        public StallingStream(string first) => _first = Encoding.UTF8.GetBytes(first);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_sent) { _sent = true; _first.CopyTo(buffer); return _first.Length; }
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => 0; set { } }
        public override void Flush() { } public override long Seek(long o, SeekOrigin s) => 0; public override void SetLength(long v) { }
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    [Fact]
    public async Task StalledStreamAfterTheFirstToken_TimesOutInsteadOfHanging()
    {
        var first = "data: " + new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["content"] = "• I'd start" } }) }.ToJsonString() + "\n\n";
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(first)) });
        using var p = new OpenAiChatAnswerProvider(() => "k", new ChatProviderOptions { StreamIdleTimeout = TimeSpan.FromMilliseconds(300) }, h);
        var parts = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<ProviderException>(async () => { await foreach (var d in p.StreamAsync(Msgs, 100, CancellationToken.None)) parts.Add(d); });
        Assert.Equal(ProviderErrorKind.Timeout, ex.Kind);
        Assert.Equal("• I'd start", Assert.Single(parts));
        Assert.True(sw.ElapsedMilliseconds < 5000, $"{sw.ElapsedMilliseconds} ms");
        Assert.False(JsonNode.Parse(h.LastBody!)!["store"]!.GetValue<bool>());      // provider-side retention off
    }

    [Fact]
    public async Task StreamsDeltasFromSse()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Sse("MODE: HYPOTHETICAL\n• I'd", " start.\n")) });
        using var p = new OpenAiChatAnswerProvider(() => "sk-test", null, h);
        var parts = new List<string>();
        await foreach (var d in p.StreamAsync(Msgs, 100, CancellationToken.None)) parts.Add(d);
        Assert.Equal("MODE: HYPOTHETICAL\n• I'd start.\n", string.Concat(parts));
        var body = JsonNode.Parse(h.LastBody!)!;
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.Equal("gpt-5.4-mini", body["model"]!.GetValue<string>());
        Assert.Equal("none", body["reasoning_effort"]!.GetValue<string>());
        Assert.Null(body["temperature"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ProviderErrorKind.InvalidApiKey)]
    [InlineData(HttpStatusCode.TooManyRequests, ProviderErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, ProviderErrorKind.ServerError)]
    [InlineData(HttpStatusCode.BadRequest, ProviderErrorKind.BadRequest)]
    public async Task MapsHttpErrors(HttpStatusCode code, ProviderErrorKind kind)
    {
        var h = new StubHandler(_ => new HttpResponseMessage(code) { Content = new StringContent("{\"error\":{\"message\":\"boom\"}}") });
        using var p = new OpenAiChatAnswerProvider(() => "sk-test", null, h);
        var ex = await Assert.ThrowsAsync<ProviderException>(async () => { await foreach (var _ in p.StreamAsync(Msgs, 10, CancellationToken.None)) { } });
        Assert.Equal(kind, ex.Kind);
    }

    [Fact]
    public async Task MissingKeyFailsFastWithoutNetwork()
    {
        using var p = new OpenAiChatAnswerProvider(() => null, null, new StubHandler(_ => throw new InvalidOperationException("should not call")));
        var ex = await Assert.ThrowsAsync<ProviderException>(async () => { await foreach (var _ in p.StreamAsync(Msgs, 10, CancellationToken.None)) { } });
        Assert.Equal(ProviderErrorKind.InvalidApiKey, ex.Kind);
    }

    [Fact]
    public async Task NetworkFailureIsMapped()
    {
        using var p = new OpenAiChatAnswerProvider(() => "k", null, new StubHandler(_ => throw new HttpRequestException("dns")));
        var ex = await Assert.ThrowsAsync<ProviderException>(async () => { await foreach (var _ in p.StreamAsync(Msgs, 10, CancellationToken.None)) { } });
        Assert.Equal(ProviderErrorKind.Network, ex.Kind);
    }

    [Fact]
    public void ReasoningModelsGetReasoningEffortAndNoTemperature()
    {
        using var p = new OpenAiChatAnswerProvider(() => "k", new ChatProviderOptions { Model = "gpt-5-mini" });
        var body = JsonNode.Parse(p.BuildBody(Msgs, 100))!;
        Assert.Null(body["temperature"]);
        Assert.Equal("minimal", body["reasoning_effort"]!.GetValue<string>());
        using var p2 = new OpenAiChatAnswerProvider(() => "k", new ChatProviderOptions { Model = "gpt-4.1-mini" });
        var b2 = JsonNode.Parse(p2.BuildBody(Msgs, 100))!;
        Assert.Null(b2["reasoning_effort"]);
        Assert.NotNull(b2["temperature"]);
    }

    [Fact]
    public async Task UnsupportedReasoningEffortIsRetriedWithoutIt()
    {
        int calls = 0;
        var h = new StubHandler(r =>
        {
            calls++;
            var body = r.Content!.ReadAsStringAsync().Result;
            return body.Contains("reasoning_effort")
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"message\":\"Unsupported value for reasoning_effort\"}}") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Sse("ok\n")) };
        });
        using var p = new OpenAiChatAnswerProvider(() => "k", null, h);
        var parts = new List<string>();
        await foreach (var d in p.StreamAsync(Msgs, 10, CancellationToken.None)) parts.Add(d);
        Assert.Equal(2, calls);
        Assert.Equal("ok\n", string.Concat(parts));
    }

    [Fact]
    public async Task UnknownModelFallsBackAutomatically()
    {
        var h = new StubHandler(r =>
        {
            var body = r.Content!.ReadAsStringAsync().Result;
            return body.Contains("gpt-5.4-mini")
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":{\"message\":\"The model gpt-5.4-mini does not exist\"}}") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Sse("hi")) };
        });
        using var p = new OpenAiChatAnswerProvider(() => "k", null, h);
        await foreach (var _ in p.StreamAsync(Msgs, 10, CancellationToken.None)) { }
        Assert.Equal("gpt-4.1-mini", p.ActiveModel);
    }

    [Theory]
    [InlineData("gpt-5.4-mini", "none")]
    [InlineData("gpt-5-mini", "minimal")]
    [InlineData("o4-mini", "low")]
    public void AutoEffortPerFamily(string model, string effort) => Assert.Equal(effort, OpenAiChatAnswerProvider.AutoEffort(model));

    [Fact]
    public void TranscriberFallsBackToNextModelWhenRejected()
    {
        var t = new OpenAiRealtimeTranscriber(() => "k");
        Assert.Equal("gpt-4o-transcribe", t.ActiveModel);
        t.HandleEvent(Encoding.UTF8.GetBytes("{\"type\":\"error\",\"error\":{\"code\":\"model_not_found\",\"message\":\"unknown model\"}}"), DateTime.UtcNow);
        Assert.Equal("gpt-4o-mini-transcribe", t.ActiveModel);
        Assert.Contains("gpt-4o-mini-transcribe", t.BuildSessionUpdate());
    }

    [Fact]
    public void RealtimeSessionUpdateMatchesDocumentedShape()
    {
        var t = new OpenAiRealtimeTranscriber(() => "k", new RealtimeTranscriberOptions { Prompt = TestKnowledge.Load().Context.BuildTranscriptionPrompt() });
        var ga = JsonNode.Parse(t.BuildSessionUpdate())!;
        Assert.DoesNotContain("Teroxx", new OpenAiRealtimeTranscriber(() => "k").BuildSessionUpdate()); // generic default
        Assert.Null(ga["session"]!["audio"]!["input"]!["transcription"]!["language"]); // auto-detect by default
        Assert.Equal("session.update", ga["type"]!.GetValue<string>());
        Assert.Equal("transcription", ga["session"]!["type"]!.GetValue<string>());
        Assert.Equal("audio/pcm", ga["session"]!["audio"]!["input"]!["format"]!["type"]!.GetValue<string>());
        Assert.Equal(24000, ga["session"]!["audio"]!["input"]!["format"]!["rate"]!.GetValue<int>());
        Assert.Equal("server_vad", ga["session"]!["audio"]!["input"]!["turn_detection"]!["type"]!.GetValue<string>());
        Assert.Contains("Teroxx", ga["session"]!["audio"]!["input"]!["transcription"]!["prompt"]!.GetValue<string>());

        var beta = JsonNode.Parse(new OpenAiRealtimeTranscriber(() => "k", new RealtimeTranscriberOptions { Protocol = "beta" }).BuildSessionUpdate())!;
        Assert.Equal("transcription_session.update", beta["type"]!.GetValue<string>());
        Assert.Equal("pcm16", beta["session"]!["input_audio_format"]!.GetValue<string>());
    }

    [Fact]
    public void RealtimeEventsAreDispatched()
    {
        var t = new OpenAiRealtimeTranscriber(() => "k");
        var log = new List<string>();
        t.SpeechStarted += () => log.Add("start");
        t.SpeechStopped += () => log.Add("stop");
        t.PartialTranscript += (id, d) => log.Add($"delta:{id}:{d}");
        t.SegmentCompleted += (id, s) => log.Add($"done:{id}:{s}");
        void Send(string j) => t.HandleEvent(Encoding.UTF8.GetBytes(j), DateTime.UtcNow);
        Send("{\"type\":\"input_audio_buffer.speech_started\"}");
        Send("{\"type\":\"input_audio_buffer.speech_stopped\"}");
        Send("{\"type\":\"conversation.item.input_audio_transcription.delta\",\"item_id\":\"i1\",\"delta\":\"How\"}");
        Send("{\"type\":\"conversation.item.input_audio_transcription.completed\",\"item_id\":\"i1\",\"transcript\":\"How would you?\"}");
        Send("not json");
        Assert.Equal(new[] { "start", "stop", "delta:i1:How", "done:i1:How would you?" }, log);
    }
}

public class ServerConfiguredTranscriptionTests
{
    [Fact]
    public void ServerConfiguredSessionNeverFallsBackClientSideOrSwitchesProtocol()
    {
        var t = new OpenAiRealtimeTranscriber(_ => Task.FromResult<string?>("ek_x"), new RealtimeTranscriberOptions { ServerConfiguredSession = true, Protocol = "beta" });
        t.HandleEvent(System.Text.Encoding.UTF8.GetBytes("{\"type\":\"error\",\"error\":{\"code\":\"model_not_found\",\"message\":\"unknown model\"}}"), DateTime.UtcNow);
        Assert.Equal("gpt-4o-transcribe", t.ActiveModel);                     // the server's routing decides models
        Assert.DoesNotContain("transcription_session.update", t.BuildSessionUpdate()); // never the beta protocol
    }

    [Fact]
    public async Task CredentialIsFetchedPerConnectionAndMissingLeaseStopsCleanly()
    {
        var calls = 0;
        var t = new OpenAiRealtimeTranscriber(_ => { calls++; return Task.FromResult<string?>(null); }, new RealtimeTranscriberOptions { ServerConfiguredSession = true });
        var statuses = new List<string>();
        t.StatusChanged += (s, d) => statuses.Add($"{s}:{d}");
        await t.StartAsync(default);
        for (int i = 0; i < 50 && !statuses.Any(s => s.StartsWith("Failed")); i++) await Task.Delay(20);
        Assert.Equal(1, calls);
        Assert.Contains(statuses, s => s.Contains("Live session not active"));
        await t.DisposeAsync();
    }
}
