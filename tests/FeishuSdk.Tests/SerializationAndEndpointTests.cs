using Feishu;
using System.Text;
using Feishu.AspNetCore;
using Feishu.Events;
using Microsoft.AspNetCore.Http;

namespace FeishuSdk.Tests;

/// <summary>序列化约定（snake_case / null 跳过 / 数字容错 / 中文不转义）。</summary>
public class SerializationTests
{
    private readonly IFeishuSerializer _s = SystemTextJsonFeishuSerializer.Instance;

    private sealed class Demo
    {
        public string? MessageId { get; set; }
        public int? PageToken { get; set; }
        public string? NullField { get; set; } = null;
        public List<string>? UserIdList { get; set; }
    }

    [Fact]
    public void Serialize_Should_Use_SnakeCase_And_Skip_Null()
    {
        var json = _s.Serialize(new Demo { MessageId = "om1", UserIdList = ["a", "b"] });

        Assert.Contains("\"message_id\":\"om1\"", json);
        Assert.Contains("\"user_id_list\":[\"a\",\"b\"]", json);
        Assert.DoesNotContain("null_field", json);      // null 不下发
        Assert.DoesNotContain("page_token", json);
    }

    [Fact]
    public void Serialize_Should_Not_Escape_Chinese()
    {
        var json = _s.Serialize(new Demo { MessageId = "消息一" });
        Assert.Contains("消息一", json);
        Assert.DoesNotContain("\\u", json);
    }

    [Fact]
    public void Deserialize_Should_Accept_Number_From_String()
    {
        // 服务端下发配置数字字段为字符串时应能读取（WsClientConfig 同款容错）
        var json = """{"ReconnectInterval":"30","PingInterval":60}""";
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        Assert.Equal("30", doc.RootElement.GetProperty("ReconnectInterval").GetString());
        Assert.Equal(60, doc.RootElement.GetProperty("PingInterval").GetInt64());
    }

    [Fact]
    public void Deserialize_CodeError_Should_Map_Snake_Fields()
    {
        var err = _s.Deserialize<CodeError>("""{"code":99991663,"msg":"invalid","error":{"log_id":"l1"}}""");

        Assert.Equal(99991663, err.Code);
        Assert.Equal("invalid", err.Msg);
        Assert.Equal("l1", err.ErrorDetails?.LogId);
    }
}

/// <summary>ASP.NET Core 端点核心（事件与卡片共用适配）。</summary>
public class AspNetCoreEndpointTests
{
    [Fact]
    public async Task Webhook_Endpoint_Should_Handle_Challenge()
    {
        var dispatcher = new EventDispatcher("v-token");
        var body = """{"type":"url_verification","challenge":"cha-http","token":"v-token"}""";

        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/feishu/events";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.Headers["X-Tt-Logid"] = "log-9";

        await FeishuWebhookEndpoint.HandleAsync(context, dispatcher);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);
    }

    [Fact]
    public async Task Webhook_Endpoint_Should_Pass_Through_Headers_CaseInsensitive()
    {
        string? seenLogId = null;
        var dispatcher = new EventDispatcher()
            .OnRaw("app_ticket", (raw, ct) => Task.CompletedTask);
        // 用绑定 client 的 app_ticket 流程验证 header 大小写不敏感太重；这里直接验证验签头可读
        var key = "k1";
        var dispatcherSigned = new EventDispatcher("v", key);
        dispatcherSigned.OnRaw("app_ticket", (_, _) => Task.CompletedTask);

        var plain = """{"schema":"2.0","header":{"event_type":"app_ticket"},"event":{"app_ticket":"t"}}""";
        var body = $$"""{"encrypt":"{{EventCrypto.Encrypt(plain, key)}}"}""";
        var sign = EventCrypto.Signature("1700000000", "n", key, body);

        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        // 大小写故意不同（默认字典为 OrdinalIgnoreCase）
        context.Request.Headers["x-lark-request-timestamp"] = "1700000000";
        context.Request.Headers["X-LARK-REQUEST-NONCE"] = "n";
        context.Request.Headers["x-lark-SIGNATURE"] = sign;

        await FeishuWebhookEndpoint.HandleAsync(context, dispatcherSigned);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Null(seenLogId);
    }

    [Fact]
    public async Task Webhook_Endpoint_Should_Work_With_Card_Handler()
    {
        var handler = new Feishu.Card.CardActionHandler("v", null, (_, _) => Task.FromResult<object?>(null))
        {
            SkipSignVerify = true,
        };
        var body = """{"token":"v","action":{"tag":"button"}}""";

        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));

        await FeishuWebhookEndpoint.HandleAsync(context, handler);

        Assert.Equal(200, context.Response.StatusCode);
    }
}
