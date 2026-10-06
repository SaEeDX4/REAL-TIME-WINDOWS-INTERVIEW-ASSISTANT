using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using InterviewAssistant.Core.Diagnostics;

namespace InterviewAssistant.Core.Providers;

public sealed class RealtimeTranscriberOptions
{
    public string Endpoint { get; set; } = "wss://api.openai.com/v1/realtime?intent=transcription";
    public string Model { get; set; } = "gpt-4o-transcribe";
    public string Language { get; set; } = "en";
    /// <summary>Vocabulary prompt that biases recognition toward domain terms.</summary>
    public string Prompt { get; set; } =
        "Job interview for a Product Owner role at Teroxx. Terms: Shervin Fallahdoust, Teroxx, Abloxx, XAB, Arzif, Binance, CoinEx, KuCoin, " +
        "MiCA, MiCAR, CySEC, Jira, Agile, Scrum, Product Owner, tokenomics, ledger, reconciliation, idempotency, wallet, VIP, cashback, " +
        "reward booster, Crypto Grow, ERC-20, blockchain, FinTech, staking, yield, retention, acceptance criteria, KYC, AML, Lite, Silver, Gold, Platinum.";
    public double VadThreshold { get; set; } = 0.5;
    public int VadPrefixPaddingMs { get; set; } = 300;
    /// <summary>Server VAD segment silence. Short so segments arrive quickly; TurnDetector merges segments into a turn.</summary>
    public int VadSilenceMs { get; set; } = 400;
    /// <summary>"ga" (current Realtime API) or "beta" (legacy transcription_session.update). "auto" tries GA then falls back.</summary>
    public string Protocol { get; set; } = "auto";
    /// <summary>Proactively renew the session when idle after this age (sessions have a maximum lifetime).</summary>
    public TimeSpan SessionRotateAfter { get; set; } = TimeSpan.FromMinutes(25);
}

/// <summary>
/// OpenAI Realtime transcription over WebSocket. Owns its connection lifecycle: connect, configure session,
/// stream audio from a bounded queue (drop-oldest backpressure), parse events, and reconnect with bounded
/// exponential backoff. Never throws into callers after StartAsync; problems surface as StatusChanged.
/// </summary>
public sealed class OpenAiRealtimeTranscriber : ITranscriber
{
    private readonly Func<string?> _apiKey;
    private readonly RealtimeTranscriberOptions _opt;
    private readonly Channel<byte[]> _audio = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(30) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _useBeta;
    private volatile bool _inSpeech;

    public event Action? SpeechStarted;
    public event Action? SpeechStopped;
    public event Action<string, string>? PartialTranscript;
    public event Action<string, string>? SegmentCompleted;
    public event Action<ConnectionStatus, string?>? StatusChanged;
    public event Action<string>? Diagnostic;

    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Disconnected;
    public int ReconnectCount { get; private set; }
    public long AudioBytesSent { get; private set; }

    public OpenAiRealtimeTranscriber(Func<string?> apiKeyProvider, RealtimeTranscriberOptions? options = null)
    {
        _apiKey = apiKeyProvider;
        _opt = options ?? new RealtimeTranscriberOptions();
        _useBeta = _opt.Protocol.Equals("beta", StringComparison.OrdinalIgnoreCase);
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (_loop != null) return Task.CompletedTask;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_loop != null) { try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        _loop = null;
        while (_audio.Reader.TryRead(out _)) { } // drop buffered audio (privacy + no stale audio on restart)
        SetStatus(ConnectionStatus.Disconnected, null);
    }

    public void SendAudio(byte[] pcm16Frame) => _audio.Writer.TryWrite(pcm16Frame);

    private async Task RunAsync(CancellationToken ct)
    {
        var backoff = new ExponentialBackoff();
        while (!ct.IsCancellationRequested)
        {
            var key = _apiKey();
            if (string.IsNullOrWhiteSpace(key)) { SetStatus(ConnectionStatus.Failed, "No API key configured"); return; }
            SetStatus(ReconnectCount == 0 && backoff.Attempt == 0 ? ConnectionStatus.Connecting : ConnectionStatus.Reconnecting, null);
            using var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Authorization", "Bearer " + key);
            if (_useBeta) ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            string? failure = null;
            bool fatal = false;
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(TimeSpan.FromSeconds(10));
                await ws.ConnectAsync(new Uri(_opt.Endpoint), connectCts.Token).ConfigureAwait(false);
                await SendJsonAsync(ws, BuildSessionUpdate(), ct).ConfigureAwait(false);
                SetStatus(ConnectionStatus.Connected, _useBeta ? "beta protocol" : null);
                backoff.Reset();
                var started = DateTime.UtcNow;
                using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var receive = ReceiveLoopAsync(ws, sessionCts);
                var send = SendLoopAsync(ws, started, sessionCts.Token);
                var first = await Task.WhenAny(receive, send).ConfigureAwait(false);
                sessionCts.Cancel();
                try { await Task.WhenAll(receive, send).ConfigureAwait(false); } catch (OperationCanceledException) { }
                failure = (first.Exception?.GetBaseException().Message) ?? _lastServerError;
                fatal = _fatal;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (WebSocketException ex) { failure = DescribeConnectError(ex); fatal = failure.Contains("401") || failure.Contains("403"); }
            catch (Exception ex) { failure = ex.Message; }
            finally
            {
                if (ws.State == WebSocketState.Open)
                {
                    try { using var c = new CancellationTokenSource(1000); await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", c.Token).ConfigureAwait(false); } catch { /* closing best-effort */ }
                }
            }
            if (ct.IsCancellationRequested) break;
            if (fatal) { SetStatus(ConnectionStatus.Failed, failure ?? "Authentication failed"); return; }
            if (_rotateRequested) { _rotateRequested = false; continue; } // planned renewal, reconnect immediately
            ReconnectCount++;
            var delay = backoff.Next();
            SetStatus(ConnectionStatus.Reconnecting, $"{failure ?? "connection closed"} — retry in {delay.TotalSeconds:0}s");
            try { await Task.Delay(delay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    private string? _lastServerError;
    private bool _fatal;
    private bool _rotateRequested;

    private static string DescribeConnectError(WebSocketException ex)
    {
        var msg = ex.Message;
        if (msg.Contains("401")) return "401 Unauthorized — check the API key";
        if (msg.Contains("403")) return "403 Forbidden — key lacks realtime access";
        if (msg.Contains("429")) return "429 rate limited";
        return msg;
    }

    private async Task SendLoopAsync(ClientWebSocket ws, DateTime started, CancellationToken ct)
    {
        var sb = new StringBuilder(16 * 1024);
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            byte[] frame;
            using (var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                waitCts.CancelAfter(TimeSpan.FromSeconds(5));
                try { frame = await _audio.Reader.ReadAsync(waitCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { frame = Array.Empty<byte>(); }
            }
            if (frame.Length > 0)
            {
                sb.Clear();
                sb.Append("{\"type\":\"input_audio_buffer.append\",\"audio\":\"").Append(Convert.ToBase64String(frame)).Append("\"}");
                await ws.SendAsync(Encoding.UTF8.GetBytes(sb.ToString()), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                AudioBytesSent += frame.Length;
            }
            if (!_inSpeech && DateTime.UtcNow - started > _opt.SessionRotateAfter)
            {
                _rotateRequested = true;
                Diagnostic?.Invoke("Rotating realtime session (age limit) during silence");
                return;
            }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationTokenSource sessionCts)
    {
        var ct = sessionCts.Token;
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        _lastServerError = null; _fatal = false;
        var connectedAt = DateTime.UtcNow;
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            ms.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _lastServerError ??= $"server closed: {result.CloseStatus} {result.CloseStatusDescription}";
                    return;
                }
                ms.Write(buffer, 0, result.Count);
                if (ms.Length > 4 * 1024 * 1024) throw new InvalidOperationException("Oversized realtime message");
            } while (!result.EndOfMessage);

            HandleEvent(ms.GetBuffer().AsSpan(0, (int)ms.Length), connectedAt);
            if (_fatal || _switchProtocol) { _switchProtocol = false; return; }
        }
    }

    private bool _switchProtocol;

    internal void HandleEvent(ReadOnlySpan<byte> json, DateTime connectedAt)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); } catch (JsonException) { return; }
        var type = node?["type"]?.GetValue<string>() ?? "";
        switch (type)
        {
            case "input_audio_buffer.speech_started":
                _inSpeech = true; SpeechStarted?.Invoke(); break;
            case "input_audio_buffer.speech_stopped":
                _inSpeech = false; SpeechStopped?.Invoke(); break;
            case "conversation.item.input_audio_transcription.delta":
                PartialTranscript?.Invoke(node?["item_id"]?.GetValue<string>() ?? "", node?["delta"]?.GetValue<string>() ?? ""); break;
            case "conversation.item.input_audio_transcription.completed":
                SegmentCompleted?.Invoke(node?["item_id"]?.GetValue<string>() ?? "", node?["transcript"]?.GetValue<string>() ?? ""); break;
            case "conversation.item.input_audio_transcription.failed":
                Diagnostic?.Invoke("Segment transcription failed: " + node?["error"]?["message"]?.GetValue<string>());
                SegmentCompleted?.Invoke(node?["item_id"]?.GetValue<string>() ?? "", ""); break;
            case "error":
                var code = node?["error"]?["code"]?.GetValue<string>() ?? "";
                var message = node?["error"]?["message"]?.GetValue<string>() ?? "unknown error";
                _lastServerError = string.IsNullOrEmpty(code) ? message : $"{code}: {message}";
                Diagnostic?.Invoke("Realtime error: " + _lastServerError);
                if (code is "invalid_api_key" or "insufficient_quota" or "model_not_found") _fatal = true;
                // Session config rejected shortly after connect on GA protocol -> try legacy beta protocol once.
                else if (!_useBeta && _opt.Protocol == "auto" && (DateTime.UtcNow - connectedAt).TotalSeconds < 5 &&
                         (message.Contains("session", StringComparison.OrdinalIgnoreCase) || code.Contains("param", StringComparison.OrdinalIgnoreCase) || code.Contains("unknown", StringComparison.OrdinalIgnoreCase)))
                {
                    _useBeta = true; _switchProtocol = true; _rotateRequested = true;
                    Diagnostic?.Invoke("Switching to legacy realtime transcription protocol");
                }
                break;
        }
    }

    internal string BuildSessionUpdate()
    {
        var turnDetection = new JsonObject
        {
            ["type"] = "server_vad",
            ["threshold"] = _opt.VadThreshold,
            ["prefix_padding_ms"] = _opt.VadPrefixPaddingMs,
            ["silence_duration_ms"] = _opt.VadSilenceMs,
        };
        var transcription = new JsonObject { ["model"] = _opt.Model, ["language"] = _opt.Language, ["prompt"] = _opt.Prompt };
        JsonObject msg = _useBeta
            ? new JsonObject
            {
                ["type"] = "transcription_session.update",
                ["session"] = new JsonObject
                {
                    ["input_audio_format"] = "pcm16",
                    ["input_audio_transcription"] = transcription,
                    ["turn_detection"] = turnDetection,
                },
            }
            : new JsonObject
            {
                ["type"] = "session.update",
                ["session"] = new JsonObject
                {
                    ["type"] = "transcription",
                    ["audio"] = new JsonObject
                    {
                        ["input"] = new JsonObject
                        {
                            ["format"] = new JsonObject { ["type"] = "audio/pcm", ["rate"] = 24000 },
                            ["transcription"] = transcription,
                            ["turn_detection"] = turnDetection,
                        },
                    },
                },
            };
        return msg.ToJsonString();
    }

    private static Task SendJsonAsync(ClientWebSocket ws, string json, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);

    private void SetStatus(ConnectionStatus s, string? detail)
    {
        Status = s;
        StatusChanged?.Invoke(s, detail);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts?.Dispose();
    }
}
