using System.Text;
using System.Text.Json;

namespace FeishuSdk.Tests;

using Feishu.Channel;

/// <summary>Channel 安全组件 / 流水线 / 归一化（对齐 Go channel 各子模块）。</summary>
public class ChannelComponentTests
{
    [Fact]
    public void DedupCache_Should_Detect_Duplicate_And_Expire()
    {
        var cache = new DedupCache(10, TimeSpan.FromMilliseconds(60));

        Assert.False(cache.IsDuplicate("a")); // 首次
        Assert.True(cache.IsDuplicate("a"));  // 重复
        Thread.Sleep(90);
        Assert.False(cache.IsDuplicate("a")); // TTL 过期
        Assert.False(cache.IsDuplicate(""));
    }

    [Fact]
    public void DedupCache_Should_Evict_Oldest_At_Capacity()
    {
        var cache = new DedupCache(2, TimeSpan.FromMinutes(1));
        _ = cache.IsDuplicate("k1");
        _ = cache.IsDuplicate("k2");
        _ = cache.IsDuplicate("k3"); // 容量 2 → 挤掉最旧的 k1

        Assert.False(cache.IsDuplicate("k1")); // k1 被逐出 → 重新写入（LRU=[k1,k3] 挤出 k2）
        Assert.False(cache.IsDuplicate("k2")); // k2 也被挤出 → 重新写入（LRU=[k2,k1] 挤出 k3）
        Assert.True(cache.IsDuplicate("k2"));  // 刚回填的 k2 在窗口内 → 重复
    }

    [Fact]
    public void ProcessingLock_Should_Block_Until_Release()
    {
        var gate = new ProcessingLock(TimeSpan.FromMinutes(1));
        Assert.True(gate.Acquire("x"));
        Assert.False(gate.Acquire("x"));
        gate.Release("x");
        Assert.True(gate.Acquire("x"));
    }

    [Fact]
    public void StaleDetector_Should_Match_Go_Semantics()
    {
        Assert.True(StaleDetector.IsStale((long)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - TimeSpan.FromHours(2).TotalMilliseconds), TimeSpan.FromMinutes(30)));
        Assert.False(StaleDetector.IsStale(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), TimeSpan.FromMinutes(30)));
        Assert.False(StaleDetector.IsStale(0, TimeSpan.FromMilliseconds(1))); // 零值不过期
    }

    [Theory]
    [InlineData("group", true, false, false, "NoMention")]        // 群里没@机器人
    [InlineData("group", true, true, false, null)]                 // @了机器人 → 放行
    [InlineData("group", true, true, true, "MentionAllBlocked")]   // @所有人被拦
    [InlineData("p2p", true, false, false, null)]                  // 私信默认开放
    public void PolicyGate_Should_Match_Go_Decisions(string chatType, bool requireMention, bool mentionedBot, bool mentionAll, string? expectReason)
    {
        var gate = new PolicyGate(new ChannelPolicyConfig { RequireMention = requireMention, RespondToMentionAll = false });
        var msg = new NormalizedMessage { ChatType = chatType, ChatId = "oc_1", UserId = "ou_1", MentionedBot = mentionedBot, MentionAll = mentionAll };

        var decision = gate.Evaluate(msg);

        Assert.Equal(expectReason == null, decision.Allowed);
        Assert.Equal(expectReason, decision.Reason?.ToString());
    }

    [Fact]
    public void PolicyGate_GroupAllowlist_And_DmAllowlist_Should_Work()
    {
        var gate = new PolicyGate(new ChannelPolicyConfig
        {
            GroupAllowlist = ["oc_ok"],
            RequireMention = false,
            DmMode = "allowlist",
            DmAllowlist = ["ou_ok"],
        });

        Assert.False(gate.Evaluate(new NormalizedMessage { ChatType = "group", ChatId = "oc_bad", UserId = "u" }).Allowed); // 群白名单
        Assert.True(gate.Evaluate(new NormalizedMessage { ChatType = "group", ChatId = "oc_ok", UserId = "u" }).Allowed);
        Assert.False(gate.Evaluate(new NormalizedMessage { ChatType = "p2p", ChatId = "c", UserId = "ou_bad" }).Allowed);   // 私信白名单
        Assert.True(gate.Evaluate(new NormalizedMessage { ChatType = "p2p", ChatId = "c", UserId = "ou_ok" }).Allowed);
    }

    [Fact]
    public async Task ChatPipeline_Should_Batch_Merge_And_Track_SourceIds()
    {
        var manager = new ChatPipelineManager(new ChannelBatchConfig { MaxMessages = 2, Delay = TimeSpan.FromMinutes(5) });
        BatchedDispatch? got = null;
        var done = new TaskCompletionSource();

        manager.Push("chat-1", Msg("m1", "hello"), batch => { got = batch; done.TrySetResult(); return Task.CompletedTask; });
        manager.Push("chat-1", Msg("m2", "world"), _ => Task.CompletedTask);

        await done.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(got);
        Assert.Equal("hello\n\nworld", got!.Message.Content); // 合并规则：\n\n 连接
        Assert.Equal(["m1", "m2"], got.SourceIds);
        manager.Dispose();
    }

    [Fact]
    public async Task ChatPipeline_RunAsync_Should_Serialize_Per_Scope()
    {
        var manager = new ChatPipelineManager(new ChannelBatchConfig());
        var order = new List<int>();
        var gate = new SemaphoreSlim(1, 1);
        var interleaved = false;
        var inScope = 0;

        var tasks = Enumerable.Range(0, 5).Select(i => manager.RunAsync("scope-1", async () =>
        {
            var entered = Interlocked.Increment(ref inScope);
            if (entered > 1) interleaved = true;
            try
            {
                await gate.WaitAsync(); // 人为制造重叠窗口
                gate.Release();
            }
            finally
            {
                Interlocked.Decrement(ref inScope);
                lock (order) order.Add(i);
            }
        }));
        await Task.WhenAll(tasks);

        Assert.False(interleaved); // 同作用域内串行
        Assert.Equal(5, order.Count);
        manager.Dispose();
    }

    private static NormalizedMessage Msg(string id, string content) => new() { MessageId = id, ChatId = "chat-1", Content = content };
}

/// <summary>归一化与 Markdown 转换（对齐 Go channel/normalize）。</summary>
public class ChannelNormalizeTests
{
    [Fact]
    public void ParseMessage_Should_Extract_All_Fields()
    {
        var payload = """
        {
          "schema": "2.0",
          "header": {"event_id": "ev-1", "event_type": "im.message.receive_v1", "create_time": "1700000000123"},
          "event": {
            "sender": {"sender_id": {"open_id": "ou_sender", "user_id": "u9"}, "sender_type": "app"},
            "message": {
              "message_id": "om_1", "chat_id": "oc_1", "chat_type": "group", "message_type": "text",
              "content": "{\"text\":\"你好 <at user_id=\\\"all\\\">@所有人</at>\"}",
              "mentions": [
                {"key": "@_all", "id": {"open_id": "ou_all"}, "name": "所有人"},
                {"key": "@_user_1", "id": {"open_id": "ou_bot", "user_id": "u_bot"}, "name": "机器人"}
              ]
            }
          }
        }
        """;

        var norm = ChannelNormalize.ParseMessage(Encoding.UTF8.GetBytes(payload));

        Assert.NotNull(norm);
        Assert.Equal("ev-1", norm!.EventId);
        Assert.Equal(1700000000123, norm.CreateTimeMs);
        Assert.Equal("om_1", norm.MessageId);
        Assert.Equal("oc_1", norm.ChatId);
        Assert.Equal("group", norm.ChatType);
        Assert.Equal("ou_sender", norm.UserId);
        Assert.Equal(2, norm.Mentions.Count);
        Assert.True(norm.MentionAll); // mentions 里带 @_all
    }

    [Theory]
    [InlineData("text", """{"text":"hi"}""", "hi")]
    [InlineData("image", """{"image_key":"img_k"}""", "![image](img_k)")]
    [InlineData("file", """{"file_key":"f_k","file_name":"报告.pdf"}""", "<file key=\"f_k\" name=\"报告.pdf\"/>")]
    [InlineData("audio", """{"file_key":"a_k","duration":65000}""", "<audio key=\"a_k\" duration=\"1:05\"/>")]
    [InlineData("interactive", "{}", "[interactive card]")]
    [InlineData("unknown_type", "{}", "[unsupported message]")]
    public void ParseContent_Common_Types_Should_Match(string type, string content, string expected)
    {
        var (text, _) = ChannelNormalize.ParseContent(type, content);
        Assert.Equal(expected, text);
    }

    [Fact]
    public void ParseContent_File_Resource_Should_Be_Extracted()
    {
        var (text, resources) = ChannelNormalize.ParseContent("image", """{"image_key":"img_9"}""");

        Assert.Equal("![image](img_9)", text);
        var res = Assert.Single(resources);
        Assert.Equal("image", res.Type);
        Assert.Equal("img_9", res.FileKey);
    }

    [Fact]
    public void ParseContent_Post_Should_Render_Markdown_Lines()
    {
        var content = """
        {"zh_cn":{"title":"标题","content":[
          [{"tag":"text","text":"行一 "},{"tag":"a","text":"链接","href":"https://x.y"}],
          [{"tag":"img","image_key":"img_1"}],
          [{"tag":"code_block","language":"go","text":"fmt.Println(1)"}]
        ]}}
        """;

        var (text, resources) = ChannelNormalize.ParseContent("post", content);

        Assert.Contains("**标题**", text);
        Assert.Contains("行一 [链接](https://x.y)", text);
        Assert.Contains("![image](img_1)", text);
        Assert.Contains("```go", text);
        Assert.Equal("img_1", Assert.Single(resources).FileKey);
    }

    [Fact]
    public void SimpleMarkdownToPost_Should_Wrap_With_Md_Tag_And_Mentions()
    {
        var json = ChannelNormalize.SimpleMarkdownToPost("标题一", "**加粗**", new List<ChannelMention>
        {
            new() { UserId = "ou_u1", Name = "张三" },
        });

        using var doc = JsonDocument.Parse(json);
        var zh = doc.RootElement.GetProperty("zh_cn");
        Assert.Equal("标题一", zh.GetProperty("title").GetString());
        var content = zh.GetProperty("content");
        // 第一段：at + 空格；第二段：md
        var first = content[0];
        Assert.Equal("at", first[0].GetProperty("tag").GetString());
        Assert.Equal("ou_u1", first[0].GetProperty("user_id").GetString());
        Assert.Equal(" ", first[1].GetProperty("text").GetString());
        Assert.Equal("md", content[1][0].GetProperty("tag").GetString());
        Assert.Equal("**加粗**", content[1][0].GetProperty("text").GetString());
    }

    [Fact]
    public void ComposeMentionsTextPrefix_Should_Format_At_Tags()
    {
        var prefix = ChannelNormalize.ComposeMentionsTextPrefix(new List<ChannelMention>
        {
            new() { UserId = "ou_1", Name = "A" },
            new() { UserId = "ou_2", Name = "B" },
        });

        Assert.Equal("<at user_id=\"ou_1\">A</at> <at user_id=\"ou_2\">B</at> ", prefix);
        Assert.Equal("", ChannelNormalize.ComposeMentionsTextPrefix(null));
    }

    [Fact]
    public void SplitWithCodeFences_Should_Close_And_Reopen_Fence()
    {
        var code = "```go\n" + string.Join("\n", Enumerable.Range(0, 60).Select(i => $"line {i}")) + "\n```";
        var text = "# 标题\n" + code + "\n结尾文字";

        var chunks = ChannelNormalize.SplitWithCodeFences(text, 200);

        Assert.True(chunks.Count >= 2);
        Assert.All(chunks, c => Assert.True(c.Length <= 260)); // 分片在上限附近
        // 第一片在围栏内被截断 → 结尾闭合
        Assert.EndsWith("```", chunks[0].TrimEnd());
        // 后续片以重开的围栏开头
        Assert.StartsWith("```go", chunks[^1]);
        // 拼回后正文等价（去掉闭合/重开引入的成对 ```）
        var joined = string.Join("\n", chunks);
        Assert.Contains("line 59", joined);
        Assert.Contains("结尾文字", joined);
    }

    [Theory]
    [InlineData("oc_x", "chat_id")]
    [InlineData("ou_x", "open_id")]
    [InlineData("on_x", "union_id")]
    [InlineData("a@b.com", "email")]
    [InlineData("plainuser", "user_id")]
    public void DetectReceiveIdType_Should_Infer_By_Prefix(string id, string expected) =>
        Assert.Equal(expected, ChannelNormalize.DetectReceiveIdType(id));
}
