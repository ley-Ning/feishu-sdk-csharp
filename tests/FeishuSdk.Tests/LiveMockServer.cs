using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Feishu.Ws;

namespace FeishuSdk.Tests;

/// <summary>
/// 本地 mock 飞书服务端：HTTP（API 端点）+ WebSocket（事件长连接）。
/// 用于在无真实凭证时验证 SDK 走真实网络栈的完整链路（E2E-local）。
/// 凭证到位后仅需把 BaseUrl/WS Domain 换回 open.feishu.cn 即为真机验证。
/// </summary>
public sealed class MockFeishuServer : IDisposable
{
    private readonly HttpListener _http = new();
    private readonly HttpListener _wsListener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly int _port;

    public string BaseUrl => $"http://127.0.0.1:{_port}";
    public List<string> ReceivedPaths { get; } = new();
    public string LastSentText { get; private set; } = "";
    public string LastSentCard { get; private set; } = "";
    /// <summary>SDK 通过 WS 上行的所有非空帧（含事件回执与心跳）。</summary>
    public List<byte[]> ReceivedWsFrames { get; } = new();

    public MockFeishuServer()
    {
        _port = GetFreePort();
        _http.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _http.Start();

        // WS 监听器同步启动：构造返回时 WsPort 已就绪，bootstrap 返回的 URL 必然可拨。
        // 注意注册根前缀：真实连接路径 /callback/ws 无尾斜杠，注册 /callback/ws/ 会导致
        // managed HttpListener 前缀不匹配 → 握手请求永远无响应 → ConnectAsync 挂起。
        WsPort = GetFreePort();
        _wsListener.Prefixes.Add($"http://127.0.0.1:{WsPort}/");
        _wsListener.Start();

        _ = Task.Run(WsAcceptLoopAsync);
        _ = Task.Run(HttpLoopAsync);
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task HttpLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _http.GetContextAsync(); }
            catch (Exception) { break; }

            var path = ctx.Request.Url!.AbsolutePath;
            var body = new StreamReader(ctx.Request.InputStream).ReadToEndAsync().GetAwaiter().GetResult();
            lock (ReceivedPaths) ReceivedPaths.Add(ctx.Request.Url.PathAndQuery);
            HandleLastSent(path, body);

            var (status, respBody) = path switch
            {
                "/open-apis/auth/v3/tenant_access_token/internal" =>
                    (200, """{"code":0,"expire":7200,"tenant_access_token":"mock-tat"}"""),
                "/callback/ws/endpoint" => (200, BootstrapBody),
                "/open-apis/bot/v3/info" =>
                    (200, """{"code":0,"bot":{"open_id":"ou_mock_bot","app_name":"MockBot","activate_status":1}}"""),
                "/open-apis/contact/v3/users" => (200, """{"code":0,"data":{}}"""),
                var p when p.StartsWith("/open-apis/contact/v3/users/") =>
                    (200, """{"code":0,"data":{"name":"Mock用户","open_id":"ou_mock_1"}}"""),
                "/open-apis/im/v1/messages" =>
                    (200, """{"code":0,"data":{"message_id":"om_mock_1","chat_id":"oc_mock"}}"""),
                "/open-apis/im/v1/messages/om_stream/reply" =>
                    (200, """{"code":0,"data":{"message_id":"om_mock_chunk"}}"""),
                var p when p.Contains("/messages/") && ctx.Request.HttpMethod == "PUT" =>
                    (200, """{"code":0,"data":{}}"""),
                _ => (200, """{"code":0,"data":{}}"""),
            };

            var buf = Encoding.UTF8.GetBytes(respBody);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.Headers["X-Tt-Logid"] = $"mock-{Guid.NewGuid():N}"[..18];
            ctx.Response.OutputStream.Write(buf, 0, buf.Length);
            ctx.Response.Close();
        }
    }

    private void HandleLastSent(string path, string body)
    {
        if (path == "/open-apis/im/v1/messages")
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("msg_type", out var mt))
                {
                    var type = mt.GetString();
                    if (type == "interactive") LastSentCard = body;
                    else if (type == "post" || type == "text") LastSentText = body;
                }
            }
            catch { /* ignore */ }
        }
    }

    /// <summary>向所有已连接的 WS 客户端推送一条 im.message.receive_v1 事件帧（pbbp2 封装，对齐真实服务端）。</summary>
    public async Task PushMessageEventAsync(string text = "你好 mock")
    {
        var eventId = "ev_" + Guid.NewGuid().ToString("N")[..8];
        var json = "{\"schema\":\"2.0\",\"header\":{\"event_id\":\"" + eventId +
                   "\",\"event_type\":\"im.message.receive_v1\",\"create_time\":\"" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() +
                   "\"},\"event\":{\"sender\":{\"sender_id\":{\"open_id\":\"ou_mock_user\"},\"sender_type\":\"user\"}," +
                   "\"message\":{\"message_id\":\"om_evt_1\",\"chat_id\":\"oc_mock\",\"chat_type\":\"p2p\",\"message_type\":\"text\"," +
                   "\"content\":\"{\\\"text\\\":\\\"" + text + "\\\"}\"}}}";
        var frame = new WsFrame { Method = 1, Payload = Encoding.UTF8.GetBytes(json) };
        frame.SetHeader("type", "event");
        frame.SetHeader("message_id", eventId);
        frame.SetHeader("sum", "1");
        frame.SetHeader("seq", "0");
        await BroadcastAsync(frame.ToBytes());
    }

    private readonly List<WebSocket> _sockets = new();
    private readonly object _sockGate = new();

    private async Task WsAcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _wsListener.GetContextAsync(); }
            catch (Exception) { break; }
            if (!ctx.Request.IsWebSocketRequest) { ctx.Response.Close(); continue; }
            var wsCtx = await ctx.AcceptWebSocketAsync(null);
            lock (_sockGate) _sockets.Add(wsCtx.WebSocket);
            _ = Task.Run(() => PumpAsync(wsCtx.WebSocket));
        }
    }

    public int WsPort { get; private set; }

    /// <summary>已连上的 WS 客户端数（服务端 accept 完成后才计入）。</summary>
    public int ConnectedSockets
    {
        get { lock (_sockGate) return _sockets.Count; }
    }

    /// <summary>等待至少一个 WS 客户端完成服务端 accept（客户端 ConnectAsync 返回略早于服务端注册）。</summary>
    public async Task WaitForWsClientAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ConnectedSockets > 0) return;
            await Task.Delay(20);
        }
        throw new TimeoutException($"no ws client connected within {timeout.TotalSeconds}s");
    }

    private async Task PumpAsync(WebSocket ws)
    {
        var buf = new byte[8192];
        try
        {
            while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), _cts.Token);
                    message.Write(buf, 0, result.Count);
                } while (!result.EndOfMessage);
                if (message.Length > 0)
                    lock (ReceivedWsFrames) ReceivedWsFrames.Add(message.ToArray());
            }
        }
        catch { /* closed */ }
        lock (_sockGate) _sockets.Remove(ws);
    }

    /// <summary>bootstrap 端点：返回 WS URL（供测试模拟 getConnURL）。</summary>
    public string BootstrapBody =>
            "{\"code\":0,\"data\":{\"URL\":\"ws://127.0.0.1:" + WsPort + "/callback/ws?device_id=d1&service_id=110\",\"ClientConfig\":{\"PingInterval\":1}}}";

    private async Task BroadcastAsync(byte[] data)
    {
        List<WebSocket> snapshot;
        lock (_sockGate) snapshot = [.. _sockets];
        foreach (var ws in snapshot)
        {
            try { await ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, _cts.Token); }
            catch { /* ignore */ }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Close();
        _wsListener.Close();
        lock (_sockGate)
            foreach (var ws in _sockets)
                try { ws.Dispose(); } catch { }
    }
}
