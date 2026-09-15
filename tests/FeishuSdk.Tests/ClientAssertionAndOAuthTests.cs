using Feishu;
using Feishu.Auth;
using System.Text;
using System.Text.Json;

namespace FeishuSdk.Tests;

/// <summary>ClientAssertion（JWT）模式 与 OAuth 用户令牌（对齐 Go ClientAssertionProvider / core/accesstoken）。</summary>
public class ClientAssertionAndOAuthTests
{
    private sealed class FixedAssertionProvider(string jwt, string? targetService = null, string? targetPrefix = null) : IClientAssertionProvider
    {
        public int Calls;

        public Task<ClientAssertionToken> RetrieveTokenAsync(string audience, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ClientAssertionToken
            {
                Value = jwt,
                TargetService = targetService,
                TargetPrefix = targetPrefix,
            });
        }
    }

    // ---- JWT 换 tenant token：请求形态 + 缓存 ----
    [Fact]
    public async Task TenantToken_ByClientAssertion_Should_Post_Jwt_Bearer_And_Cache()
    {
        var provider = new FixedAssertionProvider("jwt-abc");
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/oauth/v3/token"))
                return Task.FromResult(FakeHandler.Json(200, """{"access_token":"t-jwt","expires_in":7200,"token_type":"Bearer"}"""));
            return Task.FromResult(FakeHandler.Json(404, "{}"));
        }, o => o.ClientAssertionProvider = provider);

        var t1 = await client.GetTenantAccessTokenAsync("tk-1");
        var t2 = await client.GetTenantAccessTokenAsync("tk-1"); // 命中缓存

        Assert.Equal("t-jwt", t1);
        Assert.Equal(t1, t2);
        Assert.Equal(1, provider.Calls); // assertion 只取一次（缓存生效）

        var oauthReq = handler.Requests.Single(r => r.PathAndQuery.StartsWith("/oauth/v3/token"));
        var body = JsonDocument.Parse(oauthReq.BodyText).RootElement;
        Assert.Equal("urn:ietf:params:oauth:grant-type:jwt-bearer", body.GetProperty("grant_type").GetString());
        Assert.Equal("urn:ietf:params:oauth:client-assertion-type:jwt-bearer", body.GetProperty("client_assertion_type").GetString());
        Assert.Equal("jwt-abc", body.GetProperty("client_assertion").GetString());
        Assert.Equal("cli_test", body.GetProperty("client_id").GetString());
        Assert.Equal("https://open.feishu.cn/oauth/v3/token", oauthReq.Url);
    }

    // ---- JWT + 代理目标：URL 改写 + X-Target-Service 头 ----
    [Fact]
    public async Task ClientAssertion_WithTargetInfo_Should_Post_To_Proxy_With_Header()
    {
        var provider = new FixedAssertionProvider("jwt-xyz", targetService: "iam.internal", targetPrefix: "/proxy");
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/proxy/oauth/v3/token"))
                return Task.FromResult(FakeHandler.Json(200, """{"access_token":"t-proxy","expires_in":7200}"""));
            return Task.FromResult(FakeHandler.Json(404, "{}"));
        }, o => o.ClientAssertionProvider = provider);

        var token = await client.GetTenantAccessTokenAsync();

        Assert.Equal("t-proxy", token);
        var req = handler.Requests.Single();
        Assert.StartsWith("https://iam.internal/proxy/oauth/v3/token", req.Url);
        Assert.Equal("open.feishu.cn", req.Headers["X-Target-Service"]);
    }

    // ---- JWT + 商店应用 → 拒绝 ----
    [Fact]
    public async Task ClientAssertion_Marketplace_Should_Be_Rejected()
    {
        var (client, _) = FeishuTestHarness.CreateClient(
            req => Task.FromResult(FakeHandler.Json(200, "{}")),
            o =>
            {
                o.ClientAssertionProvider = new FixedAssertionProvider("j");
                o.AppType = FeishuAppType.Marketplace;
            });

        var ex = await Assert.ThrowsAsync<FeishuCodeException>(() => client.GetTenantAccessTokenAsync());
        Assert.Equal(FeishuErrorCodes.ClientAssertionProviderNotConfigured, ex.Code);
    }

    // ---- assertion 为空 → 7101 ----
    [Fact]
    public async Task ClientAssertion_Empty_Should_Throw_7101()
    {
        var (client, _) = FeishuTestHarness.CreateClient(
            req => Task.FromResult(FakeHandler.Json(200, "{}")),
            o => o.ClientAssertionProvider = new FixedAssertionProvider(""));

        var ex = await Assert.ThrowsAsync<FeishuCodeException>(() => client.GetTenantAccessTokenAsync());
        Assert.Equal(FeishuErrorCodes.ClientAssertionTokenEmpty, ex.Code);
    }

    // ---- OAuth：授权码换用户令牌 ----
    [Fact]
    public async Task OAuth_ExchangeByCode_Should_Send_Correct_Body_And_Parse()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/oauth/v3/token"))
                return Task.FromResult(FakeHandler.Json(200,
                    """{"access_token":"u-at","token_type":"Bearer","expires_in":7130,"refresh_token":"u-rt","refresh_token_expires_in":2592000,"scope":"contact"}"""));
            return Task.FromResult(FakeHandler.Json(404, "{}"));
        });

        var result = await client.OAuth.ExchangeByAuthorizationCodeAsync("code-1", redirectUri: "https://a/cb", codeVerifier: "ver");

        Assert.Equal("u-at", result.AccessToken);
        Assert.Equal(7130, result.ExpiresIn);
        Assert.Equal("u-rt", result.RefreshToken);

        var body = JsonDocument.Parse(handler.Requests.Single().BodyText).RootElement;
        Assert.Equal("authorization_code", body.GetProperty("grant_type").GetString());
        Assert.Equal("code-1", body.GetProperty("code").GetString());
        Assert.Equal("https://a/cb", body.GetProperty("redirect_uri").GetString());
        Assert.Equal("ver", body.GetProperty("code_verifier").GetString());
        Assert.Equal("cli_test", body.GetProperty("client_id").GetString());
        Assert.Equal("secret_test", body.GetProperty("client_secret").GetString());
    }

    // ---- OAuth：刷新令牌 ----
    [Fact]
    public async Task OAuth_Refresh_Should_Send_RefreshToken_Grant()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/oauth/v3/token"))
                return Task.FromResult(FakeHandler.Json(200, """{"access_token":"u-at2","expires_in":7100}"""));
            return Task.FromResult(FakeHandler.Json(404, "{}"));
        });

        var result = await client.OAuth.RefreshAsync("u-rt");

        Assert.Equal("u-at2", result.AccessToken);
        var body = JsonDocument.Parse(handler.Requests.Single().BodyText).RootElement;
        Assert.Equal("refresh_token", body.GetProperty("grant_type").GetString());
        Assert.Equal("u-rt", body.GetProperty("refresh_token").GetString());
    }

    // ---- OAuth：错误响应 → OAuthTokenException ----
    [Fact]
    public async Task OAuth_Error_Should_Throw_Typed_Exception()
    {
        var (client, _) = FeishuTestHarness.CreateClient(req =>
            Task.FromResult(FakeHandler.Json(400, """{"error":"invalid_grant","error_description":"code expired"}""")));

        var ex = await Assert.ThrowsAsync<OAuthTokenException>(() => client.OAuth.ExchangeByAuthorizationCodeAsync("bad"));

        Assert.Equal("invalid_grant", ex.ErrorType);
        Assert.Contains("code expired", ex.Message);
    }

    // ---- OAuth：双凭证皆空 → 7104 ----
    [Fact]
    public async Task OAuth_NoCredential_Should_Throw_7104()
    {
        var (client, _) = FeishuTestHarness.CreateClient(
            req => Task.FromResult(FakeHandler.Json(200, "{}")),
            o => o.AppSecret = "");

        var ex = await Assert.ThrowsAsync<FeishuCodeException>(() => client.OAuth.ExchangeByAuthorizationCodeAsync("c"));
        Assert.Equal(FeishuErrorCodes.AppSecretAndClientAssertionEmpty, ex.Code);
    }
}
