using System.Net;
using System.Net.Sockets;
using System.Text;

namespace InterviewAssistant.Client;

/// <summary>
/// One-shot loopback redirect receiver (RFC 8252 §7.3) on 127.0.0.1 with an OS-assigned port. Implemented on a raw
/// TcpListener rather than HttpListener/HTTP.sys so it needs no URL ACL or admin rights on Windows. Only GET
/// /callback with the expected state is accepted; everything else gets 404. Requests are size- and time-bounded.
/// </summary>
public sealed class LoopbackRedirect : IDisposable
{
    private const int MaxRequestBytes = 16 * 1024;
    private readonly TcpListener _listener;
    public int Port { get; }
    public string State { get; }
    public string RedirectUri => $"http://127.0.0.1:{Port}/callback?s={Uri.EscapeDataString(State)}";

    public LoopbackRedirect(string state, int port = 0)
    {
        State = state;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start(backlog: 8);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    /// <summary>Waits for the browser to hit the redirect; returns the authorization code.</summary>
    public async Task<string> WaitForCodeAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        while (true)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException("Sign-in was not completed in the browser.");
            }
            catch (ObjectDisposedException) { throw new TimeoutException("Sign-in was cancelled."); }

            using (client)
            {
                var (status, message, code, error) = await HandleAsync(client, cts.Token);
                if (error != null) throw new AuthException("browser_error", error);
                if (code != null) return code;
                _ = status; _ = message;
            }
        }
    }

    private async Task<(int Status, string Message, string? Code, string? Error)> HandleAsync(TcpClient client, CancellationToken ct)
    {
        var stream = client.GetStream();
        string? target = null;
        try
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(TimeSpan.FromSeconds(10));
            var buf = new byte[MaxRequestBytes];
            int len = 0;
            while (len < buf.Length)
            {
                var n = await stream.ReadAsync(buf.AsMemory(len), readCts.Token);
                if (n == 0) break;
                len += n;
                if (Encoding.ASCII.GetString(buf, 0, len).Contains("\r\n\r\n")) break;
            }
            var head = Encoding.ASCII.GetString(buf, 0, len);
            var first = head.Split("\r\n", 2)[0].Split(' ');
            if (first.Length == 3 && first[0] == "GET" && first[2].StartsWith("HTTP/1.")) target = first[1];
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { return (0, "", null, null); }

        if (target == null) { await RespondAsync(stream, 400, "Bad request."); return (400, "", null, null); }
        var uri = new Uri("http://127.0.0.1" + target);
        var q = ParseQuery(uri.Query);
        if (uri.AbsolutePath.TrimEnd('/') != "/callback" || q.GetValueOrDefault("s") != State)
        {
            await RespondAsync(stream, 404, "Not found.");
            return (404, "", null, null);
        }
        var error = q.GetValueOrDefault("error_description") ?? q.GetValueOrDefault("error");
        if (!string.IsNullOrEmpty(error))
        {
            await RespondAsync(stream, 400, "Sign-in failed. You can close this tab and try again in the app.");
            return (400, "", null, error);
        }
        var code = q.GetValueOrDefault("code");
        if (string.IsNullOrEmpty(code)) { await RespondAsync(stream, 400, "Missing authorization code."); return (400, "", null, null); }
        await RespondAsync(stream, 200, "Signed in. You can close this tab and return to Interview Assistant.");
        return (200, "", code, null);
    }

    internal static Dictionary<string, string> ParseQuery(string query)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            var k = Uri.UnescapeDataString(kv[0].Replace('+', ' '));
            if (!d.ContainsKey(k)) d[k] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
        }
        return d;
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string message)
    {
        var html = "<!doctype html><meta charset=utf-8><title>Interview Assistant</title><body style=\"font-family:system-ui;padding:3em\"><p>" + WebUtility.HtmlEncode(message) + "</p>";
        var body = Encoding.UTF8.GetBytes(html);
        var reason = status switch { 200 => "OK", 400 => "Bad Request", _ => "Not Found" };
        var headers = $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\n" +
                      "Cache-Control: no-store\r\nContent-Security-Policy: default-src 'none'; style-src 'unsafe-inline'\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n";
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers));
            await stream.WriteAsync(body);
            await stream.FlushAsync();
            stream.Socket.Shutdown(SocketShutdown.Send);   // graceful close: the browser receives the full page
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
    }

    public void Dispose() => _listener.Stop();
}
