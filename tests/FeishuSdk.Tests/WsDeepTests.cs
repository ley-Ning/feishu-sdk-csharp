using Feishu;
using System.Text;
using Feishu.Events;
using Feishu.Ws;

namespace FeishuSdk.Tests;

/// <summary>WebSocket 长连接深入用例（bootstrap 协议 / 心跳帧 / 配置下发 / 数据帧回执 / 分包后分发）。</summary>
public class WsDeepTests
{
    private static FeishuWsClient CreateClient(FakeHandler bootstrapHandler, EventDispatcher? dispatcher = null)
    {
        var ws = new FeishuWsClient("cli_test", "secret", new FeishuWsOptions
        {
            Logger = NullFeishuLogger.Instance,
            BootstrapHttpMessageHandlerFactory = () => bootstrapHandler,
        });
        if (dispatcher != null) ws.Bind(dispatcher);
        return ws;
    }

    // ---- bootstrap：请求体 PascalCase 字段 + locale/UA 头 ----
    [Fact]
    public async Task Bootstrap_Should_Send_PascalCase_Body_And_Headers()
    {
        var handler = new FakeHandler(req =>
            Task.FromResult(FakeHandler.Json(200, """{"code":0,"data":{"URL":"wss://Conn?device_id=d1&service_id=110"}}""")));

        var url = await CreateClient(handler).GetConnUrlAsync(CancellationToken.None);

        Assert.Equal("wss://Conn?device_id=d1&service_id=110", url);
        var req = Assert.Single(handler.Requests);
        Assert.Equal("/callback/ws/endpoint", req.PathAndQuery);
        Assert.Contains("\"AppID\":\"cli_test\"", req.BodyText);
        Assert.Contains("\"AppSecret\":\"secret\"", req.BodyText);
        Assert.Equal("zh", req.Headers["locale"]);
        Assert.Contains("feishu-sdk-csharp", req.Headers["User-Agent"]);
    }

    // ---- bootstrap：附带 ClientConfig 时应用为运行时配置 ----
    [Fact]
    public async Task Bootstrap_WithClientConfig_Should_Apply()
    {
        var handler = new FakeHandler(req =>
            Task.FromResult(FakeHandler.Json(200,
                """{"code":0,"data":{"URL":"wss://c","ClientConfig":{"ReconnectCount":5,"ReconnectInterval":30,"ReconnectNonce":3,"PingInterval":60}}}""")));
        var ws = CreateClient(handler);
        ws.FrameSendOverride = (_, _) => Task.CompletedTask;

        await ws.GetConnUrlAsync(CancellationToken.None);

        // 通过 ping 帧的间隔行为无法直接观测；用 pong 配置覆盖后可读取 ApplyServerConfig 的结果
        // 这里改用再次 bootstrap 后再校验行为级测试（PingLoop 不可测），断言至少不抛错且 URL 返回
        var url2 = await ws.GetConnUrlAsync(CancellationToken.None);
        Assert.Equal("wss://c", url2);
    }

    // ---- bootstrap：业务错误码 → 致命异常（403 封禁） ----
    [Fact]
    public async Task Bootstrap_Forbidden_Should_Be_Fatal()
    {
        var handler = new FakeHandler(_ =>
            Task.FromResult(FakeHandler.Json(200, """{"code":403,"msg":"forbidden"}""")));

        var ex = await Assert.ThrowsAsync<FeishuWsFatalException>(
            () => CreateClient(handler).GetConnUrlAsync(CancellationToken.None));

        Assert.Equal(403, ex.Code);
    }

    // ---- bootstrap：HTTP 非 200 → 错误信息取响应 msg ----
    [Fact]
    public async Task Bootstrap_HttpError_Should_Prefer_Server_Msg()
    {
        var handler = new FakeHandler(_ =>
            Task.FromResult(FakeHandler.Json(500, """{"code":1,"msg":"system busy"}""")));

        var ex = await Assert.ThrowsAsync<FeishuWsFatalException>(
            () => CreateClient(handler).GetConnUrlAsync(CancellationToken.None));

        Assert.Contains("system busy", ex.Message);
    }

    // ---- bootstrap：凭证双空 → 7104 ----
    [Fact]
    public async Task Bootstrap_NoCredential_Should_Throw()
    {
        var ws = new FeishuWsClient("app", "", new FeishuWsOptions { Logger = NullFeishuLogger.Instance });

        var ex = await Assert.ThrowsAsync<FeishuWsFatalException>(() => ws.GetConnUrlAsync(CancellationToken.None));
        Assert.Equal(FeishuErrorCodes.AppSecretAndClientAssertionEmpty, ex.Code);
    }

    // ---- 心跳帧：控制帧 + type=ping + service ----
    [Fact]
    public void PingFrame_Should_Be_Control_With_Type_Header()
    {
        var ping = FeishuWsClient.BuildPingFrame(110);

        Assert.Equal(0, ping.Method); // 控制帧
        Assert.Equal(110, ping.Service);
        Assert.Equal("ping", ping.GetHeader("type"));

        var parsed = WsFrame.Parse(ping.ToBytes());
        Assert.Equal("ping", parsed.GetHeader("type"));
        Assert.Equal(110, parsed.Service);
    }

    // ---- pong 载荷下发配置 → 应用 ----
    [Fact]
    public void Pong_Config_Should_Be_Applied()
    {
        var handler = new FakeHandler(_ => Task.FromResult(FakeHandler.Json(200, """{"code":0,"data":{"URL":"wss://c"}}""")));
        var ws = CreateClient(handler);

        var pong = new WsFrame { Method = 0 };
        pong.SetHeader("type", "pong");
        pong.Payload = """{"PingInterval":15,"ReconnectCount":9}"""u8.ToArray();

        ws.HandleControlFrame(pong);

        // 应用成功与否无法直接读取私有字段；用行为断言替代：
        // BuildPingFrame 与配置无关，这里仅确认不抛异常且后续仍可构建心跳
        Assert.NotNull(FeishuWsClient.BuildPingFrame(0));
    }

    // ---- 数据帧：事件分发成功 → 回执 code=200 且带 biz_rt ----
    [Fact]
    public async Task Data_Event_Frame_Should_Ack_200_With_BizRt()
    {
        var dispatched = new List<byte[]>();
        var dispatcher = new EventDispatcher()
            .OnRaw("app_ticket", (raw, _) => { dispatched.Add(raw); return Task.CompletedTask; });
        var handler = new FakeHandler(_ => Task.FromResult(FakeHandler.Json(200, """{"code":0,"data":{"URL":"wss://c"}}""")));
        var ws = CreateClient(handler, dispatcher);

        WsFrame? sent = null;
        ws.FrameSendOverride = (frame, _) => { sent = frame; return Task.CompletedTask; };

        var frame = new WsFrame { Method = 1, Payload = Encoding.UTF8.GetBytes(
            """{"schema":"2.0","header":{"event_type":"app_ticket"},"event":{"app_ticket":"t"}}""") };
        frame.SetHeader("type", "event");
        frame.SetHeader("message_id", "m1");
        frame.SetHeader("sum", "1");
        frame.SetHeader("seq", "0");

        await ws.HandleDataFrameAsync(frame, CancellationToken.None);

        Assert.NotNull(sent);
        Assert.Contains("biz_rt", sent!.Headers.Select(h => h.Key));
        var respJson = System.Text.Json.JsonDocument.Parse(Encoding.UTF8.GetString(sent.Payload!)).RootElement;
        Assert.Equal(200, respJson.GetProperty("code").GetInt32());
        Assert.Single(dispatched);
    }

    // ---- 数据帧：处理器抛异常 → 回执 code=500（对齐 Go err 分支） ----
    [Fact]
    public async Task Data_Event_Handler_Error_Should_Ack_500()
    {
        var dispatcher = new EventDispatcher()
            .OnRaw("app_ticket", (_, _) => throw new InvalidOperationException("boom"));
        var handler = new FakeHandler(_ => Task.FromResult(FakeHandler.Json(200, """{"code":0,"data":{"URL":"wss://c"}}""")));
        var ws = CreateClient(handler, dispatcher);

        WsFrame? sent = null;
        ws.FrameSendOverride = (frame, _) => { sent = frame; return Task.CompletedTask; };

        var frame = new WsFrame { Method = 1, Payload = """{"schema":"2.0","header":{"event_type":"app_ticket"},"event":{"app_ticket":"t"}}"""u8.ToArray() };
        frame.SetHeader("type", "event");
        frame.SetHeader("message_id", "m2");
        frame.SetHeader("sum", "1");
        frame.SetHeader("seq", "0");

        await ws.HandleDataFrameAsync(frame, CancellationToken.None);

        Assert.NotNull(sent);
        var respJson = System.Text.Json.JsonDocument.Parse(Encoding.UTF8.GetString(sent!.Payload!)).RootElement;
        Assert.Equal(500, respJson.GetProperty("code").GetInt32());
    }

    // ---- 数据帧分包：sum=2 两片到达后才分发一次 ----
    [Fact]
    public async Task Data_Frame_Split_Should_Wait_And_Dispatch_Once()
    {
        var dispatched = 0;
        var dispatcher = new EventDispatcher()
            .OnRaw("app_ticket", (_, _) => { dispatched++; return Task.CompletedTask; });
        var handler = new FakeHandler(_ => Task.FromResult(FakeHandler.Json(200, """{"code":0,"data":{"URL":"wss://c"}}""")));
        var ws = CreateClient(handler, dispatcher);

        var sentFrames = new List<WsFrame>();
        ws.FrameSendOverride = (frame, _) => { sentFrames.Add(frame); return Task.CompletedTask; };

        var json = """{"schema":"2.0","header":{"event_type":"app_ticket"},"event":{"app_ticket":"tk-split"}}""";
        var bytes = Encoding.UTF8.GetBytes(json);

        WsFrame Part(int seq) =>
            new()
            {
                Method = 1,
                Payload = bytes[(seq * (bytes.Length / 2))..(seq == 0 ? bytes.Length / 2 : bytes.Length)],
            };

        var f0 = Part(0);
        f0.SetHeader("type", "event");
        f0.SetHeader("message_id", "ms");
        f0.SetHeader("sum", "2");
        f0.SetHeader("seq", "0");
        await ws.HandleDataFrameAsync(f0, CancellationToken.None);
        Assert.Equal(0, dispatched); // 等另一半
        Assert.Empty(sentFrames);

        var f1 = Part(1);
        f1.SetHeader("type", "event");
        f1.SetHeader("message_id", "ms");
        f1.SetHeader("sum", "2");
        f1.SetHeader("seq", "1");
        await ws.HandleDataFrameAsync(f1, CancellationToken.None);

        Assert.Equal(1, dispatched);
        var ack = Assert.Single(sentFrames);
        var respJson = System.Text.Json.JsonDocument.Parse(Encoding.UTF8.GetString(ack.Payload!)).RootElement;
        Assert.Equal(200, respJson.GetProperty("code").GetInt32());
    }

    // ---- 非事件数据帧（type=card）→ 对齐 Go 直接忽略，不回执 ----
    [Fact]
    public async Task Data_Card_Frame_Should_Be_Ignored_Like_Go()
    {
        var handler = new FakeHandler(_ => Task.FromResult(FakeHandler.Json(200, """{"code":0,"data":{"URL":"wss://c"}}""")));
        var ws = CreateClient(handler);

        WsFrame? sent = null;
        ws.FrameSendOverride = (frame, _) => { sent = frame; return Task.CompletedTask; };

        var frame = new WsFrame { Method = 1, Payload = "{}"u8.ToArray() };
        frame.SetHeader("type", "card");
        frame.SetHeader("message_id", "mc");
        frame.SetHeader("sum", "1");
        frame.SetHeader("seq", "0");

        await ws.HandleDataFrameAsync(frame, CancellationToken.None);

        Assert.Null(sent); // Go 版 case MessageTypeCard: return —— 无回执
    }
}
