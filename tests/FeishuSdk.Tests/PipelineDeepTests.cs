using Feishu;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FeishuSdk.Tests;

/// <summary>传输层错误分类与校验分支（对齐 Go doSend/validate/validateTokenType）。</summary>
public class PipelineDeepTests
{
    // ---- 错误分类：拨号失败重试一轮，成功后返回 ----
    [Fact]
    public async Task DialFailure_Should_Retry_Once_Then_Succeed()
    {
        var messageCalls = 0;
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            messageCalls++;
            if (messageCalls == 1)
                throw new HttpRequestException("connection refused",
                    new SocketException((int)SocketError.ConnectionRefused));
            return Task.FromResult(FakeHandler.Json(200, """{"code":0,"msg":"ok"}"""));
        });

        var resp = await client.PostAsync("/open-apis/im/v1/messages", new { }, AccessTokenType.Tenant);

        Assert.Equal(200, resp.StatusCode);
        Assert.Equal(2, messageCalls);
    }

    // ---- 错误分类：拨号失败两轮都失败 → FeishuDialFailedException ----
    [Fact]
    public async Task DialFailure_Twice_Should_Throw_Typed()
    {
        var (client, _) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            throw new HttpRequestException("connection refused",
                new SocketException((int)SocketError.ConnectionRefused));
        });

        await Assert.ThrowsAsync<FeishuDialFailedException>(() =>
            client.PostAsync("/open-apis/im/v1/messages", new { }, AccessTokenType.Tenant));
    }

    // ---- 错误分类：超时立即抛出，不重试 ----
    [Fact]
    public async Task ClientTimeout_Should_Throw_Immediately_Without_Retry()
    {
        var calls = 0;
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            calls++;
            // HttpClient 超时表现为 OperationCanceledException（且业务取消未触发）
            throw new OperationCanceledException("timeout");
        });

        await Assert.ThrowsAsync<FeishuClientTimeoutException>(() =>
            client.PostAsync("/open-apis/im/v1/messages", new { }, AccessTokenType.Tenant));
        Assert.Equal(1, calls); // 不重试
    }

    // ---- validateTokenType：tenant-only API 拒绝 user token ----
    [Fact]
    public async Task TenantOnly_Api_Should_Reject_UserAccessToken()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(req => Task.FromResult(FakeHandler.Json(200, "{}")));

        var ex = await Assert.ThrowsAsync<FeishuException>(() =>
            client.PostAsync("/open-apis/x/v1/y", new { }, AccessTokenType.Tenant,
                new RequestOptions().WithUserAccessToken("u-1")));

        Assert.Contains("not match", ex.Message);
        Assert.Empty(handler.Requests);
    }

    // ---- validateTokenType：user-only API 拒绝 tenant token ----
    [Fact]
    public async Task UserOnly_Api_Should_Reject_TenantAccessToken()
    {
        var (client, _) = FeishuTestHarness.CreateClient(req => Task.FromResult(FakeHandler.Json(200, "{}")));

        var ex = await Assert.ThrowsAsync<FeishuException>(() =>
            client.PostAsync("/open-apis/x/v1/y", new { }, AccessTokenType.User,
                new RequestOptions().WithTenantAccessToken("t-1")));

        Assert.Contains("not match", ex.Message);
    }

    // ---- EnableTokenCache=false：无手动 token → 报错；有 → 直通 ----
    [Fact]
    public async Task TokenCacheDisabled_Requires_Manual_Token()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(
            req => Task.FromResult(FakeHandler.Json(200, """{"code":0,"data":{}}""")),
            o => o.EnableTokenCache = false);

        await Assert.ThrowsAsync<FeishuException>(() =>
            client.PostAsync("/open-apis/im/v1/messages", new { }, AccessTokenType.Tenant));

        var resp = await client.PostAsync("/open-apis/im/v1/messages", new { }, AccessTokenType.Tenant,
            new RequestOptions().WithTenantAccessToken("t-manual"));
        Assert.Equal(200, resp.StatusCode);
        Assert.Equal("Bearer t-manual", handler.Requests[^1].Headers["Authorization"]);
        Assert.DoesNotContain(handler.Requests, r => r.PathAndQuery.Contains("access_token/internal"));
    }

    // ---- AppSecret 与 ClientAssertion 双空 → 7104 ----
    [Fact]
    public async Task EmptySecret_And_NoAssertion_Should_Throw()
    {
        var (client, _) = FeishuTestHarness.CreateClient(
            req => Task.FromResult(FakeHandler.Json(200, "{}")),
            o => o.AppSecret = "");

        var ex = await Assert.ThrowsAsync<FeishuException>(() =>
            client.PostAsync("/open-apis/im/v1/messages", new { }, AccessTokenType.Tenant));
        Assert.Contains("AppSecret is empty", ex.Message);
    }

    // ---- 帮助台鉴权头 ----
    [Fact]
    public async Task HelpdeskAuth_Should_Add_Base64_Header()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(
            req => Task.FromResult(FakeHandler.Json(200, """{"code":0}""")),
            o =>
            {
                o.HelpdeskId = "hd1";
                o.HelpdeskToken = "hd-secret";
            });

        await client.PostAsync("/open-apis/helpdesk/v1/tickets", new { }, AccessTokenType.Tenant,
            new RequestOptions().WithTenantAccessToken("t").WithHelpdeskAuth());

        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("hd1:hd-secret"));
        Assert.Equal(expected, handler.Requests[^1].Headers["X-Lark-Helpdesk-Authorization"]);
    }

    [Fact]
    public async Task HelpdeskAuth_WithoutConfig_Should_Throw()
    {
        var (client, _) = FeishuTestHarness.CreateClient(req => Task.FromResult(FakeHandler.Json(200, "{}")));

        await Assert.ThrowsAsync<FeishuException>(() =>
            client.PostAsync("/open-apis/helpdesk/v1/tickets", new { }, AccessTokenType.Tenant,
                new RequestOptions().WithTenantAccessToken("t").WithHelpdeskAuth()));
    }

    // ---- 保留 header 拒绝 + 自定义RequestId ----
    [Fact]
    public async Task Reserved_Headers_Should_Be_Rejected()
    {
        var (client, _) = FeishuTestHarness.CreateClient(req => Task.FromResult(FakeHandler.Json(200, "{}")));

        await Assert.ThrowsAsync<FeishuException>(() =>
            client.PostAsync("/open-apis/im/v1/messages", new { }, AccessTokenType.Tenant,
                new RequestOptions().WithHeaders(new Dictionary<string, string> { ["X-Request-Id"] = "1" })));
    }

    [Fact]
    public async Task RequestId_Header_Should_Be_Sent()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(
            req => Task.FromResult(FakeHandler.Json(200, """{"code":0}""")),
            o => o.DefaultHeaders["X-Default-Header"] = "dv");

        await client.PostAsync("/open-apis/im/v1/messages", new { }, AccessTokenType.Tenant,
            new RequestOptions()
                .WithTenantAccessToken("t")
                .WithRequestId("req-42")
                .WithHeaders(new Dictionary<string, string> { ["X-Custom"] = "cv" }));

        var req = handler.Requests[^1];
        Assert.Equal("req-42", req.Headers["Oapi-Sdk-Request-Id"]);
        Assert.Equal("cv", req.Headers["X-Custom"]);
        Assert.Equal("dv", req.Headers["X-Default-Header"]);
    }

    // ---- Lark 国际版域名 ----
    [Fact]
    public async Task Lark_Domain_Should_Be_Used()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(
            req => Task.FromResult(FakeHandler.Json(200, """{"code":0}""")),
            o => o.UseLarkDomain = true);

        await client.PostAsync("/open-apis/im/v1/messages", new { }, AccessTokenType.Tenant,
            new RequestOptions().WithTenantAccessToken("t"));

        Assert.StartsWith("https://open.larksuite.com", handler.Requests[^1].Url);
    }

    // ---- multipart 上传内容 ----
    [Fact]
    public async Task Multipart_Body_Should_Contain_Fields_And_File()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(
            req => Task.FromResult(FakeHandler.Json(200, """{"code":0,"data":{"image_key":"img_1"}}""")));

        var multipart = new MultipartRequestBody()
            .AddField("image_type", "message")
            .AddFile("image", "cat.png", new MemoryStream("PNG"u8.ToArray()));

        var resp = await client.DoAsync(new ApiRequest
        {
            Method = HttpMethod.Post,
            Path = "/open-apis/im/v1/images",
            Body = multipart,
            SupportedTokenTypes = [AccessTokenType.Tenant],
        }, new RequestOptions().WithTenantAccessToken("t"));

        Assert.Equal(200, resp.StatusCode);
        var sent = handler.Requests[^1];
        Assert.NotNull(sent.Body);
        var bodyText = sent.BodyText;
        Assert.Contains("image_type", bodyText);
        Assert.Contains("message", bodyText);
        Assert.Contains("cat.png", bodyText);
        Assert.Contains("name=\"image\"", bodyText);
    }

    // ---- token TTL 提前 3 分钟缓冲 ----
    [Fact]
    public async Task Token_Cache_Ttl_Should_Deduct_ExpiryDelta()
    {
        var ttlSeen = TimeSpan.MinValue;
        var spyCache = new SpyCache
        {
            OnSet = (_, ttl) => ttlSeen = ttl,
        };
        var (client, _) = FeishuTestHarness.CreateClient(
            req => Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":400,"tenant_access_token":"t"}""")),
            o => o.TokenCache = spyCache);

        await client.GetTenantAccessTokenAsync();

        Assert.Equal(TimeSpan.FromSeconds(400 - 180), ttlSeen); // expire - 3min
    }

    // ---- app_ticket 失效码触发重推 ----
    [Fact]
    public async Task AppTicketInvalid_Code_Should_Trigger_Resend()
    {
        var resendCalls = 0;
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/app_ticket/resend"))
            {
                resendCalls++;
                return Task.FromResult(FakeHandler.Json(200, """{"code":0}"""));
            }
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            return Task.FromResult(FakeHandler.Json(200, """{"code":10012,"msg":"app ticket invalid"}"""));
        });

        var resp = await client.PostAsync("/open-apis/im/v1/messages", new { }, AccessTokenType.Tenant);

        Assert.Equal(10012, System.Text.Json.JsonSerializer.Deserialize<CodeError>(resp.RawBody)!.Code);
        Assert.Equal(1, resendCalls);
    }

    private sealed class SpyCache : IFeishuCache
    {
        public Action<string, TimeSpan>? OnSet;

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default)
        {
            OnSet?.Invoke(key, ttl);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
