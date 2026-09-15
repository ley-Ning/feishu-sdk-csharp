using Feishu;
using System.Text;
using Feishu.Card;
using Feishu.Events;

namespace FeishuSdk.Tests;

/// <summary>卡片回调协议（对齐 Go card 包：SHA1 验签 / 解密 / challenge / 三种返回形态）。</summary>
public class CardActionHandlerTests
{
    private static (string Timestamp, string Nonce, string Body) PlainActionBody() =>
        ("1700000000", "cn", """{"token":"v-token-1","action":{"tag":"button","value":{"k":"v"}}}""");

    private static Dictionary<string, string> SignedHeaders(string timestamp, string nonce, string body, string sign) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Lark-Request-Timestamp"] = timestamp,
            ["X-Lark-Request-Nonce"] = nonce,
            ["X-Lark-Signature"] = sign,
        };

    [Fact]
    public void Signature_Should_Match_Reference_Vector()
    {
        // python3: sha1("1700000000" + "cn" + "v-token-1" + body).hexdigest()
        var (_, _, body) = PlainActionBody();
        var sign = CardSignature.Signature("1700000000", "cn", "v-token-1", body);
        Assert.Equal("874ec0e2f62b357916b86cf0c417a7e933c804b3", sign);
    }

    [Fact]
    public async Task Challenge_Should_Echo_When_Token_Matches()
    {
        var handler = new CardActionHandler("v-token-1", null, (_, _) => Task.FromResult<object?>(null));
        var resp = await handler.HandleAsync(new EventRequest
        {
            Headers = new Dictionary<string, string>(),
            Body = Encoding.UTF8.GetBytes("""{"type":"url_verification","challenge":"cha-1","token":"v-token-1"}"""),
        });

        Assert.Equal(200, resp.StatusCode);
        Assert.Contains("cha-1", Encoding.UTF8.GetString(resp.Body));
    }

    [Fact]
    public async Task Signed_Action_Should_Invoke_Handler_And_Return_Serialized_Result()
    {
        CardAction? received = null;
        var handler = new CardActionHandler("v-token-1", null, (action, _) =>
        {
            received = action;
            return Task.FromResult<object?>(new CardToast { Content = "done", Type = "success" });
        });

        var (ts, nonce, body) = PlainActionBody();
        var resp = await handler.HandleAsync(new EventRequest
        {
            Headers = SignedHeaders(ts, nonce, body, CardSignature.Signature(ts, nonce, "v-token-1", body)),
            Body = Encoding.UTF8.GetBytes(body),
            Path = "/card",
        });

        Assert.Equal(200, resp.StatusCode);
        Assert.Contains("done", Encoding.UTF8.GetString(resp.Body));
        Assert.NotNull(received);
        Assert.Equal("button", received!.Action?.Tag);
        Assert.Equal("v", received.Action?.Value?["k"]?.ToString());
    }

    [Fact]
    public async Task Bad_Signature_Should_Be_Rejected()
    {
        var handler = new CardActionHandler("v-token-1", null, (_, _) => Task.FromResult<object?>(null));
        var (_, _, body) = PlainActionBody();

        var resp = await handler.HandleAsync(new EventRequest
        {
            Headers = SignedHeaders("1700000000", "cn", body, "deadbeef"),
            Body = Encoding.UTF8.GetBytes(body),
        });

        Assert.Equal(500, resp.StatusCode);
    }

    [Fact]
    public async Task SkipSignVerify_Should_Bypass()
    {
        var handler = new CardActionHandler("v-token-1", null, (_, _) => Task.FromResult<object?>(null))
        {
            SkipSignVerify = true,
        };
        var (_, _, body) = PlainActionBody();

        var resp = await handler.HandleAsync(new EventRequest
        {
            Headers = new Dictionary<string, string>(),
            Body = Encoding.UTF8.GetBytes(body),
        });

        Assert.Equal(200, resp.StatusCode);
    }

    [Fact]
    public async Task Encrypted_Action_Should_Decrypt_With_EncryptKey()
    {
        var encryptKey = "card-key";
        CardAction? received = null;
        var handler = new CardActionHandler("v-token-1", encryptKey, (a, _) =>
        {
            received = a;
            return Task.FromResult<object?>(null);
        });

        var plain = """{"token":"v-token-1","action":{"tag":"button","value":{"x":1}}}""";
        // 卡片加密时 body 为 {"encrypt":"..."}，验签基于原始 body 字符串
        var body = $$"""{"encrypt":"{{EventCrypto.Encrypt(plain, encryptKey)}}"}""";
        var sign = CardSignature.Signature("1", "2", "v-token-1", body);

        var resp = await handler.HandleAsync(new EventRequest
        {
            Headers = SignedHeaders("1", "2", body, sign),
            Body = Encoding.UTF8.GetBytes(body),
        });

        Assert.Equal(200, resp.StatusCode);
        Assert.NotNull(received);
        Assert.Equal("button", received!.Action?.Tag);
    }

    [Fact]
    public async Task Handler_Returning_Null_Should_Reply_Success()
    {
        var handler = new CardActionHandler("v-token-1", null, (_, _) => Task.FromResult<object?>(null))
        {
            SkipSignVerify = true,
        };
        var (_, _, body) = PlainActionBody();

        var resp = await handler.HandleAsync(new EventRequest
        {
            Headers = new Dictionary<string, string>(),
            Body = Encoding.UTF8.GetBytes(body),
        });

        Assert.Equal(200, resp.StatusCode);
        Assert.Contains("success", Encoding.UTF8.GetString(resp.Body));
    }

    [Fact]
    public async Task Handler_Returning_CustomResponse_Should_Apply_Status()
    {
        var handler = new CardActionHandler("v-token-1", null,
            (_, _) => Task.FromResult<object?>(new CardCustomResponse
            {
                StatusCode = 429,
                Body = new { error = "busy" },
            }))
        {
            SkipSignVerify = true,
        };
        var (_, _, body) = PlainActionBody();

        var resp = await handler.HandleAsync(new EventRequest
        {
            Headers = new Dictionary<string, string>(),
            Body = Encoding.UTF8.GetBytes(body),
        });

        Assert.Equal(429, resp.StatusCode);
        Assert.Contains("busy", Encoding.UTF8.GetString(resp.Body));
    }

    [Fact]
    public async Task Handler_Throwing_Should_Reply_500()
    {
        var handler = new CardActionHandler("v-token-1", null, (_, _) => throw new InvalidOperationException("boom"))
        {
            SkipSignVerify = true,
        };
        var (_, _, body) = PlainActionBody();

        var resp = await handler.HandleAsync(new EventRequest
        {
            Headers = new Dictionary<string, string>(),
            Body = Encoding.UTF8.GetBytes(body),
        });

        Assert.Equal(500, resp.StatusCode);
    }
}
