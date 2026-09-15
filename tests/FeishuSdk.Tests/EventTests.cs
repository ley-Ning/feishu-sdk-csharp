using System.Text;
using Feishu.Events;

namespace FeishuSdk.Tests;

public class EventCryptoTests
{
    [Fact]
    public void Signature_Should_Match_Reference_Vector()
    {
        // python3: sha256("1700000000" + "abcNonce" + "myEncryptKey" + '{"encrypt":"x"}').hexdigest()
        var sign = EventCrypto.Signature("1700000000", "abcNonce", "myEncryptKey", "{\"encrypt\":\"x\"}");
        Assert.Equal("bb056c1a92d13661bf689ea97c6b9b225e334d0aeb7b8f8456cfb83f8cbfc176", sign);
    }

    [Fact]
    public void Decrypt_Should_Roundtrip_With_Encrypt()
    {
        var key = "test-encrypt-key";
        var json = """{"schema":"2.0","header":{"event_type":"app_ticket"},"event":{"app_ticket":"tk-123"}}""";
        var cipher = EventCrypto.Encrypt(json, key);

        var plain = Encoding.UTF8.GetString(EventCrypto.Decrypt(cipher, key));

        Assert.Equal(json, plain);
    }
}

public class EventDispatcherTests
{
    [Fact]
    public async Task Challenge_Should_Echo_When_Token_Matches()
    {
        var dispatcher = new EventDispatcher(verificationToken: "v-token");
        var body = """{"type":"url_verification","challenge":"cha-abc","token":"v-token"}""";

        var resp = await dispatcher.HandleAsync(new EventRequest
        {
            Headers = new Dictionary<string, string>(),
            Body = Encoding.UTF8.GetBytes(body),
        });

        Assert.Equal(200, resp.StatusCode);
        Assert.Contains("cha-abc", Encoding.UTF8.GetString(resp.Body));
    }

    [Fact]
    public async Task Challenge_Should_Fail_When_Token_Mismatches()
    {
        var dispatcher = new EventDispatcher(verificationToken: "v-token");
        var body = """{"type":"url_verification","challenge":"cha","token":"wrong"}""";

        var resp = await dispatcher.HandleAsync(new EventRequest
        {
            Headers = new Dictionary<string, string>(),
            Body = Encoding.UTF8.GetBytes(body),
        });

        Assert.Equal(500, resp.StatusCode);
    }

    [Fact]
    public async Task Encrypted_Event_With_Signature_Should_Dispatch()
    {
        var encryptKey = "test-encrypt-key";
        var received = new List<AppTicketEvent>();
        var dispatcher = new EventDispatcher("v-token", encryptKey)
            .On<AppTicketEvent>("app_ticket", (e, _) => { received.Add(e); return Task.CompletedTask; });

        var plain = """{"schema":"2.0","header":{"event_type":"app_ticket","token":"v-token"},"event":{"app_ticket":"tk-999"}}""";
        var cipher = EventCrypto.Encrypt(plain, encryptKey);
        var body = $$"""{"encrypt":"{{cipher}}"}""";

        var timestamp = "1700000000";
        var nonce = "n1";
        var sign = EventCrypto.Signature(timestamp, nonce, encryptKey, body);

        var resp = await dispatcher.HandleAsync(new EventRequest
        {
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-Lark-Request-Timestamp"] = timestamp,
                ["X-Lark-Request-Nonce"] = nonce,
                ["X-Lark-Signature"] = sign,
            },
            Body = Encoding.UTF8.GetBytes(body),
        });

        Assert.Equal(200, resp.StatusCode);
        var evt = Assert.Single(received);
        Assert.Equal("tk-999", evt.AppTicket);
    }

    [Fact]
    public async Task Bad_Signature_Should_Be_Rejected()
    {
        var encryptKey = "test-encrypt-key";
        var dispatcher = new EventDispatcher("v-token", encryptKey)
            .On<AppTicketEvent>("app_ticket", (_, _) => Task.CompletedTask);

        var plain = """{"schema":"2.0","header":{"event_type":"app_ticket"},"event":{"app_ticket":"tk"}}""";
        var cipher = EventCrypto.Encrypt(plain, encryptKey);
        var body = $$"""{"encrypt":"{{cipher}}"}""";

        var resp = await dispatcher.HandleAsync(new EventRequest
        {
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-Lark-Request-Timestamp"] = "1700000000",
                ["X-Lark-Request-Nonce"] = "n1",
                ["X-Lark-Signature"] = "deadbeef",
            },
            Body = Encoding.UTF8.GetBytes(body),
        });

        Assert.Equal(500, resp.StatusCode);
    }

    [Fact]
    public async Task Handler_Exception_Should_Return_500_For_Retry()
    {
        var dispatcher = new EventDispatcher("v-token")
            .OnRaw("im.message.receive_v1", (_, _) => throw new InvalidOperationException("boom"));

        var body = """{"schema":"2.0","header":{"event_type":"im.message.receive_v1"},"event":{"a":1}}""";
        var resp = await dispatcher.HandleAsync(new EventRequest
        {
            Headers = new Dictionary<string, string>(),
            Body = Encoding.UTF8.GetBytes(body),
        });

        Assert.Equal(500, resp.StatusCode);
    }
}
