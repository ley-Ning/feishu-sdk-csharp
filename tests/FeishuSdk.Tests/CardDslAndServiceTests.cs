using Feishu;
using System.Text;
using System.Text.Json;
using Feishu.Card;
using Feishu.Services.Authen;
using Feishu.Services.Contact;

namespace FeishuSdk.Tests;

/// <summary>卡片搭建 DSL：序列化输出与 Go 版 MessageCard 等价（深度 JSON 对比）。</summary>
public class CardDslTests
{
    [Fact]
    public void Full_Card_Should_Serialize_To_Go_Equivalent_Json()
    {
        var card = new MessageCard
        {
            Config = new CardConfig
            {
                WideScreenMode = true,
                EnableForward = true,
                UpdateMulti = true,
            },
            Header = new CardHeader
            {
                Template = CardTemplates.Blue,
                Title = new CardPlainText { Content = "标题标题" },
            },
            Elements =
            [
                new CardDiv
                {
                    Text = new CardLarkMd { Content = "**西湖**，位于浙江省杭州市" },
                    Fields =
                    [
                        new CardField { IsShort = true, Text = new CardPlainText { Content = "地区" } },
                        new CardField { IsShort = true, Text = new CardPlainText { Content = "杭州" } },
                    ],
                    Extra = new CardImage
                    {
                        ImgKey = "img_v2_123",
                        Alt = new CardPlainText { Content = "夜景" },
                        Mode = CardImageModes.CropCenter,
                        Preview = true,
                    },
                },
                new CardHr(),
                new CardMarkdown
                {
                    Content = "水位：**7.2 米**",
                    Href = new Dictionary<string, CardUrl>
                    {
                        ["查看详情"] = new CardUrl { Url = "https://example.com" },
                    },
                },
                new CardActionBlock
                {
                    Layout = CardActionLayouts.Bisected,
                    Actions =
                    [
                        new CardButton
                        {
                            Text = new CardPlainText { Content = "更多" },
                            Type = CardButtonTypes.Primary,
                            Url = "https://example.com/more",
                        },
                        new CardButton
                        {
                            Text = new CardPlainText { Content = "危险操作" },
                            Type = CardButtonTypes.Danger,
                            Value = new Dictionary<string, object?> { ["action"] = "delete" },
                            Confirm = new CardConfirm
                            {
                                Title = new CardPlainText { Content = "确认" },
                                Text = new CardLarkMd { Content = "确认执行？" },
                            },
                        },
                    ],
                },
                new CardNote
                {
                    Elements = [new CardPlainText { Content = "备注信息" }],
                },
            ],
            CardLink = new CardUrl
            {
                Url = "https://example.com/card",
                AndroidUrl = "https://example.com/android",
            },
        };

        var expected = """
        {
          "config": {"wide_screen_mode": true, "enable_forward": true, "update_multi": true},
          "header": {"template": "blue", "title": {"tag": "plain_text", "content": "标题标题"}},
          "elements": [
            {"tag": "div",
             "text": {"tag": "lark_md", "content": "**西湖**，位于浙江省杭州市"},
             "fields": [
               {"is_short": true, "text": {"tag": "plain_text", "content": "地区"}},
               {"is_short": true, "text": {"tag": "plain_text", "content": "杭州"}}
             ],
             "extra": {"tag": "img", "img_key": "img_v2_123", "alt": {"tag": "plain_text", "content": "夜景"}, "mode": "crop_center", "preview": true}},
            {"tag": "hr"},
            {"tag": "markdown", "content": "水位：**7.2 米**", "href": {"查看详情": {"url": "https://example.com"}}},
            {"tag": "action", "layout": "bisected", "actions": [
               {"tag": "button", "text": {"tag": "plain_text", "content": "更多"}, "type": "primary", "url": "https://example.com/more"},
               {"tag": "button", "text": {"tag": "plain_text", "content": "危险操作"}, "type": "danger",
                "value": {"action": "delete"},
                "confirm": {"title": {"tag": "plain_text", "content": "确认"}, "text": {"tag": "lark_md", "content": "确认执行？"}}}
            ]},
            {"tag": "note", "elements": [{"tag": "plain_text", "content": "备注信息"}]}
          ],
          "card_link": {"url": "https://example.com/card", "android_url": "https://example.com/android"}
        }
        """;

        var actualJson = JsonDocument.Parse(card.ToJsonString());
        var expectedJson = JsonDocument.Parse(expected);

        Assert.True(JsonNodeDeepEquals(expectedJson.RootElement, actualJson.RootElement),
            $"card json mismatch:\n{card.ToJsonString()}");
    }

    [Fact]
    public void Picker_Tags_Should_Follow_Kind()
    {
        var date = new CardPicker { Kind = CardPickerKinds.Date, InitialDate = "2026-01-01" };
        var time = new CardPicker { Kind = CardPickerKinds.Time, InitialTime = "12:00" };

        Assert.Contains("\"tag\":\"date_picker\"", SystemTextJsonFeishuSerializer.Instance.Serialize(date));
        Assert.Contains("\"tag\":\"picker_time\"", SystemTextJsonFeishuSerializer.Instance.Serialize(time));
    }

    [Fact]
    public void Select_Static_Should_Serialize_Tag()
    {
        var menu = new CardSelectStatic
        {
            Placeholder = new CardPlainText { Content = "请选择" },
            Options =
            [
                new CardSelectOption { Text = new CardPlainText { Content = "A" }, Value = "a" },
            ],
        };

        var json = SystemTextJsonFeishuSerializer.Instance.Serialize(menu);

        Assert.Contains("\"tag\":\"select_static\"", json);
        Assert.Contains("\"value\":\"a\"", json);
    }

    internal static bool JsonNodeDeepEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;
        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                if (a.EnumerateObject().Count() != b.EnumerateObject().Count()) return false;
                foreach (var prop in a.EnumerateObject())
                {
                    if (!b.TryGetProperty(prop.Name, out var other)) return false;
                    if (!JsonNodeDeepEquals(prop.Value, other)) return false;
                }
                return true;
            case JsonValueKind.Array:
                var x = a.EnumerateArray().ToArray();
                var y = b.EnumerateArray().ToArray();
                if (x.Length != y.Length) return false;
                for (var i = 0; i < x.Length; i++)
                    if (!JsonNodeDeepEquals(x[i], y[i])) return false;
                return true;
            default:
                return JsonValueEquals(a, b);
        }
    }

    private static bool JsonValueEquals(JsonElement a, JsonElement b) =>
        a.ValueKind switch
        {
            JsonValueKind.String => a.GetString() == b.GetString(),
            JsonValueKind.Number => a.GetRawText() == b.GetRawText(),
            JsonValueKind.True or JsonValueKind.False => a.GetBoolean() == b.GetBoolean(),
            JsonValueKind.Null => true,
            _ => true,
        };
}

/// <summary>contact v3 / authen v1 服务（路径、token 类型、请求体形态）。</summary>
public class ContactAndAuthenServiceTests
{
    [Fact]
    public async Task Contact_User_Get_Should_Build_Correct_Request()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            return Task.FromResult(FakeHandler.Json(200, """{"code":0,"data":{"name":"张三","open_id":"ou_1","department_ids":["od_1"]}}"""));
        });

        var resp = await client.Contact.User.GetAsync("ou_1", userIdType: "open_id");

        Assert.True(resp.Success);
        Assert.Equal("张三", resp.Data?.Name);
        var req = handler.Requests[^1];
        Assert.Equal("/open-apis/contact/v3/users/ou_1?user_id_type=open_id", req.PathAndQuery);
        Assert.Equal("Bearer t", req.Headers["Authorization"]);
    }

    [Fact]
    public async Task Contact_BatchGetId_Should_Post_Emails()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            return Task.FromResult(FakeHandler.Json(200, """{"code":0,"data":{"user_list":[{"user_id":"ou_9","email":"a@b.c"}]}}"""));
        });

        var resp = await client.Contact.User.BatchGetIdAsync(new BatchGetUserIdRequest
        {
            UserIdType = "open_id",
            Body = new BatchGetUserIdBody { Emails = ["a@b.c"] },
        });

        Assert.Equal("ou_9", resp.Data?.UserList?[0].UserId);
        var req = handler.Requests[^1];
        Assert.Equal("/open-apis/contact/v3/users/batch_get_id?user_id_type=open_id", req.PathAndQuery);
        Assert.Contains("a@b.c", req.BodyText);
    }

    [Fact]
    public async Task Contact_User_Patch_And_Delete_Should_Use_Correct_Verb_And_Path()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            return Task.FromResult(FakeHandler.Json(200, """{"code":0}"""));
        });

        await client.Contact.User.PatchAsync("ou_1", new PatchContactUserRequest { Body = new { name = "李四" } });
        await client.Contact.User.DeleteAsync("ou_1", userIdType: "open_id");

        Assert.Equal("PATCH", handler.Requests[^2].Method.Method);
        Assert.Equal("/open-apis/contact/v3/users/ou_1", handler.Requests[^2].PathAndQuery);
        Assert.Contains("李四", handler.Requests[^2].BodyText);
        Assert.Equal("DELETE", handler.Requests[^1].Method.Method);
        Assert.Equal("/open-apis/contact/v3/users/ou_1?user_id_type=open_id", handler.Requests[^1].PathAndQuery);
    }

    [Fact]
    public async Task Contact_Department_Children_Should_Auto_Page()
    {
        var page = 0;
        var (client, _) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            page++;
            var (items, token) = page switch
            {
                1 => ("""[{"department_id":"d1"},{"department_id":"d2"}]""", "p2"),
                _ => ("""[{"department_id":"d3"}]""", (string?)null),
            };
            var tokenJson = token == null ? "null" : $"\"{token}\"";
            return Task.FromResult(FakeHandler.Json(200,
                $$$"""{"code":0,"data":{"items":{{{items}}},"page_token":{{{tokenJson}}}}}"""));
        });

        var ids = new List<string>();
        await foreach (var dept in client.Contact.Department.EnumerateChildrenAsync("od_0"))
            ids.Add(dept.DepartmentId!);

        Assert.Equal(["d1", "d2", "d3"], ids);
        Assert.Equal(2, page); // 两次页面请求
    }

    [Fact]
    public async Task Authen_Oidc_AccessToken_Should_Use_AppToken()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/app_access_token"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"app_access_token":"t-app"}"""));
            return Task.FromResult(FakeHandler.Json(200, """{"code":0,"access_token":"u-at","refresh_token":"u-rt","expires_in":7100,"token_type":"Bearer"}"""));
        });

        var resp = await client.Authen.CreateOidcAccessTokenAsync("code-1");

        Assert.Equal("u-at", resp.AccessToken);
        var req = handler.Requests[^1];
        Assert.Equal("/open-apis/authen/v1/oidc/access_token", req.PathAndQuery);
        Assert.Equal("Bearer t-app", req.Headers["Authorization"]); // app token 类型
        Assert.Contains("\"grant_type\":\"authorization_code\"", req.BodyText);
        Assert.Contains("\"code\":\"code-1\"", req.BodyText);
    }

    [Fact]
    public async Task Authen_UserInfo_Should_Require_UserToken()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
            Task.FromResult(FakeHandler.Json(200, """{"code":0,"name":"王五","open_id":"ou_2"}""")));

        var resp = await client.Authen.GetUserInfoAsync(new RequestOptions().WithUserAccessToken("u-tok"));

        Assert.Equal("王五", resp.Name);
        var req = Assert.Single(handler.Requests);
        Assert.Equal("/open-apis/authen/v1/user_info", req.PathAndQuery);
        Assert.Equal("Bearer u-tok", req.Headers["Authorization"]);

        // 不带 user token 应被校验拒绝
        await Assert.ThrowsAsync<FeishuException>(() => client.Authen.GetUserInfoAsync(RequestOptions.Default));
    }

    [Fact]
    public async Task Authen_Legacy_And_Refresh_Endpoints_Should_Match_Go_Paths()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"app_access_token":"t-app"}"""));
            return Task.FromResult(FakeHandler.Json(200, """{"code":0,"access_token":"u","refresh_token":"r"}"""));
        });

        await client.Authen.CreateAccessTokenAsync("uac-1");
        await client.Authen.CreateOidcRefreshAccessTokenAsync("r-1");
        await client.Authen.CreateRefreshAccessTokenAsync("r-2");

        Assert.Equal("/open-apis/authen/v1/access_token", handler.Requests[^3].PathAndQuery);
        Assert.Contains("\"user_access_code\":\"uac-1\"", handler.Requests[^3].BodyText);
        Assert.Equal("/open-apis/authen/v1/oidc/refresh_access_token", handler.Requests[^2].PathAndQuery);
        Assert.Equal("/open-apis/authen/v1/refresh_access_token", handler.Requests[^1].PathAndQuery);
        Assert.Contains("\"grant_type\":\"refresh_token\"", handler.Requests[^1].BodyText);
    }
}
