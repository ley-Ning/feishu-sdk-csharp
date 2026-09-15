using Feishu;
using Feishu.Events;
using Feishu.Services.Im;
using Feishu.Ws;

// 从环境变量读取凭证（FEISHU_APP_ID / FEISHU_APP_SECRET），
// 未配置时仅演示构建请求，不真正发网。
var appId = Environment.GetEnvironmentVariable("FEISHU_APP_ID") ?? "";
var appSecret = Environment.GetEnvironmentVariable("FEISHU_APP_SECRET") ?? "";

var options = new FeishuOptions
{
    AppId = appId,
    AppSecret = appSecret,
    Source = "sample",
};
var client = new FeishuClient(options);

// ---------- 1. 发送消息（tenant_access_token 自动管理） ----------
if (appId.Length > 0)
{
    var resp = await client.Im.Message.CreateAsync(new SendMessageRequest
    {
        ReceiveIdType = "open_id",
        Body = new SendMessageBody
        {
            ReceiveId = Environment.GetEnvironmentVariable("FEISHU_DEMO_RECEIVE_ID") ?? "ou_xxx",
            MsgType = "text",
            Content = """{"text":"hello from FeishuSdk C#"}""",
        },
    });
    Console.WriteLine(resp.Success
        ? $"sent ok, message_id: {resp.Data?.MessageId}"
        : $"send failed: code={resp.Code}, msg={resp.Msg}, requestId={resp.RequestId}");
}

// ---------- 2. 分页枚举（IAsyncEnumerable 自动翻页） ----------
if (appId.Length > 0)
{
    var userToken = Environment.GetEnvironmentVariable("FEISHU_USER_ACCESS_TOKEN");
    if (userToken != null)
    {
        await foreach (var chat in client.Im.Chat.EnumerateAsync(limit: 50, options: new RequestOptions().WithUserAccessToken(userToken)))
            Console.WriteLine($"chat: {chat.ChatId} {chat.Name}");
    }
}

// ---------- 2b. 卡片搭建 DSL + 交互消息 ----------
if (appId.Length > 0 && Environment.GetEnvironmentVariable("FEISHU_DEMO_RECEIVE_ID") != null)
{
    var card = new Feishu.Card.MessageCard
    {
        Config = new Feishu.Card.CardConfig { WideScreenMode = true },
        Header = new Feishu.Card.CardHeader
        {
            Template = Feishu.Card.CardTemplates.Blue,
            Title = new Feishu.Card.CardPlainText { Content = "FeishuSdk C# 上线" },
        },
        Elements =
        [
            new Feishu.Card.CardDiv
            {
                Text = new Feishu.Card.CardLarkMd { Content = "卡片 **DSL** 序列化测试" },
                Extra = new Feishu.Card.CardButton
                {
                    Text = new Feishu.Card.CardPlainText { Content = "点我" },
                    Type = Feishu.Card.CardButtonTypes.Primary,
                    Value = new Dictionary<string, object?> { ["action"] = "ping" },
                },
            },
            new Feishu.Card.CardHr(),
            new Feishu.Card.CardNote
            {
                Elements = [new Feishu.Card.CardPlainText { Content = "来自 feishu-sdk-csharp" }],
            },
        ],
    };
    var cardResp = await client.Im.Message.CreateAsync(new SendMessageRequest
    {
        ReceiveIdType = "open_id",
        Body = new SendMessageBody
        {
            ReceiveId = Environment.GetEnvironmentVariable("FEISHU_DEMO_RECEIVE_ID"),
            MsgType = "interactive",
            Content = card.ToJsonString(),
        },
    });
    Console.WriteLine(cardResp.Success ? "card sent" : $"card failed: {cardResp.Msg}");
}

// ---------- 2c. 通讯录：查用户 ----------
if (appId.Length > 0 && Environment.GetEnvironmentVariable("FEISHU_DEMO_USER_ID") != null)
{
    var user = await client.Contact.User.GetAsync(
        Environment.GetEnvironmentVariable("FEISHU_DEMO_USER_ID")!, userIdType: "open_id");
    Console.WriteLine(user.Success ? $"user: {user.Data?.Name} ({user.Data?.OpenId})" : $"user failed: {user.Msg}");
}

// ---------- 3. 事件分发器（webhook 或 WebSocket 共用） ----------
var dispatcher = new EventDispatcher(
        verificationToken: Environment.GetEnvironmentVariable("FEISHU_VERIFICATION_TOKEN"),
        encryptKey: Environment.GetEnvironmentVariable("FEISHU_ENCRYPT_KEY"))
    .Bind(client) // 内置处理 app_ticket 事件（ISV 需要）
    .On<P2MessageReceiveV1>(ImEventTypes.MessageReceiveV1, (e, ct) =>
    {
        var text = e.Message?.Content;
        Console.WriteLine($"收到消息 chat={e.Message?.ChatId} sender={e.Sender?.SenderId?.OpenId} content={text}");
        return Task.CompletedTask;
    });

// ---------- 4. WebSocket 长连接（免公网端点收事件） ----------
if (appId.Length > 0)
{
    var ws = new FeishuWsClient(appId, appSecret, new FeishuWsOptions
    {
        // Domain = FeishuOptions.LarkBaseUrl, // 国际版
    })
        .Bind(dispatcher);
    ws.OnReady += () => Console.WriteLine($"ws ready, connId: {ws.ConnId}");
    ws.OnError += ex => Console.Error.WriteLine($"ws error: {ex.Message}");

    await ws.StartAsync();
    Console.WriteLine("listening events via websocket, press Ctrl+C to exit");

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, _) => cts.Cancel();
    try
    {
        await Task.Delay(Timeout.Infinite, cts.Token);
    }
    catch (OperationCanceledException)
    {
    }
    await ws.ShutdownAsync();
}
else
{
    Console.WriteLine("未检测到 FEISHU_APP_ID / FEISHU_APP_SECRET，跳过真实调用。");
    Console.WriteLine("完整 ASP.NET Core 集成示例见 README（AddFeishu + MapFeishuEvents + AddFeishuWebSocket）。");
}
