using System.Text;
using System.Text.Json;
using Feishu;
using Feishu.Card;
using Feishu.Channel;
using Feishu.Events;
using Feishu.Services.Im;
using Feishu.Ws;

namespace FeishuSdk.Tests;

/// <summary>
/// 本地 mock 真机链路 E2E：真实 HttpClient/ClientWebSocket 网络栈 + 本地 mock 服务器。
/// 与真实 E2E 的唯一区别是 BaseUrl 指向本地——凭证到位后改 URL 即为真机验证。
/// 覆盖五项：发消息 / 卡片 DSL / 查用户 / Channel 流式回复 / WS 长连接收事件。
/// </summary>
public class LiveMockE2ETests
{
    [Fact]
    public async Task Full_E2E_SendMessage_Card_QueryUser_ChannelStream()
    {
        using var server = new MockFeishuServer();
        var client = new FeishuClient(new FeishuOptions
        {
            AppId = "mock_app",
            AppSecret = "mock_secret",
            BaseUrl = server.BaseUrl, // 唯一差异点：真机时是 https://open.feishu.cn
            Logger = NullFeishuLogger.Instance,
        });

        // ===== 1. 发消息（真实 socket + token 自动获取 + JSON 序列化）=====
        var sendResp = await client.Im.Message.CreateAsync(new SendMessageRequest
        {
            ReceiveIdType = "open_id",
            Body = new SendMessageBody
            {
                ReceiveId = "ou_mock_user",
                MsgType = "text",
                Content = SystemTextJsonFeishuSerializer.Instance.Serialize(new { text = "E2E 测试消息" }),
            },
        });
        Assert.True(sendResp.Success, $"send failed: {sendResp.Msg}");
        Assert.Equal("om_mock_1", sendResp.Data?.MessageId);
        Assert.Contains("/open-apis/auth/v3/tenant_access_token/internal", server.ReceivedPaths);
        Assert.Contains("/open-apis/im/v1/messages?receive_id_type=open_id", server.ReceivedPaths);
        Assert.Contains("E2E 测试消息", server.LastSentText);

        // ===== 2. 卡片 DSL（序列化 + 发送）=====
        var card = new MessageCard
        {
            Header = new CardHeader { Template = CardTemplates.Blue, Title = new CardPlainText { Content = "E2E 卡片" } },
            Elements =
            [
                new CardDiv { Text = new CardLarkMd { Content = "**本地链路**验证" } },
                new CardActionBlock
                {
                    Actions = [new CardButton { Text = new CardPlainText { Content = "点击" }, Value = new Dictionary<string, object?> { ["action"] = "e2e" } }],
                },
            ],
        };
        var cardResp = await client.Im.Message.CreateAsync(new SendMessageRequest
        {
            ReceiveIdType = "open_id",
            Body = new SendMessageBody { ReceiveId = "ou_mock_user", MsgType = "interactive", Content = card.ToJsonString() },
        });
        Assert.True(cardResp.Success);
        Assert.Contains("E2E 卡片", server.LastSentCard);
        Assert.Contains("button", server.LastSentCard);

        // ===== 3. 查用户 =====
        var userResp = await client.Contact.User.GetAsync("ou_mock_1", userIdType: "open_id");
        Assert.True(userResp.Success);
        Assert.Equal("Mock用户", userResp.Data?.Name);

        // ===== 4. Channel 流式回复（节流 PATCH + markdown→post）=====
        var channel = new FeishuChannel(client, null, cfg =>
        {
            cfg.StreamThrottle = TimeSpan.FromMilliseconds(1);
            cfg.TextChunkLimit = 200;
        });
        var stream = await channel.StreamAsync(new ChannelSendInput { UserId = "ou_mock_user", Markdown = "流式开头" });
        await stream.AppendAsync(" 中段");
        await stream.CloseAsync();

        var patchPaths = server.ReceivedPaths.Where(p => p.StartsWith("/open-apis/im/v1/messages/om_mock_1")).ToList();
        Assert.NotEmpty(patchPaths);
        Assert.Contains("流式开头", server.LastSentText);

        // ===== 5. WS 长连接收事件（bootstrap → 真实 WebSocket 建连 → pbbp2 事件帧 → 分发器回调 → 回执 200）=====
        var wsReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new EventDispatcher()
            .OnRaw("im.message.receive_v1", (raw, _) =>
            {
                wsReceived.TrySetResult(Encoding.UTF8.GetString(raw));
                return Task.CompletedTask;
            });
        await using var wsClient = new FeishuWsClient("mock_app", "mock_secret", new FeishuWsOptions
        {
            Domain = server.BaseUrl, // bootstrap 端点；返回的 WS URL 由 mock 服务端签发
            Logger = NullFeishuLogger.Instance,
        });
        wsClient.Bind(dispatcher);
        await wsClient.StartAsync(); // 真机时唯一差异：Domain 换回 open.feishu.cn
        await server.WaitForWsClientAsync(TimeSpan.FromSeconds(5));
        await server.PushMessageEventAsync("WS E2E 事件");

        var receivedJson = await wsReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("im.message.receive_v1", receivedJson);
        Assert.Contains("WS E2E 事件", receivedJson);
        Assert.Contains("/callback/ws/endpoint", server.ReceivedPaths); // bootstrap 走了真实 HTTP

        // SDK 收事件后必须回执 code=200（对齐 Go handleDataFrame；回执在回调返回后才发，需轮询）
        var ackDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        var ackSeen = false;
        while (DateTime.UtcNow < ackDeadline && !ackSeen)
        {
            List<byte[]> frames;
            lock (server.ReceivedWsFrames) frames = [.. server.ReceivedWsFrames];
            foreach (var f in frames)
            {
                if (Encoding.UTF8.GetString(f).Contains("\"code\":200")) { ackSeen = true; break; }
            }
            if (!ackSeen) await Task.Delay(50);
        }
        Assert.True(ackSeen, "SDK 未回执 code=200");
        await wsClient.ShutdownAsync();
    }
}
