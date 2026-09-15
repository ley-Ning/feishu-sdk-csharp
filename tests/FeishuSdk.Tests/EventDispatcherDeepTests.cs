using Feishu;
using System.Text;
using Feishu.Events;
using Feishu.Services.Im;

namespace FeishuSdk.Tests;

/// <summary>事件分发器深入用例（v1 事件 / SkipSignVerify / OnCallback / 多处理器 / Bind 端到端）。</summary>
public class EventDispatcherDeepTests
{
    private static EventRequest PlainRequest(string body, IDictionary<string, string>? headers = null) => new()
    {
        Headers = new Dictionary<string, string>(headers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
        Body = Encoding.UTF8.GetBytes(body),
        Path = "/event",
    };

    // ---- v1 事件：事件类型取自 event.type（而非 header.event_type） ----
    [Fact]
    public async Task V1_Event_Type_Should_Be_Resolved_From_EventField()
    {
        var got = new List<byte[]>();
        var dispatcher = new EventDispatcher()
            .OnRaw("contact.user.created_v1", (raw, _) => { got.Add(raw); return Task.CompletedTask; });

        var body = """{"schema":"1.0","event":{"type":"contact.user.created_v1","user_id":"u1"}}""";
        var resp = await dispatcher.HandleAsync(PlainRequest(body));

        Assert.Equal(200, resp.StatusCode);
        var single = Assert.Single(got);
        Assert.Contains("u1", Encoding.UTF8.GetString(single));
    }

    // ---- SkipSignVerify：坏签名放行 ----
    [Fact]
    public async Task SkipSignVerify_Should_Bypass_Bad_Signature()
    {
        var hits = 0;
        var key = "k1";
        var dispatcher = new EventDispatcher("v", key)
        {
            SkipSignVerify = true,
        };
        dispatcher.OnRaw("app_ticket", (_, _) => { hits++; return Task.CompletedTask; });

        var plain = """{"schema":"2.0","header":{"event_type":"app_ticket"},"event":{"app_ticket":"t1"}}""";
        var body = $$"""{"encrypt":"{{EventCrypto.Encrypt(plain, key)}}"}""";
        var resp = await dispatcher.HandleAsync(PlainRequest(body, new Dictionary<string, string>
        {
            ["X-Lark-Signature"] = "bad",
        }));

        Assert.Equal(200, resp.StatusCode);
        Assert.Equal(1, hits);
    }

    // ---- OnCallback：返回值序列化为响应体 ----
    [Fact]
    public async Task OnCallback_Result_Should_Be_The_ResponseBody()
    {
        var dispatcher = new EventDispatcher()
            .OnCallback<AppTicketEvent, CardReply>("app_ticket", (e, _) =>
                Task.FromResult<CardReply?>(new CardReply { Ok = e.AppTicket == "tk-77" }));

        var body = """{"schema":"2.0","header":{"event_type":"app_ticket"},"event":{"app_ticket":"tk-77"}}""";
        var resp = await dispatcher.HandleAsync(PlainRequest(body));

        Assert.Equal(200, resp.StatusCode);
        var text = Encoding.UTF8.GetString(resp.Body);
        Assert.Contains("true", text);
        Assert.Contains("ok", text);
    }

    private sealed class CardReply
    {
        public bool Ok { get; set; }
    }

    // ---- 同一事件多处理器：全部按序执行 ----
    [Fact]
    public async Task Multiple_Handlers_Should_Run_In_Order()
    {
        var order = new List<int>();
        var dispatcher = new EventDispatcher()
            .OnRaw("app_ticket", (_, _) => { order.Add(1); return Task.CompletedTask; })
            .OnRaw("app_ticket", (_, _) => { order.Add(2); return Task.CompletedTask; });

        await dispatcher.HandleAsync(PlainRequest(
            """{"schema":"2.0","header":{"event_type":"app_ticket"},"event":{"app_ticket":"x"}}"""));

        Assert.Equal([1, 2], order);
    }

    // ---- 未注册事件类型：HTTP 200 + msg=success（对齐 Go：记日志但不阻断重推风暴） ----
    [Fact]
    public async Task Unregistered_Event_Should_Still_Return_Success()
    {
        var dispatcher = new EventDispatcher();

        var resp = await dispatcher.HandleAsync(PlainRequest(
            """{"schema":"2.0","header":{"event_type":"nope.event"},"event":{}}"""));

        Assert.Equal(200, resp.StatusCode);
    }

    // ---- 加密体缺 encrypt 字段 → 500 ----
    [Fact]
    public async Task Encrypted_Body_Missing_Field_Should_500()
    {
        var dispatcher = new EventDispatcher("v", "k1");

        var resp = await dispatcher.HandleAsync(PlainRequest("""{"foo":1}"""));

        Assert.Equal(500, resp.StatusCode);
    }

    // ---- Bind：app_ticket 事件 → 客户端缓存 → 随后商店应用取 token 走通 ----
    [Fact]
    public async Task Bind_AppTicket_Should_Feed_Marketplace_Token_Flow()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/app_access_token"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"app_access_token":"t-app"}"""));
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/tenant_access_token"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t-market"}"""));
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/app_ticket/resend"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0}"""));
            if (req.PathAndQuery.StartsWith("/open-apis/im/v1/messages"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"msg":"ok"}"""));
            return Task.FromResult(FakeHandler.Json(404, "{}"));
        }, o => o.AppType = FeishuAppType.Marketplace);

        var dispatcher = new EventDispatcher("v").Bind(client);

        // 1) 收到 app_ticket 事件 → 写入缓存
        var resp = await dispatcher.HandleAsync(PlainRequest(
            """{"schema":"2.0","header":{"event_type":"app_ticket","app_id":"cli_test"},"event":{"app_ticket":"ticket-live"}}"""));
        Assert.Equal(200, resp.StatusCode);

        // 2) 商店应用取 tenant token：用缓存的 app_ticket 换 app_access_token → tenant_access_token
        var token = await client.GetTenantAccessTokenAsync("tk-tenant");
        Assert.Equal("t-market", token);

        // app_access_token 请求体应携带 ticket-live
        var appTokenReq = handler.Requests.First(r => r.PathAndQuery.StartsWith("/open-apis/auth/v3/app_access_token"));
        Assert.Contains("ticket-live", appTokenReq.BodyText);
    }

    // ---- WS 裸分发：类型缺失返回 false ----
    [Fact]
    public async Task WsDispatch_MissingType_Should_Return_False()
    {
        var dispatcher = new EventDispatcher();

        var ok = await dispatcher.DispatchAsync("""{"schema":"2.0","event":{}}"""u8.ToArray());

        Assert.False(ok);
    }

    // ---- WS 裸分发：命中的强类型事件（信封裁剪） ----
    [Fact]
    public async Task WsDispatch_Should_Deserialize_Envelope_Event()
    {
        P2MessageReceiveV1? got = null;
        var dispatcher = new EventDispatcher()
            .On<P2MessageReceiveV1>(ImEventTypes.MessageReceiveV1, (e, _) => { got = e; return Task.CompletedTask; });

        var payload = "{\"schema\":\"2.0\",\"header\":{\"event_type\":\"" + ImEventTypes.MessageReceiveV1 +
                      "\"},\"event\":{\"sender\":{\"sender_id\":{\"open_id\":\"ou_9\"},\"sender_type\":\"app\"}," +
                      "\"message\":{\"message_id\":\"om_1\",\"chat_id\":\"oc_1\",\"chat_type\":\"p2p\",\"message_type\":\"text\"," +
                      "\"content\":\"{\\\"text\\\":\\\"hi\\\"}\"}}}";

        var ok = await dispatcher.DispatchAsync(Encoding.UTF8.GetBytes(payload));

        Assert.True(ok);
        Assert.NotNull(got);
        Assert.Equal("ou_9", got!.Sender?.SenderId?.OpenId);
        Assert.Equal("om_1", got.Message?.MessageId);
        Assert.Equal("p2p", got.Message?.ChatType);
    }
}
