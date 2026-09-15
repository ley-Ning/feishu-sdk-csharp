using Feishu;
using Feishu.Services.Im;
using System.Text;

namespace FeishuSdk.Tests;

public class PaginationTests
{
    [Fact]
    public async Task EnumerateChats_Should_Auto_Page_And_Respect_Limit()
    {
        var page = 0;
        var (client, _) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            page++;
            // 三页，每页 2 条；第三页不再给 token
            var (items, token) = page switch
            {
                1 => ("""[{"chat_id":"c1"},{"chat_id":"c2"}]""", "p2"),
                2 => ("""[{"chat_id":"c3"},{"chat_id":"c4"}]""", "p3"),
                _ => ("""[{"chat_id":"c5"}]""", (string?)null),
            };
            var tokenJson = token == null ? "null" : $"\"{token}\"";
            var hasMoreJson = token != null ? "true" : "false";
            var body = $$$"""{"code":0,"data":{"items":{{{items}}},"page_token":{{{tokenJson}}},"has_more":{{{hasMoreJson}}}}}""";
            return Task.FromResult(FakeHandler.Json(200, body));
        });

        var all = new List<string>();
        await foreach (var chat in client.Im.Chat.EnumerateAsync(new ListChatsRequest { PageSize = 2 }, new RequestOptions().WithUserAccessToken("u-1")))
            all.Add(chat.ChatId!);

        Assert.Equal(["c1", "c2", "c3", "c4", "c5"], all);
        Assert.Equal(3, page);

        // limit 生效（重置页计数后重新枚举）
        page = 0;
        var limited = new List<string>();
        await foreach (var chat in client.Im.Chat.EnumerateAsync(limit: 3, options: new RequestOptions().WithUserAccessToken("u-1")))
            limited.Add(chat.ChatId!);
        Assert.Equal(["c1", "c2", "c3"], limited);
    }

    [Fact]
    public async Task Enumerate_Should_Stop_When_PageToken_Empty()
    {
        var hits = 0;
        var (client, _) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            hits++;
            return Task.FromResult(FakeHandler.Json(200, """{"code":0,"data":{"items":[{"chat_id":"c1"}],"page_token":"","has_more":false}}"""));
        });

        var count = 0;
        await foreach (var _ in client.Im.Chat.EnumerateAsync(options: new RequestOptions().WithUserAccessToken("u-1")))
            count++;

        Assert.Equal(1, count);
        Assert.Equal(1, hits);
    }
}
