using Feishu;
using Feishu.Services.Im;
using System.Text;

namespace FeishuSdk.Tests;

public class RequestPipelineTests
{
    [Fact]
    public async Task SendAsync_Should_Inject_TenantToken_And_Serialize_Body()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/tenant_access_token/internal"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t-abc"}"""));
            if (req.PathAndQuery.StartsWith("/open-apis/im/v1/messages"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"msg":"success","data":{"message_id":"om_1"}}"""));
            return Task.FromResult(FakeHandler.Json(404, """{"code":404,"msg":"not found"}"""));
        });

        var resp = await client.Im.Message.CreateAsync(new SendMessageRequest
        {
            ReceiveIdType = "open_id",
            Body = new SendMessageBody { ReceiveId = "ou_1", MsgType = "text", Content = "{\"text\":\"hi 中文\"}" },
        });

        Assert.True(resp.Success);
        Assert.Equal("om_1", resp.Data?.MessageId);

        var messageReq = handler.Requests.Single(r => r.PathAndQuery.StartsWith("/open-apis/im/v1/messages"));
        Assert.Contains("receive_id_type=open_id", messageReq.PathAndQuery);
        Assert.Equal("Bearer t-abc", messageReq.Headers["Authorization"]);
        Assert.Contains("hi 中文", messageReq.BodyText); // 中文不被转义成 \\uXXXX
        Assert.Contains("feishu-sdk-csharp", messageReq.Headers["User-Agent"]);
    }

    [Fact]
    public async Task SendAsync_Should_Invalidate_Token_And_Retry_On_TokenInvalid_Code()
    {
        var tokenFetchCount = 0;
        var messageCall = 0;
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/tenant_access_token/internal"))
            {
                tokenFetchCount++;
                var token = tokenFetchCount == 1 ? "t-stale" : "t-fresh";
                return Task.FromResult(FakeHandler.Json(200, $$"""{"code":0,"expire":7200,"tenant_access_token":"{{token}}"}"""));
            }
            if (req.PathAndQuery.StartsWith("/open-apis/im/v1/messages"))
            {
                messageCall++;
                var body = messageCall == 1
                    ? """{"code":99991663,"msg":"invalid tenant access token"}"""
                    : """{"code":0,"msg":"success","data":{"message_id":"om_2"}}""";
                return Task.FromResult(FakeHandler.Json(200, body));
            }
            return Task.FromResult(FakeHandler.Json(404, """{"code":404}"""));
        });

        var resp = await client.Im.Message.CreateAsync(new SendMessageRequest
        {
            ReceiveIdType = "chat_id",
            Body = new SendMessageBody { ReceiveId = "oc_1", MsgType = "text", Content = "{}" },
        });

        Assert.True(resp.Success);
        Assert.Equal(2, tokenFetchCount);                 // 失效后重新取 token
        Assert.Equal(2, messageCall);                     // 业务请求重试一次
        Assert.Equal("Bearer t-fresh", handler.Requests[^1].Headers["Authorization"]);
    }

    [Fact]
    public async Task SendAsync_Should_Passthrough_NonJson_Download()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(async req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/im/v1/messages/m_1/resources"))
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent("PNGDATA"u8.ToArray()),
                };
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}""");
            return FakeHandler.Json(404, "{}");
        });

        var resp = await client.GetAsync("/open-apis/im/v1/messages/m_1/resources", null, AccessTokenType.Tenant,
            new RequestOptions().WithFileDownload());

        Assert.Equal(200, resp.StatusCode);
        Assert.Equal("PNGDATA"u8.ToArray(), resp.RawBody);
    }

    [Fact]
    public void BuildUrl_Should_Escape_PathParams_And_Keep_Query()
    {
        var (client, _) = FeishuTestHarness.CreateClient(_ => Task.FromResult(FakeHandler.Json(200, "{}")));
        var pipeline = client.Pipeline;

        var url = pipeline.BuildUrl(new ApiRequest
        {
            Method = HttpMethod.Get,
            Path = "/open-apis/im/v1/messages/:message_id/chats/:chat_id",
            PathParams =
            {
                ["message_id"] = "om 1/2",
                ["chat_id"] = "oc_1",
            },
            QueryParams =
            {
                { "user_id_type", "open_id" },
                { "q", "a b&c" },
            },
        });

        Assert.Equal(
            "https://open.feishu.cn/open-apis/im/v1/messages/om%201%2F2/chats/oc_1?user_id_type=open_id&q=a%20b%26c",
            url);
    }

    [Fact]
    public async Task UserAccessToken_Should_Be_Used_Without_Token_Endpoint_Call()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
            Task.FromResult(FakeHandler.Json(200, """{"code":0,"data":{"items":[]}}""")));

        await client.Im.Chat.ListAsync(options: new RequestOptions().WithUserAccessToken("u-xyz"));

        var req = handler.Requests.Single();
        Assert.Equal("Bearer u-xyz", req.Headers["Authorization"]);
        Assert.DoesNotContain(handler.Requests, r => r.PathAndQuery.Contains("access_token"));
    }

    [Fact]
    public async Task ServerTimeout_504_Should_Throw_Immediately()
    {
        var (client, _) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.GatewayTimeout));
        });

        await Assert.ThrowsAsync<FeishuServerTimeoutException>(() =>
            client.PostAsync("/open-apis/im/v1/messages", new { }, AccessTokenType.Tenant));
    }
}
