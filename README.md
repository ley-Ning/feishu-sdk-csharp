# FeishuSdk（飞书开放平台 C# SDK）

飞书 / Lark 开放平台的非官方 C# SDK，**协议实现逐项对齐官方 [oapi-sdk-go v3](https://github.com/larksuite/oapi-sdk-go)**（MIT，官方目前只维护 Go / Java / Python / Node.js，无 C# 版本）。在忠实移植的基础上做了 C# 惯用法与并发层面的增强优化。

## 能力

| 模块 | 说明 |
|---|---|
| **API 调用管道** | token 类型裁决 → 参数校验 → URL 构建（路径参数/query/multipart）→ 发送 → 业务码预解码 → **token 失效自动驱逐缓存并重试**（对 Go 版的增强：Go 重试时不驱逐缓存，会带着同一枚过期 token 再打一次） |
| **token 三模式** | 自建应用 / 商店应用（ISV，app_ticket 流程）/ ClientAssertion（JWT Bearer，支持代理转发） |
| **事件分发** | URL 验证（challenge）、AES-256-CBC 解密、SHA256 验签、按 `header.event_type` 分发到强类型处理器、内置 app_ticket 事件 |
| **WebSocket 长连接** | bootstrap 换连接 URL、protobuf 二进制帧（**手写编解码，零依赖**）、ping/pong 心跳、分包重组、服务端下发配置、断线自动重连（首次抖动） |
| **卡片回调** | CardActionHandler：解密、challenge、SHA1 验签（timestamp+nonce+verificationToken+body）、四种返回形态（success/raw/自定义状态码/JSON） |
| **OAuth 用户令牌** | 授权码换取 / 刷新 user_access_token（/oauth/v3/token），支持 client_secret 与 ClientAssertion 双凭证及代理转发 |
| **强类型服务** | im v1、contact v3、authen v1（手写模板）+ bitable/drive/approval/task/docx/sheets/calendar/wiki/search/board/mail/docs/tenant/bot/application/translation/ocr/cardkit/moments/verification/acs/attendance/helpdesk/minutes/event-svc/okr/vc/admin/base/block/report/.../passport/spark/workplace/mdm/compensation/payroll/performance/unified_kms/aily/security_and_compliance/apaas/ehr/hire/corehr v1+v2 全量（`tools/FeishuSdk.CodeGen` 生成，992 端点，59 服务全部收口） |
| **Channel 高层编排** | 归一化/去重(LRU+TTL)/处理锁/策略门控/批量合并/长文分片(代码围栏感知)/降级重试/流式节流回复/机器人身份缓存/**SSRF 防护**/**音视频时长探测(OGG/MP4)** |
| **事件驱动总线** | `FeishuEventBus`：一切入站流量（WS/webhook/卡片）皆事件，同一根总线上强类型订阅/原始订阅/前缀通配（`im.message.*`）/全量订阅、handler 异常隔离、WS 生命周期事件、`IObservable` 事件流；`EventDispatcher` 与总线实现同一 `IEventHub` 契约，事件源可插拔 |
| **一键建应用** | scene/registration：二维码轮询注册新应用（Addons 三段编码、Lark 域名自动切换、slow_down 退避），拿到 ClientId/Secret |
| **ext 扩展** | DriveExplorer 建文件 + authen 便捷封装 |
| **IAsyncEnumerable 分页** | `await foreach` 自动翻页，支持 `limit` |
| **ASP.NET Core 集成** | `AddFeishu`（IHttpClientFactory + MEL + IDistributedCache 自动适配）、`MapFeishuEvents`（webhook 端点）、`AddFeishuWebSocket`（托管长连接） |

## 快速开始

```bash
dotnet build FeishuSdk.slnx
dotnet test tests/FeishuSdk.Tests   # 245 个用例
```

与 Go 版逐模块的一对一对照见 **[PARITY.md](PARITY.md)**。

### 事件驱动（推荐入口）

一切入站流量皆事件：WS 长连接、webhook、卡片回传汇入同一根总线，订阅、过滤、观察连接生命周期。

```csharp
using Feishu.EventBus;

var bus = new FeishuEventBus();
var ws = new FeishuWsClient(appId, appSecret);

// 强类型订阅：信封（event_id/source/时间）+ P2MessageReceiveV1 视图
bus.Subscribe<P2MessageReceiveV1>("im.message.receive_v1", (envelope, msg, ct) =>
{
    Console.WriteLine($"{envelope.EventId} 来自 {msg!.Sender?.SenderId?.OpenId}");
    return Task.CompletedTask;
});

// 前缀通配 + WS 生命周期 + 异常可观测
bus.SubscribePattern("im.message.*", (envelope, ct) => Task.CompletedTask);
using var bridge = bus.ObserveWsLifecycle(ws);   // WsReady/WsReconnecting/... → 总线
bus.OnHandlerError(e => Console.Error.WriteLine(e.Error));  // handler 异常不扩散，只在此可观测

// 流式消费（IObservable，Rx 兼容，不引入 Rx 依赖）
bus.AsObservable().Subscribe(e => Console.WriteLine(e.EventType));

ws.Bind(bus);   // EventDispatcher 与 FeishuEventBus 实现同一 IEventHub 契约，可互换
await ws.StartAsync();
```

`FeishuChannel` 同样面向 `IEventHub` 契约接线：绑定总线的 WS 客户端上，Channel 的事件订阅照常工作。

### 发消息

```csharp
using Feishu;
using Feishu.Services.Im;

var client = new FeishuClient(new FeishuOptions
{
    AppId = Environment.GetEnvironmentVariable("FEISHU_APP_ID")!,
    AppSecret = Environment.GetEnvironmentVariable("FEISHU_APP_SECRET")!,
});

var resp = await client.Im.Message.CreateAsync(new SendMessageRequest
{
    ReceiveIdType = "open_id",
    Body = new SendMessageBody
    {
        ReceiveId = "ou_xxx",
        MsgType = "text",
        Content = """{"text":"hello"}""",
    },
});

if (!resp.Success)
    Console.WriteLine($"code={resp.Code} msg={resp.Msg} requestId={resp.RequestId}");
```

设计取舍：**API 调用不抛业务异常**（与 Go 版一致），用 `resp.Success` / `resp.EnsureSuccess()` 判断；基础设施错误（参数缺失、token 获取失败、504 网关超时）以异常抛出。

### 接收事件（WebSocket 长连接，免公网端点）

```csharp
using Feishu.Events;
using Feishu.Services.Im;
using Feishu.Ws;

var dispatcher = new EventDispatcher("verificationToken", "encryptKey")
    .On<P2MessageReceiveV1>(ImEventTypes.MessageReceiveV1, (e, ct) =>
    {
        Console.WriteLine($"{e.Message?.ChatId}: {e.Message?.Content}");
        return Task.CompletedTask;
    });

var ws = new FeishuWsClient(appId, appSecret).Bind(dispatcher);
ws.OnReady += () => Console.WriteLine("ready");
await ws.StartAsync();
await ws.WaitUntilShutdownAsync(cts.Token);
```

### ASP.NET Core

```csharp
builder.Services.AddFeishu(o =>
{
    o.AppId = config["Feishu:AppId"]!;
    o.AppSecret = config["Feishu:AppSecret"]!;
    // o.AppType = FeishuAppType.Marketplace;      // 商店应用
    // o.TokenCache = new RedisCacheFeishuAdapter(…); // 多实例共享 token
});

var app = builder.Build();
app.MapFeishuEvents("/feishu/events", dispatcher); // webhook 方式
// 或长连接方式：builder.Services.AddFeishuWebSocket();
```

## 解决方案结构

```
src/FeishuSdk/               主库（零第三方依赖，net8.0）
├── FeishuClient.cs          主入口 + raw API（Post/Get/Put/Patch/Delete/Do）
├── FeishuOptions.cs         配置（凭证/域名/缓存/日志/序列化/断言器）
├── Core/
│   ├── ApiRequest.cs        请求描述 + QueryParams + MultipartRequestBody + RequestOptions
│   ├── RequestPipeline.cs   ★ 请求管道（token 裁决/校验/构建/重试）
│   ├── TokenManager.cs      ★ token 三模式 + 单飞缓存 + AppTicketManager
├── Events/                  ★ EventDispatcher + EventCrypto（AES/SHA256）
├── Ws/                      ★ FeishuWsClient + WsFrame（手写 protobuf）+ FrameReassembler
├── Services/Im|Contact|Authen/  手写模板服务
├── Services/{Bitable,Drive,Approval,Task,Docx}/  生成器产出的服务（.g.cs）
├── Channel/                 ★ 高层机器人编排（对齐 Go channel/）
├── Cache/                   IFeishuCache + MemoryFeishuCache + SingleFlight
└── Serialization/           IFeishuSerializer（System.Text.Json, snake_case）
src/FeishuSdk.AspNetCore/    DI / webhook 端点 / WebSocket 托管服务 / MEL·分布式缓存适配器
tests/FeishuSdk.Tests/       233 个单元测试（含本地 mock 真机链路 E2E）（管道/错误分类/token 三模式/单飞/ClientAssertion/OAuth/事件/卡片回调+DSL/contact/authen/Channel 编排/生成服务/WS 帧与分包/分页/序列化/端点）
samples/FeishuSdk.Sample/    控制台示例
```

## 与 Go 版的对照 & 关键优化

| Go 版（oapi-sdk-go v3） | 本 SDK | 优化点 |
|---|---|---|
| `context.Context` | `CancellationToken` 贯穿全链路 | — |
| token 缓存 miss 并发各自请求 | **SingleFlight 按 key 单飞** | 防惊群 |
| token 失效重试不驱逐缓存 | **失效先驱逐再重试** | 修复语义缺陷 |
| `Iterator.Next()` 手动分页 | `IAsyncEnumerable<T>` + `[EnumeratorCancellation]` | 语言级异步流 |
| 每 DTO 生成 XXXBuilder（数百行） | 可空属性 + 对象初始化器 + `required` | 生成代码量减半以上，编译期强约束 |
| gorilla/websocket + gogo/protobuf | BCL `ClientWebSocket` + **手写 protobuf 帧** | 零第三方依赖 |
| `Logger`/`Cache`/`HttpClient` 自定义接口 | 同为接口，且 AspNetCore 包自动适配 MEL / IDistributedCache / IHttpClientFactory | DI 原生 |
| `select {}` 阻塞 Start | `StartAsync`（首连完成即返回）+ `WaitUntilShutdownAsync` | 可组合托管 |
| 重连递归/全局随机 | 循环重连、`Random.Shared`、volatile 配置快照 | 无栈增长 |

协议对齐清单（与 Go 源码逐项核对）：

- token 端点路径、错误码（99991663/99991664/99991671/10012）、提前 3 分钟过期缓冲
- 事件：`{"encrypt":…}` AES-256-CBC（key=SHA256(encryptKey)，IV=密文前 16B，按 `{}`裁剪）、签名 `hex(sha256(timestamp+nonce+key+body))`、challenge 响应 `{"challenge":"…"}`、业务回执 `{"msg":"success"}`
- 长连接：bootstrap `POST /callback/ws/endpoint`（PascalCase 字段）、pbbp2 Frame 字段号 1-9、控制帧 `method=0`+`type=ping/pong`、数据帧 `sum/seq/message_id` 分包、pong 载荷下发 ClientConfig、回执 `{"code":200|500}`

## 尚未覆盖（路线图）

- [x] ~~代码生成器~~（已落地：apis.json 清单范式 + 5 服务示范，剩余为清单数据补录）
- [x] ~~Channel 高层编排~~（已对齐，除 ssrf_guard 与音视频时长解析）
- [x] ~~截断区资源补录~~（机读提取全量收口：curl 源码 + 正则解析，565 端点一次性导入）
- [ ] 一键应用注册（scene/registration）
- [ ] 真实飞书服务端端到端联调（协议层已按 Go 源码逐项对齐并有向量测试，见 PARITY.md 第 10 节）

## License

MIT。协议实现参考 [larksuite/oapi-sdk-go](https://github.com/larksuite/oapi-sdk-go)（MIT License, Copyright (c) 2022 Lark Technologies Pte. Ltd.）。
