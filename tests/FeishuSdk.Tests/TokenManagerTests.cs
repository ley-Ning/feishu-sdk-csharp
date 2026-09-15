using Feishu;
using System.Text;

namespace FeishuSdk.Tests;

public class TokenManagerTests
{
    [Fact]
    public async Task Concurrent_Fetch_Should_Be_SingleFlight()
    {
        var tokenEndpointHits = 0;
        var (client, handler) = FeishuTestHarness.CreateClient(async req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/tenant_access_token/internal"))
            {
                Interlocked.Increment(ref tokenEndpointHits);
                await Task.Delay(300); // 模拟慢网络，放大惊群窗口
                return FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t-single"}""");
            }
            return FakeHandler.Json(404, "{}");
        });

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => client.GetTenantAccessTokenAsync());
        var tokens = await Task.WhenAll(tasks);

        Assert.All(tokens, t => Assert.Equal("t-single", t));
        Assert.Equal(1, tokenEndpointHits); // 单飞：只打一次 token 端点
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Token_Should_Be_Cached_Until_Invalidated()
    {
        var hits = 0;
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/tenant_access_token/internal"))
            {
                hits++;
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t-cached"}"""));
            }
            return Task.FromResult(FakeHandler.Json(404, "{}"));
        });

        _ = await client.GetTenantAccessTokenAsync();
        _ = await client.GetTenantAccessTokenAsync();
        _ = await client.GetTenantAccessTokenAsync();

        Assert.Equal(1, hits);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Marketplace_TenantToken_Requires_AppTicket_And_TenantKey()
    {
        var (client, _) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/app_access_token"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"app_access_token":"t-app"}"""));
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/tenant_access_token"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t-market"}"""));
            return Task.FromResult(FakeHandler.Json(200, """{"code":0}"""));
        }, o => o.AppType = FeishuAppType.Marketplace);

        // 没有 app_ticket 缓存时应报错并触发重推
        await Assert.ThrowsAsync<FeishuException>(() => client.GetTenantAccessTokenAsync("tk-tenant"));

        // 写入 app_ticket 后走商店流程：app_access_token → tenant_access_token
        await client.Pipeline.AppTickets.SetAsync("ticket-1");
        var token = await client.GetTenantAccessTokenAsync("tk-tenant");

        Assert.Equal("t-market", token);
    }
}
