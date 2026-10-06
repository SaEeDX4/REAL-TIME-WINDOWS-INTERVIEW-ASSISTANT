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
        Assert.Equal("gpt-4.1-mini", body["model"]!.GetValue<string>());
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
    }

    [Fact]
    public void RealtimeSessionUpdateMatchesDocumentedShape()
    {
        var t = new OpenAiRealtimeTranscriber(() => "k");
        var ga = JsonNode.Parse(t.BuildSessionUpdate())!;
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
