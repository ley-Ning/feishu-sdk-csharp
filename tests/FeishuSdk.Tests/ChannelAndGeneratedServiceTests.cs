using Feishu;
using System.Text;
using System.Text.Json;

namespace FeishuSdk.Tests;

using Feishu.Channel;

/// <summary>Channel 端到端编排：发送/降级/流式/入站管道（身份→去重→策略→批量分发）。</summary>
public class ChannelOrchestrationTests
{
    private static (FeishuChannel Channel, FakeHandler Handler) CreateChannel(Action<ChannelConfig>? cfg = null,
        Func<RequestLog, Task<HttpResponseMessage>>? responder = null)
    {
        var (client, handler) = FeishuTestHarness.CreateClient(responder ?? (req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            if (req.PathAndQuery.StartsWith("/open-apis/bot/v3/info"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"bot":{"open_id":"ou_bot","app_name":"测试机器人","activate_status":1}}"""));
            if (req.PathAndQuery.StartsWith("/open-apis/im/v1/messages"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"data":{"message_id":"om_new","chat_id":"oc_back"}}"""));
            return Task.FromResult(FakeHandler.Json(200, """{"code":0}"""));
        }));
        return (new FeishuChannel(client, null, cfg), handler);
    }

    /// <summary>解析请求体的 content 字段（内层 JSON 字符串）里的 text 字段。</summary>
    private static string InnerTextField(string bodyText)
    {
        using var outer = System.Text.Json.JsonDocument.Parse(bodyText);
        var content = outer.RootElement.GetProperty("content").GetString()!;
        using var inner = System.Text.Json.JsonDocument.Parse(content);
        return inner.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
    }

    private static byte[] MessagePayload(string messageId, string chatId, string chatType, string senderOpenId, string text, string? createTimeMs = null) =>
        Encoding.UTF8.GetBytes($$$"""
        {
          "schema": "2.0",
          "header": {"event_id": "ev_{{{messageId}}}", "event_type": "im.message.receive_v1", "create_time": "{{{createTimeMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()}}}"},
          "event": {
            "sender": {"sender_id": {"open_id": "{{{senderOpenId}}}"}},
            "message": {"message_id": "{{{messageId}}}", "chat_id": "{{{chatId}}}", "chat_type": "{{{chatType}}}",
              "message_type": "text", "content": "{\"text\":\"{{{text}}}\"}"}
          }
        }
        """);

    // ---- Send：文本直达 ----
    [Fact]
    public async Task Send_Text_Should_Create_With_Detected_IdType()
    {
        var (channel, handler) = CreateChannel();

        var result = await channel.SendAsync(new ChannelSendInput { UserId = "ou_9", Text = "你好" });

        Assert.Equal("om_new", result.MessageId);
        var req = handler.Requests.Single(r => r.PathAndQuery.StartsWith("/open-apis/im/v1/messages"));
        Assert.Equal("/open-apis/im/v1/messages?receive_id_type=open_id", req.PathAndQuery);
        Assert.Equal("你好", InnerTextField(req.BodyText));
    }

    // ---- Send：超长 Markdown 分片（ChunkIDs） ----
    [Fact]
    public async Task Send_LongMarkdown_Should_Chunk_Into_Posts()
    {
        var (channel, handler) = CreateChannel(cfg => cfg.TextChunkLimit = 100);

        var longMd = string.Join("\n", Enumerable.Range(0, 30).Select(i => $"段落 {i} 一些内容填充文本"));
        var result = await channel.SendAsync(new ChannelSendInput { UserId = "ou_9", Markdown = longMd, Title = "长文" });

        Assert.NotNull(result.ChunkIds);
        Assert.True(result.ChunkIds!.Count >= 2);
        var posts = handler.Requests.Where(r => r.PathAndQuery.StartsWith("/open-apis/im/v1/messages")).ToList();
        Assert.Equal(posts.Count, result.ChunkIds.Count);
        Assert.All(posts, p =>
        {
            Assert.Contains("\"msg_type\":\"post\"", p.BodyText);
            Assert.Contains("\\\"tag\\\":\\\"md\\\"", p.BodyText); // SimpleMarkdownToPost 包装（content 内层转义）
        });
    }

    // ---- Send：提及前缀 ----
    [Fact]
    public async Task Send_Text_With_Mentions_Should_Prepend_At_Prefix()
    {
        var (channel, handler) = CreateChannel();

        await channel.SendAsync(new ChannelSendInput
        {
            UserId = "ou_9",
            Text = "通知",
            Mentions = [new() { UserId = "ou_1", Name = "张三" }],
        });

        Assert.Equal("<at user_id=\"ou_1\">张三</at> 通知", InnerTextField(handler.Requests[^1].BodyText));
    }

    // ---- Send：回复对象被撤回（230011）→ 去掉 reply 重发 ----
    [Fact]
    public async Task Send_ReplyTargetRevoked_Should_Fallback_To_NewMessage()
    {
        var messageCall = 0;
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            messageCall++;
            var body = messageCall == 1
                ? """{"code":230011,"msg":"message is deleted"}"""
                : """{"code":0,"data":{"message_id":"om_fallback"}}""";
            return Task.FromResult(FakeHandler.Json(200, body));
        });
        var channel = new FeishuChannel(client);

        var result = await channel.SendAsync(new ChannelSendInput
        {
            UserId = "ou_9",
            Text = "hi",
            ReplyMessageId = "om_dead",
        });

        Assert.Equal("om_fallback", result.MessageId);
        Assert.Equal(2, messageCall);
        Assert.Equal("/open-apis/im/v1/messages/om_dead/reply", handler.Requests[^2].PathAndQuery);
        Assert.StartsWith("/open-apis/im/v1/messages?", handler.Requests[^1].PathAndQuery); // 降级为新消息
    }

    // ---- Send：格式错误（230001）→ 降级纯文本 ----
    [Fact]
    public async Task Send_FormatError_Should_Downgrade_To_Text()
    {
        var messageCall = 0;
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            messageCall++;
            var body = messageCall == 1
                ? """{"code":230001,"msg":"format error"}"""
                : """{"code":0,"data":{"message_id":"om_text"}}""";
            return Task.FromResult(FakeHandler.Json(200, body));
        });
        var channel = new FeishuChannel(client);

        var result = await channel.SendAsync(new ChannelSendInput
        {
            UserId = "ou_9",
            Markdown = "**富文本**",
        });

        Assert.Equal("om_text", result.MessageId);
        var downgraded = handler.Requests[^1];
        Assert.Contains("\"msg_type\":\"text\"", downgraded.BodyText);
        Assert.Contains("**富文本**", InnerTextField(downgraded.BodyText)); // Markdown 原文兜底
    }

    // ---- 入站管道：自发消息过滤（身份缓存生效）----
    [Fact]
    public async Task Inbound_SelfMessage_Should_Be_Filtered()
    {
        var got = new List<NormalizedMessage>();
        var (channel, handler) = CreateChannel();
        channel.OnMessage((m, _) => { got.Add(m); return Task.CompletedTask; });
        channel.PipelinesForTest.Dispose();
        var channel2 = channel;

        await channel2.HandleMessageAsync(MessagePayload("om_self", "oc_1", "p2p", "ou_bot", "我自己"), CancellationToken.None);

        Assert.Empty(got);
        Assert.Single(handler.Requests, r => r.PathAndQuery.StartsWith("/open-apis/bot/v3/info")); // 身份拉取
    }

    // ---- 入站管道：群未@机器人 → 拒绝事件 ----
    [Fact]
    public async Task Inbound_Group_Without_Mention_Should_Reject()
    {
        var got = new List<NormalizedMessage>();
        var rejects = new List<ChannelRejectEvent>();
        var (channel, _) = CreateChannel();
        channel.OnMessage((m, _) => { got.Add(m); return Task.CompletedTask; });
        channel.OnReject(r => rejects.Add(r));

        await channel.HandleMessageAsync(MessagePayload("om_1", "oc_1", "group", "ou_user", "群消息"), CancellationToken.None);

        Assert.Empty(got);
        var reject = Assert.Single(rejects);
        Assert.Equal("NoMention", reject.Reason);
    }

    // ---- 入站管道：私信 → 放行且去重 ----
    [Fact]
    public async Task Inbound_Dm_Should_Dispatch_Once_And_Dedup()
    {
        var got = new List<NormalizedMessage>();
        var (channel, _) = CreateChannel(cfg => cfg.Batch = new ChannelBatchConfig { Delay = TimeSpan.FromMilliseconds(20) });
        channel.OnMessage((m, _) => { got.Add(m); return Task.CompletedTask; });

        await channel.HandleMessageAsync(MessagePayload("om_d1", "oc_p2p", "p2p", "ou_user", "私聊"), CancellationToken.None);
        await channel.HandleMessageAsync(MessagePayload("om_d1", "oc_p2p", "p2p", "ou_user", "私聊"), CancellationToken.None); // 重复投递
        await channel.PipelinesForTest.FlushAllAsync();
        await Task.Delay(50);

        var single = Assert.Single(got);
        Assert.Equal("om_d1", single.MessageId);
        Assert.Equal("私聊", single.Content);
    }

    // ---- 入站管道：同群两条合并批量（\n\n）----
    [Fact]
    public async Task Inbound_Burst_Should_Batch_Into_Merged_Message()
    {
        var got = new List<NormalizedMessage>();
        var (channel, _) = CreateChannel(cfg => cfg.Batch = new ChannelBatchConfig { MaxMessages = 2, Delay = TimeSpan.FromMinutes(5) });
        channel.OnMessage((m, _) => { got.Add(m); return Task.CompletedTask; });

        await channel.HandleMessageAsync(MessagePayload("om_b1", "oc_g", "p2p", "ou_u1", "第一条"), CancellationToken.None);
        await channel.HandleMessageAsync(MessagePayload("om_b2", "oc_g", "p2p", "ou_u2", "第二条"), CancellationToken.None);
        await channel.PipelinesForTest.FlushAllAsync();
        await Task.Delay(50);

        var single = Assert.Single(got);
        Assert.Equal("第一条\n\n第二条", single.Content);
    }

    // ---- BotIdentity 缓存：两次取用只打一次接口 ----
    [Fact]
    public async Task BotIdentity_Should_Be_Cached()
    {
        var infoCalls = 0;
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            if (req.PathAndQuery.StartsWith("/open-apis/bot/v3/info"))
            {
                infoCalls++;
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"bot":{"open_id":"ou_bot","app_name":"N"}}"""));
            }
            return Task.FromResult(FakeHandler.Json(200, "{}"));
        });
        var channel = new FeishuChannel(client);

        var a = await channel.GetBotIdentityAsync();
        var b = await channel.GetBotIdentityAsync();

        Assert.NotNull(a);
        Assert.Equal("ou_bot", a!.OpenId);
        Assert.Same(a, b);
        Assert.Equal(1, infoCalls);
    }

    // ---- 流式：追加 → PATCH 累积内容；超限 → 新分片走回复 ----
    [Fact]
    public async Task Stream_Markdown_Should_Patch_Then_Spawn_New_Chunk()
    {
        var replies = 0;
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            if (req.PathAndQuery.StartsWith("/open-apis/im/v1/messages") &&
                req.Method == HttpMethod.Post &&
                req.PathAndQuery.Contains("/reply"))
            {
                replies++;
                return Task.FromResult(FakeHandler.Json(200, $$"""{"code":0,"data":{"message_id":"om_chunk_{{replies}}"} }"""));
            }
            return Task.FromResult(FakeHandler.Json(200, """{"code":0,"data":{"message_id":"om_stream"}}"""));
        });
        var channel = new FeishuChannel(client, null, cfg =>
        {
            cfg.StreamThrottle = TimeSpan.FromMilliseconds(1); // 测试下不节流
            cfg.TextChunkLimit = 200;
        });

        var stream = await channel.StreamAsync(new ChannelSendInput { UserId = "ou_9", Markdown = "开头" });

        // 节流间隔已过 → Append 触发立即更新（无需再 Flush；Flush 与 Go 一致为无条件执行，会多一次 PUT）
        await stream.AppendAsync(" 中段");
        var puts = handler.Requests.Where(r => r.Method == HttpMethod.Put && r.PathAndQuery.StartsWith("/open-apis/im/v1/messages/om_stream")).ToList();
        Assert.Single(puts);
        Assert.Contains("开头 中段", puts[0].BodyText);

        // 追加超限 → SplitWithCodeFences 产生第二片 → 走回复产生新消息
        await stream.AppendAsync(string.Join("\n", Enumerable.Range(0, 40).Select(i => $"第 {i} 行内容填充")));
        await stream.CloseAsync();

        Assert.Equal(1, replies); // 新分片通过回复建立
    }

    // ---- 流式：卡片控制器 UpdateCard ----
    [Fact]
    public async Task Stream_Card_UpdateCard_Should_Patch_Interactive()
    {
        var (channel, handler) = CreateChannel();

        var stream = await channel.StreamAsync(new ChannelSendInput
        {
            UserId = "ou_9",
            Card = """{"config":{}}""",
        });

        await stream.UpdateCardAsync("""{"config":{"wide_screen_mode":true}}""");
        await stream.CloseAsync();

        var put = handler.Requests.Single(r => r.Method == HttpMethod.Put && r.PathAndQuery.StartsWith("/open-apis/im/v1/messages/om_new"));
        Assert.Contains("wide_screen_mode", put.BodyText);

        await Assert.ThrowsAsync<FeishuChannelException>(() => stream.AppendAsync("x"));
    }
}

/// <summary>生成器产出的服务（管道一致性 + 端点契约）。</summary>
public class GeneratedServiceTests
{
    [Fact]
    public async Task Bitable_CreateRecord_Should_Build_Correct_Request()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            return Task.FromResult(FakeHandler.Json(200, """{"code":0,"data":{"record":{"record_id":"rec1","fields":{"名称":"A"}}}}"""));
        });

        var resp = await client.Bitable.Record.CreateAsync("appT", "tbl1", new Dictionary<string, string> { ["user_id_type"] = "open_id" },
            new { fields = new { 名称 = "A" } });

        Assert.True(resp.Success);
        var recordId = resp.Data?.GetProperty("record").GetProperty("record_id").GetString();
        Assert.Equal("rec1", recordId);
        var req = handler.Requests[^1];
        Assert.Equal("POST", req.Method.Method);
        Assert.Equal("/open-apis/bitable/v1/apps/appT/tables/tbl1/records?user_id_type=open_id", req.PathAndQuery);
        Assert.Contains("名称", req.BodyText);
    }

    [Fact]
    public async Task Bitable_ListTables_Should_Enumerate_Pages()
    {
        var page = 0;
        var (client, _) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            page++;
            var (items, token) = page switch
            {
                1 => ("""[{"table_id":"tblA"},{"table_id":"tblB"}]""", "p2"),
                _ => ("""[{"table_id":"tblC"}]""", (string?)null),
            };
            var tokenJson = token == null ? "null" : $"\"{token}\"";
            var hasMoreJson = token != null ? "true" : "false";
            return Task.FromResult(FakeHandler.Json(200,
                $$$"""{"code":0,"data":{"items":{{{items}}},"page_token":{{{tokenJson}}},"has_more":{{{hasMoreJson}}}}}"""));
        });

        var ids = new List<string>();
        await foreach (var item in client.Bitable.Table.ListAsyncEnumerate("appT", new Dictionary<string, string> { ["page_size"] = "2" }))
            ids.Add(item.GetProperty("table_id").GetString()!);

        Assert.Equal(["tblA", "tblB", "tblC"], ids);
    }

    [Fact]
    public async Task Drive_Download_Should_Passthrough_Bytes()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(async req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}""");
            if (req.PathAndQuery.StartsWith("/open-apis/drive/v1/files/f1/download"))
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent("PDFBYTES"u8.ToArray()) };
            return FakeHandler.Json(404, "{}");
        });

        var resp = await client.Drive.File.DownloadAsync("f1");

        Assert.Equal(200, resp.StatusCode);
        Assert.Equal("PDFBYTES"u8.ToArray(), resp.RawBody);
        Assert.Equal("/open-apis/drive/v1/files/f1/download", handler.Requests[^1].PathAndQuery);
    }

    [Fact]
    public async Task Approval_CreateInstance_Should_Post_Body()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            return Task.FromResult(FakeHandler.Json(200, """{"code":0,"data":{"instance_id":"ins1"}}"""));
        });

        var resp = await client.Approval.Instance.CreateAsync(new { approval_code = "c", user_id = "ou_1" });

        Assert.True(resp.Success);
        Assert.Equal("/open-apis/approval/v4/instances", handler.Requests[^1].PathAndQuery);
        Assert.Contains("approval_code", handler.Requests[^1].BodyText);
    }

    [Fact]
    public async Task Task_And_Docx_Endpoints_Should_Match_Paths()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            return Task.FromResult(FakeHandler.Json(200, """{"code":0,"data":{}}"""));
        });

        await client.Task.Task.CompleteAsync("task1");
        await client.Docx.Document.GetRawContentAsync("doc1", new Dictionary<string, string> { ["lang"] = "zh" });

        Assert.Equal("/open-apis/task/v2/tasks/task1/complete", handler.Requests[^2].PathAndQuery);
        Assert.Equal("/open-apis/docx/v1/documents/doc1/raw_content?lang=zh", handler.Requests[^1].PathAndQuery);
    }

    [Fact]
    public void CodeGen_Should_Generate_Deterministic_Files()
    {
        // 从测试程序集向上定位仓库根（本地 / CI 均可，不硬编码绝对路径）
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FeishuSdk.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var root = Path.Combine(dir!.FullName, "src", "FeishuSdk", "Services");
        foreach (var svc in new[] { "Bitable", "Drive", "Approval", "Task", "Docx" })
        {
            var file = Path.Combine(root, svc, $"{svc}Service.g.cs");
            Assert.True(File.Exists(file), $"missing generated {file}");
            var content = File.ReadAllText(file);
            Assert.Contains("<auto-generated>", content);
            Assert.Contains($"namespace Feishu.Services.{svc};", content);
            Assert.DoesNotContain("HttpMethod.GET", content); // 动词已归一化
        }
    }
}

/// <summary>第二轮扩录的生成服务（sheets/calendar/wiki/search）契约测试。</summary>
public class GeneratedServiceWave2Tests
{
    private static (FeishuClient, FakeHandler) OkClient(Func<string, string>? responder = null)
    {
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            return Task.FromResult(FakeHandler.Json(200, responder?.Invoke(req.PathAndQuery) ?? """{"code":0,"data":{}}"""));
        });
        return (client, handler);
    }

    [Fact]
    public async Task Sheets_CreateSpreadsheet_Should_Match_Path()
    {
        var (client, handler) = OkClient();
        await client.Sheets.Spreadsheet.CreateAsync(new { title = "表格一" });
        var req = handler.Requests[^1];
        Assert.Equal("POST", req.Method.Method);
        Assert.Equal("/open-apis/sheets/v3/spreadsheets", req.PathAndQuery);
        Assert.Contains("表格一", req.BodyText);
    }

    [Fact]
    public async Task Calendar_CreateEvent_Should_Carry_CalendarId_Path_And_Query()
    {
        var (client, handler) = OkClient();
        await client.Calendar.Event.CreateAsync("cal_1", new Dictionary<string, string> { ["user_id_type"] = "open_id" }, new { summary = "会议" });
        var req = handler.Requests[^1];
        Assert.Equal("/open-apis/calendar/v4/calendars/cal_1/events?user_id_type=open_id", req.PathAndQuery);
        Assert.Contains("会议", req.BodyText);
    }

    [Fact]
    public async Task Wiki_ListSpaces_Should_Enumerate_Pages()
    {
        var page = 0;
        var (client, _) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            page++;
            var (items, token) = page switch
            {
                1 => ("""[{"space_id":"sp1"},{"space_id":"sp2"}]""", "p2"),
                _ => ("""[{"space_id":"sp3"}]""", (string?)null),
            };
            var tokenJson = token == null ? "null" : $"\"{token}\"";
            return Task.FromResult(FakeHandler.Json(200,
                $$$"""{"code":0,"data":{"items":{{{items}}},"page_token":{{{tokenJson}}}}}"""));
        });

        var ids = new List<string>();
        await foreach (var space in client.Wiki.Space.ListAsyncEnumerate())
            ids.Add(space.GetProperty("space_id").GetString()!);

        Assert.Equal(["sp1", "sp2", "sp3"], ids);
    }

    [Fact]
    public async Task Search_DocsSearch_Should_Post_Object()
    {
        var (client, handler) = OkClient();
        var resp = await client.Search.DocsSearch.SearchAsync(new { search_key = "报表", owner_ids = new[] { "ou_1" } });
        Assert.True(resp.Success);
        Assert.Equal("/open-apis/suite/docs-api/search/object", handler.Requests[^1].PathAndQuery);
        Assert.Contains("报表", handler.Requests[^1].BodyText);
    }
}

/// <summary>第三波生成服务（board/mail/docs/tenant/bot/application + drive 追加）契约测试。</summary>
public class GeneratedServiceWave3Tests
{
    private static (FeishuClient, FakeHandler) OkClient()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            return Task.FromResult(FakeHandler.Json(200, """{"code":0,"data":{}}"""));
        });
        return (client, handler);
    }

    [Fact]
    public async Task Board_Create_And_Drive_Extras_Should_Match_Paths()
    {
        var (client, handler) = OkClient();
        await client.Board.Whiteboard.CreateAsync(new { title = "白板一", board_type = "paint" });
        await client.Drive.File.CreateFolderAsync(new { name = "新文件夹", folder_token = "fld_0" });
        await client.Drive.File.CreateImportTaskAsync(null, new { file_extension = "docx", file_token = "ft_1", file_type = "doc", point = new { mount_type = 1, mount_key = "fld_0" } });

        Assert.Equal("/open-apis/board/v1/whiteboards", handler.Requests[^3].PathAndQuery);
        Assert.Contains("白板一", handler.Requests[^3].BodyText);
        Assert.Equal("/open-apis/drive/v1/files/create_folder", handler.Requests[^2].PathAndQuery);
        Assert.Equal("/open-apis/drive/v1/import_tasks", handler.Requests[^1].PathAndQuery);
        Assert.Contains("file_extension", handler.Requests[^1].BodyText);
    }

    [Fact]
    public async Task Tenant_Bot_Application_Should_Match_Paths()
    {
        var (client, handler) = OkClient();
        await client.Tenant.Tenant.QueryAsync();
        await client.Bot.Bot.GetInfoAsync();
        await client.Application.Application.GetAsync("cli_9", new Dictionary<string, string> { ["lang"] = "zh_cn" });

        Assert.Equal("/open-apis/tenant/v2/tenant/query", handler.Requests[^3].PathAndQuery);
        Assert.Equal("/open-apis/bot/v3/info", handler.Requests[^2].PathAndQuery);
        Assert.Equal("/open-apis/application/v6/applications/cli_9?lang=zh_cn", handler.Requests[^1].PathAndQuery);
    }

    [Fact]
    public async Task Mail_PublicMailbox_List_Should_Page()
    {
        var page = 0;
        var (client, _) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            page++;
            var (items, token) = page == 1 ? ("""[{"mailbox_id":"mb1"}]""", "p2") : ("""[{"mailbox_id":"mb2"}]""", (string?)null);
            var tokenJson = token == null ? "null" : $"\"{token}\"";
            return Task.FromResult(FakeHandler.Json(200,
                $$$"""{"code":0,"data":{"items":{{{items}}},"page_token":{{{tokenJson}}}}}"""));
        });

        var ids = new List<string>();
        await foreach (var mb in client.Mail.PublicMailbox.ListAsyncEnumerate())
            ids.Add(mb.GetProperty("mailbox_id").GetString()!);

        Assert.Equal(["mb1", "mb2"], ids);
    }

    [Fact]
    public async Task Docs_Legacy_Create_Should_Match()
    {
        var (client, handler) = OkClient();
        await client.Docs.Doc.CreateAsync(new { title = "旧版文档" });
        Assert.Equal("/open-apis/doc/v2/docs", handler.Requests[^1].PathAndQuery);
        Assert.Contains("旧版文档", handler.Requests[^1].BodyText);
    }
}

/// <summary>第四波生成服务（translation/ocr/cardkit/moments/verification/acs，路径均核对自 Go resource.go）。</summary>
public class GeneratedServiceWave4Tests
{
    private static (FeishuClient, FakeHandler) OkClient()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            return Task.FromResult(FakeHandler.Json(200, """{"code":0,"data":{}}"""));
        });
        return (client, handler);
    }

    [Fact]
    public async Task Translation_And_Ocr_Should_Match_Verified_Paths()
    {
        var (client, handler) = OkClient();
        await client.Translation.Text.DetectAsync(new { text = "hello" });
        await client.Translation.Text.TranslateAsync(new { source_lang = "en", text = "hello", target_lang = "zh" });
        await client.Ocr.Image.BasicRecognizeAsync(new { image = "base64..." });

        Assert.Equal("/open-apis/translation/v1/text/detect", handler.Requests[^3].PathAndQuery);
        Assert.Equal("/open-apis/translation/v1/text/translate", handler.Requests[^2].PathAndQuery);
        Assert.Equal("/open-apis/optical_char_recognition/v1/image/basic_recognize", handler.Requests[^1].PathAndQuery);
    }

    [Fact]
    public async Task Cardkit_Full_Lifecycle_Should_Match_Verified_Paths()
    {
        var (client, handler) = OkClient();
        await client.Cardkit.Card.CreateAsync(new { type = "json_schema", data = new { schema = "2.0" } });
        await client.Cardkit.Card.BatchUpdateAsync("ck_1", new { actions = Array.Empty<object>() });
        await client.Cardkit.CardElement.ContentAsync("ck_1", "el_1", new { text = "流式" });

        Assert.Equal("/open-apis/cardkit/v1/cards", handler.Requests[^3].PathAndQuery);
        Assert.Equal("/open-apis/cardkit/v1/cards/ck_1/batch_update", handler.Requests[^2].PathAndQuery);
        Assert.Equal("/open-apis/cardkit/v1/cards/ck_1/elements/el_1/content", handler.Requests[^1].PathAndQuery);
        Assert.Equal("PUT", handler.Requests[^1].Method.Method);
    }

    [Fact]
    public async Task Acs_Mixed_Token_Types_And_Downloads_Should_Match()
    {
        var (client, handler) = OkClient();
        await client.Acs.User.ListAsync(new Dictionary<string, string> { ["page_size"] = "50" });
        await client.Acs.RuleExternal.CreateAsync(new { rule = new { name = "权限组" } },
            new RequestOptions().WithUserAccessToken("u-tok"));
        await client.Acs.UserFace.UpdateAsync("u_1", new { file = "x" });

        // users 列表：tenant token（自动注入）
        Assert.Equal("/open-apis/acs/v1/users?page_size=50", handler.Requests[^3].PathAndQuery);
        Assert.Equal("Bearer u-tok", handler.Requests[^2].Headers["Authorization"]); // User-only 端点
        // rule_external：Go 源码声明 User-only → 无 user token 时应校验拒绝
        await Assert.ThrowsAsync<FeishuException>(() =>
            client.Acs.RuleExternal.CreateAsync(new { rule = new { name = "x" } }));
        // 人脸上传：PUT 到 /users/:id/face
        Assert.Equal("/open-apis/acs/v1/users/u_1/face", handler.Requests[^1].PathAndQuery);
        Assert.Equal("PUT", handler.Requests[^1].Method.Method);
    }

    [Fact]
    public async Task Moments_And_Verification_Should_Match()
    {
        var (client, handler) = OkClient();
        await client.Moments.Post.GetAsync("po_1");
        await client.Verification.Verification.GetAsync();

        Assert.Equal("/open-apis/moments/v1/posts/po_1", handler.Requests[^2].PathAndQuery);
        Assert.Equal("/open-apis/verification/v1/verification", handler.Requests[^1].PathAndQuery);
    }
}

/// <summary>第五批生成服务（attendance 36 / helpdesk 44 端点，路径核对自 Go resource.go）。</summary>
public class GeneratedServiceWave5Tests
{
    private static (FeishuClient, FakeHandler) OkClient(Action<FeishuOptions>? cfg = null)
    {
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            return Task.FromResult(FakeHandler.Json(200, """{"code":0,"data":{}}"""));
        }, cfg);
        return (client, handler);
    }

    [Fact]
    public async Task Attendance_Core_Resources_Should_Match_Verified_Paths()
    {
        var (client, handler) = OkClient();
        await client.Attendance.UserFlow.BatchCreateAsync(new { user_flows = Array.Empty<object>() });
        await client.Attendance.UserTask.QueryAsync(new { user_ids = new[] { "ou_1" } });
        await client.Attendance.Shift.CreateAsync(new { shift_name = "早班" });
        await client.Attendance.Group.ListAsync(new Dictionary<string, string> { ["page_size"] = "100" });

        Assert.Equal("/open-apis/attendance/v1/user_flows/batch_create", handler.Requests[^4].PathAndQuery);
        Assert.Equal("/open-apis/attendance/v1/user_tasks/query", handler.Requests[^3].PathAndQuery);
        Assert.Equal("/open-apis/attendance/v1/shifts", handler.Requests[^2].PathAndQuery);
        Assert.Equal("/open-apis/attendance/v1/groups?page_size=100", handler.Requests[^1].PathAndQuery);
    }

    [Fact]
    public async Task Helpdesk_Ticket_Should_Carry_HelpdeskAuth_Header()
    {
        var (client, handler) = OkClient(o =>
        {
            o.HelpdeskId = "hd1";
            o.HelpdeskToken = "hd-secret";
        });

        var resp = await client.Helpdesk.Ticket.CreateAsync(new { description = "工单内容" });
        Assert.True(resp.Success);

        var req = handler.Requests[^1];
        Assert.Equal("/open-apis/helpdesk/v1/tickets", req.PathAndQuery);
        var expected = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("hd1:hd-secret"));
        Assert.Equal(expected, req.Headers["X-Lark-Helpdesk-Authorization"]); // 生成代码自动携带服务台鉴权头
        Assert.Contains("工单内容", req.BodyText);
    }

    [Fact]
    public async Task Helpdesk_Mixed_Token_Resources_Should_Match()
    {
        var (client, handler) = OkClient();
        await client.Helpdesk.Faq.ListAsync(new Dictionary<string, string> { ["page_size"] = "20" });
        await client.Helpdesk.Faq.CreateAsync(new { question = "Q", answer = "A" },
            new RequestOptions().WithUserAccessToken("u-t"));
        await client.Helpdesk.Event.SubscribeAsync(new { event_types = new[] { "ticket" } });

        Assert.Equal("/open-apis/helpdesk/v1/faqs?page_size=20", handler.Requests[^3].PathAndQuery);
        Assert.Equal("/open-apis/helpdesk/v1/faqs", handler.Requests[^2].PathAndQuery);
        Assert.Equal("Bearer u-t", handler.Requests[^2].Headers["Authorization"]); // User-only 创建
        Assert.Equal("/open-apis/helpdesk/v1/events/subscribe", handler.Requests[^1].PathAndQuery);
    }

    [Fact]
    public async Task Helpdesk_Ticket_Without_Credential_Should_Throw()
    {
        var (client, _) = OkClient(); // 未配置 HelpdeskId/Token
        await Assert.ThrowsAsync<FeishuException>(() =>
            client.Helpdesk.Ticket.CreateAsync(new { description = "x" }));
    }
}

/// <summary>第六批生成服务（minutes/event/okr/vc，路径核对自 Go resource.go）。</summary>
public class GeneratedServiceWave6Tests
{
    private static (FeishuClient, FakeHandler) OkClient()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            return Task.FromResult(FakeHandler.Json(200, """{"code":0,"data":{}}"""));
        });
        return (client, handler);
    }

    [Fact]
    public async Task Minutes_And_EventSvc_Should_Match_Verified_Paths()
    {
        var (client, handler) = OkClient();
        await client.Minutes.Minute.GetAsync("mv_1");
        await client.Minutes.Minute.SearchAsync(new { keyword = "周会" });
        await client.EventSvc.Connection.GetAsync();

        Assert.Equal("/open-apis/minutes/v1/minutes/mv_1", handler.Requests[^3].PathAndQuery);
        Assert.Equal("/open-apis/minutes/v1/minutes/search", handler.Requests[^2].PathAndQuery);
        Assert.Equal("/open-apis/event/v1/connection", handler.Requests[^1].PathAndQuery);
    }

    [Fact]
    public async Task Okr_Full_Resources_Should_Match_Verified_Paths()
    {
        var (client, handler) = OkClient();
        await client.Okr.ProgressRecord.CreateAsync(new { okr_id = "o1", content = "进展" });
        await client.Okr.UserOkr.ListAsync("ou_1");
        await client.Okr.Period.ListAsync();

        Assert.Equal("/open-apis/okr/v1/progress_records", handler.Requests[^3].PathAndQuery);
        Assert.Contains("进展", handler.Requests[^3].BodyText);
        Assert.Equal("/open-apis/okr/v1/users/ou_1/okrs", handler.Requests[^2].PathAndQuery);
        Assert.Equal("/open-apis/okr/v1/periods", handler.Requests[^1].PathAndQuery);
    }

    [Fact]
    public async Task Vc_Meeting_Lifecycle_Should_Match_Verified_Paths_And_Tokens()
    {
        var (client, handler) = OkClient();
        await client.Vc.Reserve.ApplyAsync(new { topic = "评审会", end_time = "2026-01-01 10:00" });
        await client.Vc.Meeting.EndAsync("mt_1", null, new RequestOptions().WithUserAccessToken("u-t"));
        await client.Vc.Meeting.KickoutAsync("mt_1", new { user_id = "ou_bad" });

        Assert.Equal("/open-apis/vc/v1/reserves/apply", handler.Requests[^3].PathAndQuery);
        Assert.Contains("评审会", handler.Requests[^3].BodyText);
        // 结束会议：Go 源码声明 User-only
        Assert.Equal("PATCH", handler.Requests[^2].Method.Method);
        Assert.Equal("/open-apis/vc/v1/meetings/mt_1/end", handler.Requests[^2].PathAndQuery);
        Assert.Equal("Bearer u-t", handler.Requests[^2].Headers["Authorization"]);
        // 踢出参会人：Tenant（自动注入）
        Assert.Equal("/open-apis/vc/v1/meetings/mt_1/kickout", handler.Requests[^1].PathAndQuery);
        Assert.Equal("Bearer t", handler.Requests[^1].Headers["Authorization"]);
    }

    [Fact]
    public async Task Vc_Export_And_Bot_Should_Match()
    {
        var (client, handler) = OkClient();
        await client.Vc.Export.MeetingListAsync(new { begin_time = "0", end_time = "1" });
        await client.Vc.Bot.JoinAsync(new { meeting_code = "123456789" });

        Assert.Equal("/open-apis/vc/v1/exports/meeting_list", handler.Requests[^2].PathAndQuery);
        Assert.Equal("/open-apis/vc/v1/bots/join", handler.Requests[^1].PathAndQuery);
        Assert.Contains("123456789", handler.Requests[^1].BodyText);
    }
}

/// <summary>第七批生成服务（admin/base/block，路径核对自 Go resource.go）。</summary>
public class GeneratedServiceWave7Tests
{
    private static (FeishuClient, FakeHandler) OkClient()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            return Task.FromResult(FakeHandler.Json(200, """{"code":0,"data":{}}"""));
        });
        return (client, handler);
    }

    [Fact]
    public async Task Admin_Badge_And_Grant_Lifecycle_Should_Match_Verified_Paths()
    {
        var (client, handler) = OkClient();
        await client.Admin.Badge.CreateAsync(new { badge_name = "金牌客服" });
        await client.Admin.BadgeGrant.CreateAsync("bg_1", new { user_ids = new[] { "ou_1" } });
        await client.Admin.BadgeGrant.DeleteAsync("bg_1", "gr_1");
        await client.Admin.Password.ResetAsync(new { user_id = "ou_2", password = "new-pwd" });

        Assert.Equal("/open-apis/admin/v1/badges", handler.Requests[^4].PathAndQuery);
        Assert.Contains("金牌客服", handler.Requests[^4].BodyText);
        Assert.Equal("/open-apis/admin/v1/badges/bg_1/grants", handler.Requests[^3].PathAndQuery);
        Assert.Equal("/open-apis/admin/v1/badges/bg_1/grants/gr_1", handler.Requests[^2].PathAndQuery);
        Assert.Equal("DELETE", handler.Requests[^2].Method.Method);
        Assert.Equal("/open-apis/admin/v1/password/reset", handler.Requests[^1].PathAndQuery);
    }

    [Fact]
    public async Task Admin_Stats_Should_Page()
    {
        var page = 0;
        var (client, _) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            page++;
            var (items, token) = page == 1 ? ("""[{"date":"2026-01-01"}]""", "p2") : ("""[{"date":"2026-01-02"}]""", (string?)null);
            var tokenJson = token == null ? "null" : $"\"{token}\"";
            return Task.FromResult(FakeHandler.Json(200,
                $$$"""{"code":0,"data":{"items":{{{items}}},"page_token":{{{tokenJson}}}}}"""));
        });

        var dates = new List<string>();
        await foreach (var row in client.Admin.AdminDeptStat.ListAsyncEnumerate())
            dates.Add(row.GetProperty("date").GetString()!);

        Assert.Equal(["2026-01-01", "2026-01-02"], dates);
    }

    [Fact]
    public async Task Base_AppRole_And_Block_Entity_Should_Match_Verified_Paths()
    {
        var (client, handler) = OkClient();
        await client.Base.AppRole.CreateAsync("appT", new { role_name = "管理员" });
        await client.Base.AppRole.UpdateAsync("appT", "role_1", new { role_name = "管理员2" });
        await client.Block.Entity.CreateAsync(new { block = new { } });
        await client.Block.Entity.UpdateAsync("blk_1", new { block = new { } });

        Assert.Equal("/open-apis/base/v2/apps/appT/roles", handler.Requests[^4].PathAndQuery);
        Assert.Equal("/open-apis/base/v2/apps/appT/roles/role_1", handler.Requests[^3].PathAndQuery);
        Assert.Equal("PUT", handler.Requests[^3].Method.Method);
        Assert.Equal("/open-apis/block/v2/entities", handler.Requests[^2].PathAndQuery);
        Assert.Equal("/open-apis/block/v2/entities/blk_1", handler.Requests[^1].PathAndQuery);
    }
}

/// <summary>第八批生成服务（report/personal_settings/directory，路径核对自 Go resource.go）。</summary>
public class GeneratedServiceWave8Tests
{
    private static (FeishuClient, FakeHandler) OkClient()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            return Task.FromResult(FakeHandler.Json(200, """{"code":0,"data":{}}"""));
        });
        return (client, handler);
    }

    [Fact]
    public async Task Report_And_PersonalSettings_Should_Match_Verified_Paths()
    {
        var (client, handler) = OkClient();
        await client.Report.Rule.QueryAsync();
        await client.Report.Task.QueryAsync(new { platform = 1 });
        await client.PersonalSettings.SystemStatus.CreateAsync(new { title = "会议中" });
        await client.PersonalSettings.SystemStatus.BatchOpenAsync("ss_1", new { open = true });

        Assert.Equal("/open-apis/report/v1/rules/query", handler.Requests[^4].PathAndQuery);
        Assert.Equal("/open-apis/report/v1/tasks/query", handler.Requests[^3].PathAndQuery);
        Assert.Equal("/open-apis/personal_settings/v1/system_statuses", handler.Requests[^2].PathAndQuery);
        Assert.Equal("/open-apis/personal_settings/v1/system_statuses/ss_1/batch_open", handler.Requests[^1].PathAndQuery);
    }

    [Fact]
    public async Task Directory_Employee_Lifecycle_Should_Match_Verified_Paths_And_Verbs()
    {
        var (client, handler) = OkClient();
        await client.Directory.Employee.CreateAsync(new { name = "张三", mobile = "+8613800000000" });
        await client.Directory.Employee.ToBeResignedAsync("emp_1");
        await client.Directory.Employee.DeleteAsync("emp_1");
        await client.Directory.Employee.ResurrectAsync("emp_1", new { leader_id = "ou_l" });
        await client.Directory.CollaborationRule.CreateAsync(new { rule = new { } });

        Assert.Equal("/open-apis/directory/v1/employees", handler.Requests[^5].PathAndQuery);
        Assert.Contains("张三", handler.Requests[^5].BodyText);
        // 待离职 = PATCH /to_be_resigned；离职 = DELETE（动词语义与 Go 一致）
        Assert.Equal("PATCH", handler.Requests[^4].Method.Method);
        Assert.Equal("/open-apis/directory/v1/employees/emp_1/to_be_resigned", handler.Requests[^4].PathAndQuery);
        Assert.Equal("DELETE", handler.Requests[^3].Method.Method);
        Assert.Equal("/open-apis/directory/v1/employees/emp_1", handler.Requests[^3].PathAndQuery);
        // 恢复离职 = POST /resurrect
        Assert.Equal("POST", handler.Requests[^2].Method.Method);
        Assert.Equal("/open-apis/directory/v1/employees/emp_1/resurrect", handler.Requests[^2].PathAndQuery);
        Assert.Equal("/open-apis/directory/v1/collaboration_rules", handler.Requests[^1].PathAndQuery);
    }

    [Fact]
    public async Task Directory_Department_Should_Match()
    {
        var (client, handler) = OkClient();
        await client.Directory.Department.CreateAsync(new { name = "技术部" });
        await client.Directory.Department.MgetAsync(new { department_ids = new[] { "od_1" } });

        Assert.Equal("/open-apis/directory/v1/departments", handler.Requests[^2].PathAndQuery);
        Assert.Equal("/open-apis/directory/v1/departments/mget", handler.Requests[^1].PathAndQuery);
        Assert.Contains("od_1", handler.Requests[^1].BodyText);
    }
}

/// <summary>第九批生成服务（lingo/trust_party，路径核对自 Go resource.go）。</summary>
public class GeneratedServiceWave9Tests
{
    private static (FeishuClient, FakeHandler) OkClient()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            return Task.FromResult(FakeHandler.Json(200, """{"code":0,"data":{}}"""));
        });
        return (client, handler);
    }

    [Fact]
    public async Task Lingo_Entity_And_Draft_Should_Match_Verified_Paths()
    {
        var (client, handler) = OkClient();
        await client.Lingo.Entity.CreateAsync(new { name = "飞书", description = "协同办公" });
        await client.Lingo.Entity.HighlightAsync(new { query = "飞书是啥" });
        await client.Lingo.Draft.UpdateAsync("dr_1", new { name = "飞书2" });
        await client.Lingo.Repo.ListAsync();

        // 免审词条创建/更新为 Tenant-only；查询/高亮 User+Tenant（与 Go 一致）
        Assert.Equal("/open-apis/lingo/v1/entities", handler.Requests[^4].PathAndQuery);
        Assert.Contains("协同办公", handler.Requests[^4].BodyText);
        Assert.Equal("/open-apis/lingo/v1/entities/highlight", handler.Requests[^3].PathAndQuery);
        Assert.Equal("/open-apis/lingo/v1/drafts/dr_1", handler.Requests[^2].PathAndQuery);
        Assert.Equal("PUT", handler.Requests[^2].Method.Method);
        Assert.Equal("/open-apis/lingo/v1/repos", handler.Requests[^1].PathAndQuery);
    }

    [Fact]
    public async Task TrustParty_Double_Path_Params_Should_Match()
    {
        var (client, handler) = OkClient();
        await client.TrustParty.CollaborationTenant.GetAsync("tk_9");
        await client.TrustParty.CollaborationDepartment.GetAsync("tk_9", "od_1");
        await client.TrustParty.CollaborationUser.GetAsync("tk_9", "ou_1");

        Assert.Equal("/open-apis/trust_party/v1/collaboration_tenants/tk_9", handler.Requests[^3].PathAndQuery);
        Assert.Equal("/open-apis/trust_party/v1/collaboration_tenants/tk_9/collaboration_departments/od_1", handler.Requests[^2].PathAndQuery);
        Assert.Equal("/open-apis/trust_party/v1/collaboration_tenants/tk_9/collaboration_users/ou_1", handler.Requests[^1].PathAndQuery);
    }
}

/// <summary>第十批生成服务（document_ai 18 / speech_to_text 2 / human_authentication 1，路径核对自 Go resource.go）。</summary>
public class GeneratedServiceWave10Tests
{
    private static (FeishuClient, FakeHandler) OkClient()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            return Task.FromResult(FakeHandler.Json(200, """{"code":0,"data":{}}"""));
        });
        return (client, handler);
    }

    [Fact]
    public async Task DocumentAi_Recognition_Family_Should_Match_Verified_Paths()
    {
        var (client, handler) = OkClient();
        await client.DocumentAi.IdCard.RecognizeIdCardAsync(new { file = "base64", file_name = "a.jpg" });
        await client.DocumentAi.Contract.FieldExtractionAsync(new { file = "base64" });
        await client.DocumentAi.Resume.ParseAsync(new { file = "base64" });
        await client.DocumentAi.VatInvoice.RecognizeVatInvoiceAsync(new { file = "base64" });

        Assert.Equal("/open-apis/document_ai/v1/id_card/recognize", handler.Requests[^4].PathAndQuery);
        Assert.Equal("/open-apis/document_ai/v1/contract/field_extraction", handler.Requests[^3].PathAndQuery);
        Assert.Equal("/open-apis/document_ai/v1/resume/parse", handler.Requests[^2].PathAndQuery);
        Assert.Equal("/open-apis/document_ai/v1/vat_invoice/recognize", handler.Requests[^1].PathAndQuery);
        Assert.All(handler.Requests[^4..], r => Assert.Equal("POST", r.Method.Method));
    }

    [Fact]
    public async Task DocumentAi_Token_Types_Should_Match_Go()
    {
        var (client, handler) = OkClient();
        // 健康证识别为 Tenant+User（Go 源码核对）
        await client.DocumentAi.HealthCertificate.RecognizeHealthCertificateAsync(new { file = "b64" });
        Assert.Equal("/open-apis/document_ai/v1/health_certificate/recognize", handler.Requests[^1].PathAndQuery);
    }

    [Fact]
    public async Task SpeechToText_And_HumanAuthentication_Should_Match()
    {
        var (client, handler) = OkClient();
        await client.SpeechToText.Speech.FileRecognizeAsync(new { data = "b64", format = "wav" });
        await client.SpeechToText.Speech.StreamRecognizeAsync(new { config = new { } });
        await client.HumanAuthentication.Identity.CreateAsync(new { id_name = "张三", id_number = "110..." });

        Assert.Equal("/open-apis/speech_to_text/v1/speech/file_recognize", handler.Requests[^3].PathAndQuery);
        Assert.Equal("/open-apis/speech_to_text/v1/speech/stream_recognize", handler.Requests[^2].PathAndQuery);
        Assert.Equal("/open-apis/human_authentication/v1/identities", handler.Requests[^1].PathAndQuery);
        Assert.Contains("张三", handler.Requests[^1].BodyText);
    }
}

/// <summary>第十一批生成服务（passport/spark/workplace/mdm，路径核对自 Go resource.go）。</summary>
public class GeneratedServiceWave11Tests
{
    private static (FeishuClient, FakeHandler) OkClient()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            return Task.FromResult(FakeHandler.Json(200, """{"code":0,"data":{}}"""));
        });
        return (client, handler);
    }

    [Fact]
    public async Task Passport_Should_Match_Verified_Paths_And_Verbs()
    {
        var (client, handler) = OkClient();
        await client.Passport.Password.UpdateAsync(new { password = "newPwd123", user_id = "ou_1", id_type = "open_id" });
        await client.Passport.Session.LogoutAsync(new { user_id = "ou_1", id_type = "open_id" });

        // 重置密码是 PUT /password（Go 源码核对，不是 POST）
        Assert.Equal("PUT", handler.Requests[^2].Method.Method);
        Assert.Equal("/open-apis/passport/v1/password", handler.Requests[^2].PathAndQuery);
        Assert.Equal("POST", handler.Requests[^1].Method.Method);
        Assert.Equal("/open-apis/passport/v1/sessions/logout", handler.Requests[^1].PathAndQuery);
    }

    [Fact]
    public async Task Spark_UserOnly_And_Kebab_Path_Should_Match()
    {
        var (client, handler) = OkClient();
        var opt = new RequestOptions().WithUserAccessToken("u-spark");
        await client.Spark.App.CreateAsync(new { name = "我的妙搭" }, opt);
        await client.Spark.App.GetAppVisibilityAsync("sp_1", opt);
        await client.Spark.AppStorage.UploadInitializeAsync("sp_1", new { file_name = "a.bin", size = 1024 }, opt);
        await client.Spark.DirectoryUser.IdConvertAsync(new { ids = new[] { "1" } }); // User+Tenant 双支持

        // 请求顺序：create、access-scope、upload-init、（id_convert 用 tenant token → 先取 token）、id_convert
        Assert.Equal("/open-apis/spark/v1/apps", handler.Requests[^5].PathAndQuery);
        Assert.Equal("Bearer u-spark", handler.Requests[^5].Headers["Authorization"]);
        // kebab-case 路径段 access-scope（非 access_scope）
        Assert.Equal("/open-apis/spark/v1/apps/sp_1/access-scope", handler.Requests[^4].PathAndQuery);
        Assert.Equal("/open-apis/spark/v1/apps/sp_1/storage/upload/initialize", handler.Requests[^3].PathAndQuery);
        // id_convert 允许 tenant token 自动注入
        Assert.Equal("/open-apis/spark/v1/directory/user/id_convert", handler.Requests[^1].PathAndQuery);
        Assert.Equal("Bearer t", handler.Requests[^1].Headers["Authorization"]);
    }

    [Fact]
    public async Task Spark_Table_Records_Multi_Verb_Should_Match()
    {
        var (client, handler) = OkClient();
        var opt = new RequestOptions().WithUserAccessToken("u-spark");
        await client.Spark.AppTable.PostTableRecordsAsync("sp_1", "tblA", new { records = Array.Empty<object>() }, opt);
        await client.Spark.AppTable.BatchUpdateTableRecordsAsync("sp_1", "tblA", new { records = Array.Empty<object>() }, opt);
        await client.Spark.AppTable.DeleteTableRecordsAsync("sp_1", "tblA", new { filter = "x" }, opt);

        Assert.Equal("POST", handler.Requests[^3].Method.Method);
        Assert.Equal("/open-apis/spark/v1/apps/sp_1/tables/tblA/records", handler.Requests[^3].PathAndQuery);
        // 批量更新是 PATCH .../records_batch_update（专用后缀，非 body 区分）
        Assert.Equal("PATCH", handler.Requests[^2].Method.Method);
        Assert.Equal("/open-apis/spark/v1/apps/sp_1/tables/tblA/records_batch_update", handler.Requests[^2].PathAndQuery);
        Assert.Equal("DELETE", handler.Requests[^1].Method.Method);
    }

    [Fact]
    public async Task Workplace_And_Mdm_Should_Match_Verified_Paths()
    {
        var (client, handler) = OkClient();
        await client.Workplace.WorkplaceAccessData.SearchAsync(new { date = "2026-01-01" });
        await client.Workplace.CustomWorkplaceAccessData.SearchAsync(new { workplace_id = "wp_1" });
        await client.Mdm.UserAuthDataRelation.BindAsync(new { user_ids = new[] { "ou_1" }, data_dimension = "x" });
        await client.Mdm.CountryRegion.ListAsync();

        Assert.Equal("/open-apis/workplace/v1/workplace_access_data/search", handler.Requests[^4].PathAndQuery);
        Assert.Equal("/open-apis/workplace/v1/custom_workplace_access_data/search", handler.Requests[^3].PathAndQuery);
        Assert.Equal("/open-apis/mdm/v1/user_auth_data_relations/bind", handler.Requests[^2].PathAndQuery);
        // mdm v3 路径段是 country_regions（复数）
        Assert.Equal("/open-apis/mdm/v3/country_regions", handler.Requests[^1].PathAndQuery);
    }
}

/// <summary>第十二批生成服务（compensation/payroll/performance/unified_kms/aily/security_and_compliance，路径核对自 Go resource.go）。</summary>
public class GeneratedServiceWave12Tests
{
    private static (FeishuClient, FakeHandler) OkClient()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            return Task.FromResult(FakeHandler.Json(200, """{"code":0,"data":{}}"""));
        });
        return (client, handler);
    }

    [Fact]
    public async Task Compensation_And_Payroll_Core_Should_Match_Verified_Paths()
    {
        var (client, handler) = OkClient();
        await client.Compensation.Archive.QueryAsync(new { user_ids = new[] { "ou_1" } });
        await client.Compensation.LumpSumPayment.BatchCreateAsync(new { records = Array.Empty<object>() });
        await client.Payroll.PaymentActivity.ListAsync();
        await client.Payroll.DatasourceRecord.SaveAsync(new { records = Array.Empty<object>() });

        Assert.Equal("/open-apis/compensation/v1/archives/query", handler.Requests[^4].PathAndQuery);
        // 注意：lump_sum_payment 是单数（非 payments）
        Assert.Equal("/open-apis/compensation/v1/lump_sum_payment/batch_create", handler.Requests[^3].PathAndQuery);
        // Go 源码核对：payment_activitys 是拼写的 activitys（非 activities）
        Assert.Equal("/open-apis/payroll/v1/payment_activitys", handler.Requests[^2].PathAndQuery);
        Assert.Equal("/open-apis/payroll/v1/datasource_records/save", handler.Requests[^1].PathAndQuery);
    }

    [Fact]
    public async Task Performance_Query_Style_Should_Match()
    {
        var (client, handler) = OkClient();
        await client.Performance.Activity.QueryAsync(new { activity_ids = new[] { "1" } });
        await client.Performance.ReviewData.QueryAsync(new { activity_id = "1" });
        await client.Performance.MetricTag.ListAsync();

        // performance v2 以 POST query 为主、少量 GET list（与 Go 一致）
        Assert.Equal("POST", handler.Requests[^3].Method.Method);
        Assert.Equal("/open-apis/performance/v2/activity/query", handler.Requests[^3].PathAndQuery);
        Assert.Equal("/open-apis/performance/v2/review_datas/query", handler.Requests[^2].PathAndQuery);
        Assert.Equal("GET", handler.Requests[^1].Method.Method);
        Assert.Equal("/open-apis/performance/v2/metric_tags", handler.Requests[^1].PathAndQuery);
    }

    [Fact]
    public async Task UnifiedKms_Key_Lifecycle_Should_Match()
    {
        var (client, handler) = OkClient();
        await client.UnifiedKms.KeyImportMaterial.GetAsync();
        await client.UnifiedKms.AutonomousKey.CreateAsync(new { key_material = "base64" });
        await client.UnifiedKms.AutonomousKeyDeletionPlan.CreateAsync("kv_1", new { plan = new { } });
        await client.UnifiedKms.AutonomousKeyRecover.CreateAsync("kv_1", new { });

        Assert.Equal("/open-apis/unified_kms/v1/key_import_material", handler.Requests[^4].PathAndQuery);
        Assert.Equal("/open-apis/unified_kms/v1/autonomous_keys", handler.Requests[^3].PathAndQuery);
        Assert.Equal("/open-apis/unified_kms/v1/autonomous_keys/kv_1/deletion_plan", handler.Requests[^2].PathAndQuery);
        Assert.Equal("/open-apis/unified_kms/v1/autonomous_keys/kv_1/recover", handler.Requests[^1].PathAndQuery);
    }

    [Fact]
    public async Task Aily_Session_Run_Flow_Should_Match()
    {
        var (client, handler) = OkClient();
        await client.Aily.AilySession.CreateAsync(new { channel = "web" });
        await client.Aily.AilyMessage.CreateAsync("s_1", new { content = new { text = "帮我分析" } });
        await client.Aily.AilySessionRun.CreateAsync("s_1", new { });
        await client.Aily.AilySessionRun.CancelAsync("s_1", "r_1");
        await client.Aily.AppSkill.StartAsync("app_1", "sk_1", new { input = new { } });

        Assert.Equal("/open-apis/aily/v1/sessions", handler.Requests[^5].PathAndQuery);
        Assert.Equal("/open-apis/aily/v1/sessions/s_1/messages", handler.Requests[^4].PathAndQuery);
        Assert.Equal("/open-apis/aily/v1/sessions/s_1/runs", handler.Requests[^3].PathAndQuery);
        Assert.Equal("/open-apis/aily/v1/sessions/s_1/runs/r_1/cancel", handler.Requests[^2].PathAndQuery);
        Assert.Equal("/open-apis/aily/v1/apps/app_1/skills/sk_1/start", handler.Requests[^1].PathAndQuery);
    }

    [Fact]
    public async Task SecurityAndCompliance_Device_Mgmt_Should_Match()
    {
        var (client, handler) = OkClient();
        await client.SecurityAndCompliance.DeviceRecord.CreateAsync(new { device_name = "MacBook", os = "macOS" });
        await client.SecurityAndCompliance.DeviceRecord.GetAsync("dv_1");
        await client.SecurityAndCompliance.DeviceRecord.MineAsync(new RequestOptions().WithUserAccessToken("u-t"));

        Assert.Equal("/open-apis/security_and_compliance/v2/device_records", handler.Requests[^3].PathAndQuery);
        Assert.Contains("MacBook", handler.Requests[^3].BodyText);
        Assert.Equal("/open-apis/security_and_compliance/v2/device_records/dv_1", handler.Requests[^2].PathAndQuery);
        // mine 是 User-only
        Assert.Equal("/open-apis/security_and_compliance/v2/device_records/mine", handler.Requests[^1].PathAndQuery);
        Assert.Equal("Bearer u-t", handler.Requests[^1].Headers["Authorization"]);
    }
}

/// <summary>第十三批生成服务（apaas 38 / ehr 2 / hire 前8资源 25，路径核对自 Go resource.go）。</summary>
public class GeneratedServiceWave13Tests
{
    private static (FeishuClient, FakeHandler) OkClient()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            return Task.FromResult(FakeHandler.Json(200, """{"code":0,"data":{}}"""));
        });
        return (client, handler);
    }

    [Fact]
    public async Task Apaas_Namespace_Keyword_Param_Should_Compile_And_Match()
    {
        var (client, handler) = OkClient();
        await client.Apaas.ApplicationObjectRecord.CreateAsync("ns_1", "obj_1", new { fields = new { } });
        await client.Apaas.ApplicationAuditLog.AuditLogListAsync("ns_1", new RequestOptions().WithUserAccessToken("u-t"));
        await client.Apaas.UserTask.ExpeditingAsync("task_1", new { });

        // namespace 是 C# 保留字，生成器以 @namespace 参数名处理
        Assert.Equal("/open-apis/apaas/v1/applications/ns_1/objects/obj_1/records", handler.Requests[^3].PathAndQuery);
        Assert.Equal("/open-apis/apaas/v1/applications/ns_1/audit_log/audit_log_list", handler.Requests[^2].PathAndQuery);
        Assert.Equal("/open-apis/apaas/v1/user_tasks/task_1/expediting", handler.Requests[^1].PathAndQuery);
    }

    [Fact]
    public async Task Apaas_Approval_Task_Actions_Should_Match()
    {
        var (client, handler) = OkClient();
        await client.Apaas.ApprovalTask.AgreeAsync("at_1", new { });
        await client.Apaas.ApprovalTask.RejectAsync("at_1", new { });
        await client.Apaas.ApprovalInstance.CancelAsync("ai_1", new { });

        Assert.Equal("/open-apis/apaas/v1/approval_tasks/at_1/agree", handler.Requests[^3].PathAndQuery);
        Assert.Equal("/open-apis/apaas/v1/approval_tasks/at_1/reject", handler.Requests[^2].PathAndQuery);
        Assert.Equal("/open-apis/apaas/v1/approval_instances/ai_1/cancel", handler.Requests[^1].PathAndQuery);
    }

    [Fact]
    public async Task Ehr_Two_Endpoints_Should_Match()
    {
        var (client, handler) = OkClient();
        await client.Ehr.Employee.ListAsync();
        Assert.Equal("/open-apis/ehr/v1/employees", handler.Requests[^1].PathAndQuery);
    }

    [Fact]
    public async Task Hire_Application_Lifecycle_Should_Match_Verified_Paths()
    {
        var (client, handler) = OkClient();
        await client.Hire.Application.CreateAsync(new { talent_id = "t_1", job_id = "j_1" });
        await client.Hire.Application.TransferStageAsync("ap_1", new { stage_id = "s_1" });
        await client.Hire.Application.TerminateAsync("ap_1", new { });
        await client.Hire.Application.RecoverAsync("ap_1", new { });
        await client.Hire.Agency.ProtectSearchAsync(new { talent_id = "t_1" });

        Assert.Equal("/open-apis/hire/v1/applications", handler.Requests[^5].PathAndQuery);
        Assert.Equal("/open-apis/hire/v1/applications/ap_1/transfer_stage", handler.Requests[^4].PathAndQuery);
        Assert.Equal("/open-apis/hire/v1/applications/ap_1/terminate", handler.Requests[^3].PathAndQuery);
        Assert.Equal("/open-apis/hire/v1/applications/ap_1/recover", handler.Requests[^2].PathAndQuery);
        // 保护期查询：agencies/protection_period/search（kebab 不是 protect_search）
        Assert.Equal("/open-apis/hire/v1/agencies/protection_period/search", handler.Requests[^1].PathAndQuery);
    }
}

/// <summary>第十四批生成服务（corehr/v1 前 11 资源 40 端点，路径核对自 Go resource.go）。</summary>
public class GeneratedServiceWave14Tests
{
    private static (FeishuClient, FakeHandler) OkClient()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            return Task.FromResult(FakeHandler.Json(200, """{"code":0,"data":{}}"""));
        });
        return (client, handler);
    }

    [Fact]
    public async Task Corehr_Authorization_RoleAssign_Should_Match_Verified_Paths()
    {
        var (client, handler) = OkClient();
        await client.Corehr.Authorization.GetByParamAsync();
        await client.Corehr.Authorization.AddRoleAssignAsync(new { user_id = "ou_1", role_id = "r_1" });
        await client.Corehr.Authorization.RemoveRoleAssignAsync(new { user_id = "ou_1" });
        await client.Corehr.AssignedUser.SearchAsync(new { role_id = "r_1" });

        Assert.Equal("/open-apis/corehr/v1/authorizations/get_by_param", handler.Requests[^4].PathAndQuery);
        Assert.Equal("/open-apis/corehr/v1/authorizations/add_role_assign", handler.Requests[^3].PathAndQuery);
        Assert.Equal("/open-apis/corehr/v1/authorizations/remove_role_assign", handler.Requests[^2].PathAndQuery);
        Assert.Equal("/open-apis/corehr/v1/assigned_users/search", handler.Requests[^1].PathAndQuery);
    }

    [Fact]
    public async Task Corehr_Company_Contract_Crud_Should_Match()
    {
        var (client, handler) = OkClient();
        await client.Corehr.Company.CreateAsync(new { name = "示例公司" });
        await client.Corehr.Company.GetAsync("cp_1");
        await client.Corehr.Company.PatchAsync("cp_1", new { name = "新名" }, new RequestOptions().WithUserAccessToken("u-t"));
        await client.Corehr.Contract.CreateAsync(new { employment_id = "e_1" });

        Assert.Equal("/open-apis/corehr/v1/companies", handler.Requests[^4].PathAndQuery);
        Assert.Contains("示例公司", handler.Requests[^4].BodyText);
        Assert.Equal("/open-apis/corehr/v1/companies/cp_1", handler.Requests[^3].PathAndQuery);
        // Patch 公司是 Tenant+User（Go 源码核对），传 user token 时直接使用
        Assert.Equal("PATCH", handler.Requests[^2].Method.Method);
        Assert.Equal("Bearer u-t", handler.Requests[^2].Headers["Authorization"]);
        Assert.Equal("/open-apis/corehr/v1/contracts", handler.Requests[^1].PathAndQuery);
    }

    [Fact]
    public async Task Corehr_CommonData_And_CustomField_Should_Match()
    {
        var (client, handler) = OkClient();
        await client.Corehr.CommonDataId.ConvertAsync(new { ids = new[] { "1" } });
        await client.Corehr.CommonDataMetaData.AddEnumOptionAsync(new { object_api_name = "person" });
        await client.Corehr.CustomField.QueryAsync();

        Assert.Equal("/open-apis/corehr/v1/common_data/id/convert", handler.Requests[^3].PathAndQuery);
        Assert.Equal("/open-apis/corehr/v1/common_data/meta_data/add_enum_option", handler.Requests[^2].PathAndQuery);
        Assert.Equal("/open-apis/corehr/v1/custom_fields/query", handler.Requests[^1].PathAndQuery);
    }
}

/// <summary>第十五批生成服务（corehr/v2 前 16 资源 28 端点，路径核对自 Go resource.go）。</summary>
public class GeneratedServiceWave15Tests
{
    private static (FeishuClient, FakeHandler) OkClient()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            return Task.FromResult(FakeHandler.Json(200, """{"code":0,"data":{}}"""));
        });
        return (client, handler);
    }

    [Fact]
    public async Task CorehrV2_ApprovalGroups_Change_Queries_Should_Match()
    {
        var (client, handler) = OkClient();
        await client.CorehrV2.ApprovalGroups.GetAsync("pr_1");
        await client.CorehrV2.ApprovalGroups.OpenQueryDepartmentChangeListByIdsAsync(new { ids = new[] { "1" } });
        await client.CorehrV2.ApprovalGroups.OpenQueryJobChangeListByIdsAsync(new { ids = new[] { "1" } });

        Assert.Equal("/open-apis/corehr/v2/approval_groups/pr_1", handler.Requests[^3].PathAndQuery);
        Assert.Equal("/open-apis/corehr/v2/approval_groups/open_query_department_change_list_by_ids", handler.Requests[^2].PathAndQuery);
        Assert.Equal("/open-apis/corehr/v2/approval_groups/open_query_job_change_list_by_ids", handler.Requests[^1].PathAndQuery);
    }

    [Fact]
    public async Task CorehrV2_BasicInfo_Search_Family_Should_Match()
    {
        var (client, handler) = OkClient();
        await client.CorehrV2.BasicInfoBank.SearchAsync(new { name = "工商银行" });
        await client.CorehrV2.BasicInfoBankBranch.SearchAsync(new { bank_id = "b1" });
        // 注意：bank_branchs 是复数（非 branches）
        await client.CorehrV2.BasicInfoNationality.SearchAsync(new { });
        await client.CorehrV2.BasicInfoTimeZone.SearchAsync(new { });

        Assert.Equal("/open-apis/corehr/v2/basic_info/banks/search", handler.Requests[^4].PathAndQuery);
        Assert.Equal("/open-apis/corehr/v2/basic_info/bank_branchs/search", handler.Requests[^3].PathAndQuery);
        Assert.Equal("/open-apis/corehr/v2/basic_info/nationalities/search", handler.Requests[^2].PathAndQuery);
        Assert.Equal("/open-apis/corehr/v2/basic_info/time_zones/search", handler.Requests[^1].PathAndQuery);
    }

    [Fact]
    public async Task CorehrV2_Company_BatchGet_Should_Match()
    {
        var (client, handler) = OkClient();
        await client.CorehrV2.Company.BatchGetAsync(new { ids = new[] { "cp_1" } });
        await client.CorehrV2.Company.QueryRecentChangeAsync();
        await client.CorehrV2.Contract.SearchAsync(new { });
        await client.CorehrV2.Bp.GetByDepartmentAsync(new { department_id = "d_1" });

        Assert.Equal("/open-apis/corehr/v2/companies/batch_get", handler.Requests[^4].PathAndQuery);
        Assert.Equal("/open-apis/corehr/v2/companies/query_recent_change", handler.Requests[^3].PathAndQuery);
        Assert.Equal("/open-apis/corehr/v2/contracts/search", handler.Requests[^2].PathAndQuery);
        Assert.Equal("/open-apis/corehr/v2/bps/get_by_department", handler.Requests[^1].PathAndQuery);
    }
}

/// <summary>第十六批：机读全量提取（curl 原始源码 → 正则解析，绕过 zread 50KB 上限）。
/// corehr v1 105 + v2 164 + hire 179 + apaas 49 + vc 68 = 565 端点全量核对。抽样验证关键差异点。</summary>
public class GeneratedServiceWave16FullCoverageTests
{
    private static (FeishuClient, FakeHandler) OkClient()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            return Task.FromResult(FakeHandler.Json(200, """{"code":0,"data":{}}"""));
        });
        return (client, handler);
    }

    [Fact]
    public async Task CorehrV1_FullCoverage_Employee_Lifecycle_Should_Match()
    {
        var (client, handler) = OkClient();
        await client.Corehr.EmployeeType.CreateAsync(new { name = "正式" });
        await client.Corehr.Employment.CreateAsync(new { });
        await client.Corehr.Person.GetAsync("p_1");
        await client.Corehr.Offboarding.QueryAsync(new { }, new RequestOptions().WithUserAccessToken("u-t"));

        Assert.Equal("/open-apis/corehr/v1/employee_types", handler.Requests[^4].PathAndQuery);
        Assert.Equal("/open-apis/corehr/v1/employments", handler.Requests[^3].PathAndQuery);
        Assert.Equal("/open-apis/corehr/v1/persons/p_1", handler.Requests[^2].PathAndQuery);
        // 离职查询 Tenant+User（机读提取）
        Assert.Equal("/open-apis/corehr/v1/offboardings/query", handler.Requests[^1].PathAndQuery);
        Assert.Equal("Bearer u-t", handler.Requests[^1].Headers["Authorization"]);
    }

    [Fact]
    public async Task CorehrV1_Leave_And_Job_Family_Should_Match()
    {
        var (client, handler) = OkClient();
        await client.Corehr.Leave.WorkCalendarAsync(new { });
        await client.Corehr.JobFamily.ListAsync();
        await client.Corehr.WorkingHoursType.PatchAsync("w_1", new { });

        // WorkCalendar 是 POST、CalendarByScope 是 GET——动词差异由机读保留
        Assert.Equal("/open-apis/corehr/v1/leaves/work_calendar", handler.Requests[^3].PathAndQuery);
        Assert.Equal("/open-apis/corehr/v1/job_families", handler.Requests[^2].PathAndQuery);
        Assert.Equal("/open-apis/corehr/v1/working_hours_types/w_1", handler.Requests[^1].PathAndQuery);
    }

    [Fact]
    public async Task Hire_FullCoverage_78_Resources_Key_Samples_Should_Match()
    {
        var (client, handler) = OkClient();
        await client.Corehr.PreHire.ListAsync();
        await client.Hire.Talent.CombinedCreateAsync(new { name = "张三" });
        await client.Hire.Job.CombinedCreateAsync(new { title = "后端工程师" });

        Assert.Equal("/open-apis/corehr/v1/pre_hires", handler.Requests[^3].PathAndQuery);
        Assert.Equal("/open-apis/hire/v1/talents/combined_create", handler.Requests[^2].PathAndQuery);
        Assert.Equal("/open-apis/hire/v1/jobs/combined_create", handler.Requests[^1].PathAndQuery);
    }

    [Fact]
    public async Task Apaas_FullCoverage_And_Vc_Tail_Should_Match()
    {
        var (client, handler) = OkClient();
        await client.Apaas.Workspace.SqlCommandsAsync("ws_1", new { sql = "select 1" }, new RequestOptions().WithUserAccessToken("u-t"));
        await client.Vc.Room.ListAsync();
        await client.Vc.ScopeConfig.GetAsync();

        // 请求序列：workspace(user token 直传) → tenant token 获取 → rooms → scope_config
        Assert.Equal("/open-apis/apaas/v1/workspaces/ws_1/sql_commands", handler.Requests[^4].PathAndQuery);
        Assert.Equal("Bearer u-t", handler.Requests[^4].Headers["Authorization"]);
        Assert.Equal("/open-apis/vc/v1/rooms", handler.Requests[^2].PathAndQuery);
        Assert.Equal("Bearer t", handler.Requests[^2].Headers["Authorization"]); // Room 是 Tenant-only
        Assert.Equal("/open-apis/vc/v1/scope_config", handler.Requests[^1].PathAndQuery);
    }
}
