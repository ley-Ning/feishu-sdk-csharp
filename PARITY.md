# 与 oapi-sdk-go v3 的一对一功能对照矩阵

逐模块对照 [larksuite/oapi-sdk-go](https://github.com/larksuite/oapi-sdk-go)（master@2026-09），标注对应实现与测试证据。
测试证据均为 `tests/FeishuSdk.Tests` 中的实际用例（85 个全绿）。

## 1. core（基础设施层）

| Go（larkcore） | C#（FeishuSdk） | 状态 | 测试证据 |
|---|---|---|---|
| `Config` + `ClientOptionFunc`（WithMarketplaceApp/WithEnableTokenCache/WithTokenCache/WithLogger/WithOpenBaseUrl/WithReqTimeout/WithHeaders/WithSource/WithHttpClient/WithHelpdeskCredential/WithSerialization/WithLogReqAtDebug/WithClientAssertionProvider/WithOAuthBaseUrl/WithAppType/WithLogLevel） | `FeishuOptions`（全部同名能力以属性暴露；Feishu/Lark 双域名 `UseLarkDomain`） | ✅ | `PipelineDeepTests.Lark_Domain_Should_Be_Used` 等 |
| `Request()` 管道（determineTokenType → validate → translate → doSend → 预解码 → 失效重试×2） | `RequestPipeline.SendAsync` 同构五段 | ✅ | `RequestPipelineTests`（5 例） |
| `determineTokenType`（优先级树：手动 token → tenant → app；ClientAssertion 只允许 tenant/user） | `DetermineTokenType` 同优先级树 | ✅ | `RequestPipelineTests.UserAccessToken_...`、`ClientAssertionAndOAuthTests` |
| `validateTokenType`（单类型 API 拒绝不匹配的手动 token） | `ValidateTokenType` | ✅ | `PipelineDeepTests.TenantOnly/UserOnly_Api_Should_Reject_*` |
| `validate`（AppId/Secret 空、市场应用缺 tenant_key、user token 空、保留 header、禁缓存需手动 token） | `Validate` 全分支 | ✅ | `PipelineDeepTests`（4 例） |
| token 失效重试（99991663/99991664/99991671 循环一次） | 同错误码循环一次，**增强：失效先驱逐缓存**（Go 重试仍带旧 token） | ✅+ | `RequestPipelineTests.SendAsync_Should_Invalidate_Token_And_Retry...` |
| `errCodeAppTicketInvalid`(10012) → `applyAppTicket` 重推 | 同：预解码命中即调 Resend | ✅ | `PipelineDeepTests.AppTicketInvalid_Code_Should_Trigger_Resend` |
| `doSend` 错误分类：`ClientTimeoutError`（不重试）/`DialFailedError`（重试一轮）/504 `ServerTimeoutError` | `FeishuClientTimeoutException` / `FeishuDialFailedException` / `FeishuServerTimeoutException`（重试语义一致） | ✅ | `PipelineDeepTests`（3 例）+ `RequestPipelineTests.ServerTimeout_504_...` |
| 文件下载/非 JSON 响应透传（`FileDownload` + content-type 判定） | 同 | ✅ | `RequestPipelineTests.SendAsync_Should_Passthrough_NonJson_Download` |
| `ReqTranslator`：`:param` 路径替换 + URL 转义 + query 编码 + multipart | `BuildUrl` + `MultipartRequestBody`（name 带引号，输出与 Go 一致） | ✅ | `RequestPipelineTests.BuildUrl_...`、`PipelineDeepTests.Multipart_...` |
| `Formdata`（AddField/AddFile/AddFileByPath） | `MultipartRequestBody` 同三法 | ✅ | 同上 |
| `TokenManager`：自建 app/tenant、商店 app/tenant（app_ticket 流程）、缓存 TTL=expire−3min | 同 + **SingleFlight 单飞**（防并发惊群） | ✅+ | `TokenManagerTests`（3 例）+ `PipelineDeepTests.Token_Cache_Ttl_...` |
| `ClientAssertionProvider`（JWT → /oauth/v3/token；TargetService 代理 + X-Target-Service 头；aud=host） | `IClientAssertionProvider` 同协议 | ✅ | `ClientAssertionAndOAuthTests`（4 例） |
| `AppTicketManager`（Get 触发 resend / Set） | 同 | ✅ | `EventDispatcherDeepTests.Bind_AppTicket_...`（端到端） |
| 商店应用 NewClient 自动重推 app_ticket | `FeishuClient` 构造触发 | ✅ | 行为对齐（代码 `FeishuClient` ctor） |
| 帮助台鉴权（X-Lark-Helpdesk-Authorization = base64(id:token)） | 同 | ✅ | `PipelineDeepTests.HelpdeskAuth_*`（2 例） |
| `ApiResp`（StatusCode/Header/RawBody/RequestId/JSONUnmarshalBody） | `ApiResponse` + `FeishuResponse.Raw` | ✅ | 各 API 测试断言 `RequestId` |
| `CodeError`（code/msg/error{log_id,details,violations}） | `CodeError` 全字段 | ✅ | `SerializationTests.Deserialize_CodeError_...` |
| `Logger` / `Cache` / `Serializable` / `HttpClient` 接口 | `IFeishuLogger` / `IFeishuCache` / `IFeishuSerializer` / HttpClient 注入 | ✅ | `MemoryFeishuCacheTests`（2 例）+ AspNetCore 适配器 |
| `LogReqAtDebug`（脱敏打印请求） | `LogRequestAtDebug`（Authorization/X-Lark-Helpdesk-Authorization 脱敏） | ✅ | 实现 `LogRequest` |

## 2. core/accesstoken（OAuth 用户令牌）

| Go | C# | 状态 | 测试 |
|---|---|---|---|
| `RetrieveByAuthorizationCode`（grant_type=authorization_code + code/redirect_uri/code_verifier/scope） | `OAuth.ExchangeByAuthorizationCodeAsync` | ✅ | `OAuth_ExchangeByCode_...`（含请求体逐字段断言） |
| `Refresh`（grant_type=refresh_token） | `OAuth.RefreshAsync` | ✅ | `OAuth_Refresh_...` |
| client_secret / client_assertion 双凭证；TargetInfo 代理改写 URL+头 | 同 | ✅ | `ClientAssertion_*`（代理用例） |
| `AccessTokenError`（status≠200 / error 非空 / token 空） | `OAuthTokenException` | ✅ | `OAuth_Error_...`、`OAuth_NoCredential_...`(7104) |
| 响应解析（access_token/expires_in/refresh_token/refresh_token_expires_in/scope） | `OAuthTokenResult` | ✅ | `OAuth_ExchangeByCode_...` |

## 3. event（事件）

| Go | C# | 状态 | 测试 |
|---|---|---|---|
| `EventDispatcher`（map[eventType]handler + callback map） | `EventDispatcher.On/OnRaw/OnCallback` | ✅ | `EventDispatcherDeepTests`（多处理器/回调/裸字节） |
| URL challenge（type=url_verification + token 校验 + `{"challenge":...}`） | 同 | ✅ | `EventDispatcherTests.Challenge_*`（2 例） |
| `EventDecrypt`（base64 → AES-256-CBC, key=SHA256(key), IV=前16B, `{}`裁剪） | `EventCrypto.Decrypt/Encrypt` | ✅ | `EventCryptoTests.Decrypt_...`（回环） |
| `Signature`（hex sha256(ts+nonce+key+body)） | `EventCrypto.Signature`（+固定时间比较） | ✅ | `Signature_Should_Match_Reference_Vector`（预计算向量） |
| 验签开关：未配 EncryptKey 不验；`SkipSignVerify` 跳过 | 同 | ✅ | `SkipSignVerify_...`、`Bad_Signature_...` |
| v1 事件类型取 `event.type`；v2 取 `header.event_type` | 同 | ✅ | `V1_Event_Type_...` |
| `defaultHandler`（自定义回调）、`EventHandlerModel.RawReq` | `OnRaw`；`CardAction.RawRequest` | ✅ | `V1_Event_...` |
| app_ticket 事件内置处理（写缓存） | `Bind(FeishuClient)` | ✅ | `Bind_AppTicket_Should_Feed_Marketplace_Token_Flow`（端到端） |
| 回调处理器返回值作为响应体 | `OnCallback<TEvent,TResult>` | ✅ | `OnCallback_Result_...` |
| 未注册事件：HTTP 200 + 日志（Go DoHandle 行为） | 同 | ✅ | `Unregistered_Event_...` |
| 处理器异常：500（触发重推） | 同 | ✅ | `Handler_Exception_...` |

## 4. card（卡片回调）

| Go | C# | 状态 | 测试 |
|---|---|---|---|
| `NewCardActionHandler(token, key, handler)` | `CardActionHandler(token, key, handler)` | ✅ | `CardActionHandlerTests`（8 例） |
| 解密（encrypt → EncryptKey，缺 key 报错）/ 明文透传 | 同 | ✅ | `Encrypted_Action_...` |
| challenge（token 匹配 → 回显） | 同 | ✅ | `Challenge_...`（成败 2 例） |
| `VerifySign`：**SHA1**(ts+nonce+**verificationToken**+body)，未配 token 不验 | `CardSignature.Signature` | ✅ | `Signature_Should_Match_Reference_Vector`（SHA1 向量）+ 签名用例 |
| 结果序列化：nil→`{"msg":"success"}`；string→原文；`CustomResp{StatusCode,Body}`→自定义状态码；其他→JSON | `CardCustomResponse` / `CardToast` / string / object 四形态 | ✅ | `Handler_Returning_*`（3 例） |
| `CardAction` 模型（open_id/user_id/open_message_id/open_chat_id/tenant_key/token/timezone/challenge/type/action{value,tag,option,timezone,name,form_value,input_value,options,checked}） | `CardAction`/`CardActionData` 全字段 | ✅ | `Signed_Action_...` |
| `SkipSignVerify` | 同名属性 | ✅ | `SkipSignVerify_...` |
| 卡片搭建 DSL（MessageCard/Config/Header/17 种元素：hr/markdown/div/note/img/button/overflow/select_static/select_person/date_picker/picker_time/picker_datetime/action/plain_text/lark_md/URL/Field/Confirm + 模板/按钮/布局/图片模式常量） | `Feishu.Card` DTO 家族（元素自带 tag 序列化，null 省略，可空属性替代 Go Builder） | ✅ | `CardDslTests.Full_Card_Should_Serialize_To_Go_Equivalent_Json`（与 Go 等价 JSON 深度对比）+ Picker/SelectStatic 2 例 |

## 5. ws（长连接）

| Go | C# | 状态 | 测试 |
|---|---|---|---|
| bootstrap：POST `{domain}/callback/ws/endpoint`，体 `{"AppID","AppSecret"}`（PascalCase），头 locale=zh + UA | `GetConnUrlAsync` 同体同头 | ✅ | `Bootstrap_Should_Send_PascalCase_Body_And_Headers` |
| bootstrap 错误分类（code=1 busy → 服务端错；403/514 → 客户端致命；HTTP≠200 取 msg） | 同（`FeishuWsFatalException.Code`） | ✅ | `Bootstrap_Forbidden/HttpError/NoCredential_...` |
| bootstrap 支持 ClientAssertion（含 TargetInfo 代理） | 同 | ✅ | 代码路径与 OAuth 共用逻辑；`Bootstrap_NoCredential`（7104） |
| pbbp2 Frame protobuf（字段号 1-9；gogo generated） | `WsFrame` 手写编解码（varint/length-delimited/未知字段跳过） | ✅ | `WsFrameTests`（字节级向量 + 回环 + 未知字段） |
| ping 帧（method=0 + headers[type=ping] + service=id） | `BuildPingFrame` | ✅ | `PingFrame_Should_Be_Control_...` |
| pong 载荷 ClientConfig 应用（ReconnectCount/Interval/Nonce/PingInterval） | `HandleControlFrame`→`ApplyServerConfig` | ✅ | `Pong_Config_Should_Be_Applied` |
| 数据帧：sum/seq/message_id 分包重组（5s 缓存） | `FrameReassembler`（TTL 清扫） | ✅ | `WsDeepTests.Data_Frame_Split_...`、`FrameReassemblerTests`（2 例） |
| 事件分发 → 回执 `{"code":200}` + biz_rt 头 | 同 | ✅ | `Data_Event_Frame_Should_Ack_200_With_BizRt` |
| 处理器错误 → 回执 `{"code":500}` | 同 | ✅ | `Data_Event_Handler_Error_Should_Ack_500` |
| 卡片帧：Go 直接 return（不回执） | 同（忽略） | ✅ | `Data_Card_Frame_Should_Be_Ignored_Like_Go` |
| 断线重连：autoReconnect、首次抖动(0~nonce s)、间隔、次数上限、ClientError 不重连 | 同策略（`ReconnectAsync` 循环实现，无递归栈增长） | ✅ | 逻辑级对齐；重连全流程需真实服务端（见"待真联调"） |
| `Start()` 启动全链路：bootstrap → 拨号 → 循环 | `StartAsync`（两段式门闩，修复跨 `await` 持有 `_stateGate` 与 `ConnectAsync` 内部取门闩的异步信号量自死锁——任何生产调用必挂起，E2E 首次穿透即抓到） | ✅ | `LiveMockE2ETests.Full_E2E_...`（真实 WebSocket 建连 + 事件分发 + 回执 200） |
| 生命周期回调 OnReady/OnError/OnReconnecting/OnReconnected/OnDisconnected | 同名事件 | ✅ | `FeishuWsClient` 事件 |
| Go 版 `WithCardHandler` 在 ws 中被注释禁用 | C# 同样未启用 | ✅ | 行为一致 |

## 5.5 channel（高层机器人编排，Go 2024-2026 新增的大模块）

| Go（channel/） | C#（Feishu.Channel） | 状态 | 测试 |
|---|---|---|---|
| `types.Channel` 接口 + `channelImpl` 编排 | `FeishuChannel`（消息/回复/评论/进群/卡片行为/拒绝 六类处理器 + 生命周期） | ✅ | `ChannelOrchestrationTests`（12 例） |
| `normalize.ParseMessage`（信封→归一化：event_id/create_ms/mentions/@_all/资源） | `ChannelNormalize.ParseMessage` | ✅ | `ChannelNormalizeTests.ParseMessage_...` |
| `normalize.ParseContent`（text/image/file/folder/audio/media/video/sticker/share_chat/share_user/post(content_v2)/interactive/merge_forward/未知兜底） | `ParseContent` 同类型集（post 含 md/text/a/at/img/media/code_block/hr 元素） | ✅ | ParseContent 6 例 + Post 渲染 |
| `SimpleMarkdownToPost`（md 标签包装 zh_cn 信封 + 提及 at 元素） | `SimpleMarkdownToPost` | ✅ | `SimpleMarkdownToPost_...` |
| `ComposeMentionsTextPrefix / ComposePostMentionElements` | 同（前缀空格连接） | ✅ | `ComposeMentionsTextPrefix_...` |
| `ParseReaction/ParseComment/ParseBotAdded/ParseCardAction`（含 notice_meta 回退、字段缺失判 nil） | 同（`ParseReaction/ParseComment/ParseBotAdded/ParseCardAction`） | ✅ | 归一化测试 + dedup key 用例 |
| `safety.DedupCache`（LRU+TTL，容量逐出） | `DedupCache` | ✅ | `DedupCache_*`（2 例，逐出链精确断言） |
| `safety.ProcessingLock`（短 TTL 处理锁） | `ProcessingLock` | ✅ | `ProcessingLock_...` |
| `safety.IsStale`（过期消息，零值不判过期） | `StaleDetector.IsStale` | ✅ | `StaleDetector_...` |
| `safety.PolicyGate`（群白名单→必须@→@all 拦截；私信 open/disabled/allowlist；UpdateConfig） | `PolicyGate`（含动态 UpdatePolicy/GetPolicy） | ✅ | PolicyGate 4 例（含双白名单） |
| `pipeline.ChatPipeline`（去抖/批量上限/长文长延迟/合并 \n\n/提及资源去重/SourceIDs）+ Manager | `ChatPipeline` + `ChatPipelineManager`（Channel 串行队列） | ✅ | 批量合并 + 作用域串行 2 例 |
| `send.go Send`（receive_id 前缀推断、内容优先级 image>audio>video>file>card>post>share>sticker>markdown分片>text分片、提及前缀、本地文件自动上传） | `SendAsync` 同优先级同分片 | ✅ | Send 3 例（文本/分片/提及） |
| `sendOneWithFallback`（230011/17/20/40 目标失效→去 reply 重发；230001 格式错→降级 text） | `SendOneWithFallbackAsync` 同码表 | ✅ | 回复降级 + 格式降级 2 例 |
| `outbound.Retry`（仅 RateLimited/Unknown 重试，指数退避） | `RawSendWithRetryAsync`（分类后仅限频/未知重试） | ✅ | 降级用例断言无多余重试 |
| `outbound.SplitWithCodeFences`（围栏闭合重开、标题断行） | `SplitWithCodeFences`（GeneratedRegex） | ✅ | `SplitWithCodeFences_...` |
| `outbound.DetectReceiveIdType`（oc_/ou_/on_/email/user_id） | `DetectReceiveIdType` | ✅ | 5 前缀 Theory |
| `outbound.Uploader`（image_path/file_path 自动上传换 key） | `UploadImageAsync/UploadFileAsync`（multipart） | ✅ | 通道内实现（端到端上传走真机待验） |
| `stream.go MarkdownStreamController`（节流 Append、超限新分片走回复、PATCH 更新） | `MarkdownStreamController` + `ThrottleController`（500ms 默认节流） | ✅ | `Stream_Markdown_...`（PATCH 累积 + 新分片回复） |
| `card_stream.go CardStreamController`（换卡 PATCH） | `CardStreamController` | ✅ | `Stream_Card_...` |
| `GetBotIdentity`（bot/v3/info + TTL 缓存 + 失败节流 + 旧值兜底） | `GetBotIdentityAsync` | ✅ | 缓存单次拉取 + 自发消息过滤 |
| 自发消息回环保护 / MentionedBot 标记 / 拒绝事件分发 | 同 | ✅ | `Inbound_SelfMessage/Group_Without_Mention/Dm_Dedup/Burst` 4 例 |
| `channel.CardActionEvent` 归一 + 去重键（EventID 或 message+operator+action 组合） | `ChannelCardActionEvent` + `CardActionDedupKey` | ✅ | 归一化/键构造（WS 卡片回调上游与 Go 同样未启用） |
| `ssrf_guard.go`（上传 URL 出网防护：14 段 IPv4 CIDR 黑名单 + IPv6 环回/链路本地/fc00::/7/组播/IPv4 映射地址，Allowlist 直通） | `SsrfGuard.AssertPublicUrlAsync`（接入 `UploadMediaAsync` 的 URL 来源） | ✅ | `SsrfGuardTests`（26 断言：私网/保留/映射/白名单/协议） |
| `duration_ogg.go`（尾部 64KB 反扫 OggS 页取 granule/48） | `MediaDuration.ParseOpusDuration` | ✅ | `MediaDurationTests.Opus_*`（构造页 + 尾页命中） |
| `duration_mp4.go`（ISO BMFF box 遍历 moov→mvhd，v0/v1 timescale） | `MediaDuration.ParseMp4Duration` | ✅ | `MediaDurationTests.Mp4_*`（v0/v1 合成容器 + 缺 moov 报错） |
| `outbound.Uploader.UploadMedia`（URL/路径/字节三来源） | `FeishuChannel.UploadMediaAsync`（SSRF 前置 + 音视频时长探测） | ✅ | SSRF/时长用例 + 上传路径走 `UploadBytesAsync` |

## 5.7 ext（扩展服务）

| Go（service/ext） | C#（Services.Ext） | 状态 | 测试 |
|---|---|---|---|
| `ExtService.DriveExplorer.CreateFile`（POST /open-apis/drive/explorer/v2/file/:folderToken，tenant+user） | `Ext.DriveExplorer.CreateFileAsync` | ✅ | `ExtServiceTests.DriveExplorer_...` |
| `ExtService.Authen.AuthenAccessToken/RefreshAuthenAccessToken/AuthenUserInfo`（三端点，App/User token） | `Ext.Authen.*` | ✅ | `Ext_Authen_Endpoints_...` |

## 5.8 scene/registration（一键建应用）

| Go（scene/registration） | C#（Feishu.Scene.AppRegistration） | 状态 | 测试 |
|---|---|---|---|
| begin（form：action=begin&archetype=PersonalAgent&auth_method=client_secret&request_user_info=open_id，POST /oauth/v1/app/registration） | 同 | ✅ | `RegisterApp_Happy_Path_...`（含表单断言） |
| 二维码 URL 组装（from=sdk&tp=sdk&source=sdk[/src]、avatar×1-6、name/desc、createOnly、clientID；Set 覆盖原参数） | `BuildQrCodeUrl` 同语义 | ✅ | `QrUrl_Should_Carry_...` |
| Addons 三段编码（规范化→gzip→base64url；三值 Preset；空项校验；空增量需 Preset=false） | `EncodeAddons` 同规则 | ✅ | `Addons_Encoding_...`（解码回 JSON 深验）+ 2 个拒绝用例 |
| 轮询状态机（interval/slow_down +5、authorization_pending、access_denied/expired_token 抛错、空错误继续轮询） | 同 | ✅ | Happy Path（pending→slow_down→凭证）+ `Access_Denied_...` |
| tenant_brand==lark → 自动切换国际版域名 + domain_switched 状态 | 同 | ✅ | `RegisterApp_Lark_Brand_...`（断言第三次请求打到 larksuite.com） |
| Options.OnQRCode 必填 | 同（ArgumentException） | ✅ | `Missing_OnQrCode_...` |

## 6. httpserverext / 适配层

| Go | C# | 状态 | 测试 |
|---|---|---|---|
| `httpserverext`（各框架把 HTTP 请求转 EventReq/EventResp） | `FeishuWebhookEndpoint.HandleAsync(HttpContext, IWebhookHandler)` + `MapFeishuEvents` / `MapFeishuWebhook` | ✅ | `AspNetCoreEndpointTests`（3 例，含卡片 handler 接入） |
| —（Go 无等价） | `AddFeishu`（IHttpClientFactory + MEL + IDistributedCache 自动装配）、`AddFeishuWebSocket`（IHostedService） | ✅+ | DI 装配代码 + 适配器 |

## 7. 服务模块（service/*）

| Go | C# | 状态 | 说明 |
|---|---|---|---|
| im/v1 消息（send/reply/get/patch/recall） | `Im.Message.*` | ✅ | `RequestPipelineTests`（3 例覆盖 send/retry/download） |
| im/v1 群（create/get/list） | `Im.Chat.*`（含 `IAsyncEnumerable` 分页 + limit） | ✅ | `PaginationTests`（2 例，自动翻页/停止/limit） |
| im/v1 事件（P2MessageReceiveV1 等） | `P2MessageReceiveV1` + `ImEventTypes` | ✅ | `WsDispatch_Should_Deserialize_Envelope_Event`（含中文 content 反序列化） |
| contact/v3 用户（get/create/patch/delete/batch_get_id） | `Contact.User.*`（高频子集，路径/方法/token 类型对齐） | ✅ | `Contact_User_Get/...`、`Contact_BatchGetId_...`、`Contact_User_Patch_And_Delete_...` |
| contact/v3 部门（get/children 分页） | `Contact.Department.*`（含 EnumerateChildrenAsync 自动翻页） | ✅ | `Contact_Department_Children_Should_Auto_Page` |
| authen/v1（access_token / oidc.access_token / oidc.refresh_access_token / refresh_access_token / user_info，五端点 token 类型 App/User） | `Authen.*`（v1 扁平响应模型；官方建议新集成走 client.OAuth） | ✅ | `Authen_Oidc_...`、`Authen_UserInfo_...`、`Authen_Legacy_And_Refresh_...` |
| **代码生成器**（Go oapi-sdk-gen 生成全部服务的等价能力） | `tools/FeishuSdk.CodeGen`（apis.json 清单 → 服务代码，#nullable、下载端点原始响应、分页枚举） | ✅ | `CodeGen_Should_Generate_Deterministic_Files` |
| sheets/v3（spreadsheets 3 端点） | `client.Sheets.*`（生成） | ✅ | `GeneratedServiceWave2Tests.Sheets_...` |
| calendar/v4（calendars/acls/events 11 端点，分页） | `client.Calendar.*`（生成） | ✅ | `Calendar_CreateEvent_...` |
| wiki/v2（spaces/nodes 6 端点，分页） | `client.Wiki.*`（生成） | ✅ | `Wiki_ListSpaces_...`（自动翻页） |
| suite/docs-api 搜索（1 端点） | `client.Search.*`（生成） | ✅ | `Search_DocsSearch_...` |
| board/v1（白板 3 端点） | `client.Board.*`（生成） | ✅ | `GeneratedServiceWave3Tests.Board_...` |
| mail/v1（公共邮箱/邮箱组 4 端点，分页） | `client.Mail.*`（生成） | ✅ | `Mail_PublicMailbox_List_...` |
| doc/v2 旧版文档（2 端点） | `client.Docs.*`（生成） | ✅ | `Docs_Legacy_Create_...` |
| tenant/v2 企业信息（1 端点） | `client.Tenant.*`（生成） | ✅ | `Tenant_Bot_Application_...` |
| bot/v3 机器人信息（1 端点） | `client.Bot.*`（生成） | ✅ | 同上 |
| application/v6 应用信息（2 端点） | `client.Application.*`（生成） | ✅ | 同上 |
| drive/v1 追加（create_folder / import·export 任务 5 端点） | `client.Drive.*`（生成） | ✅ | `Board_Create_And_Drive_Extras_...` |
| translation/v1（text/detect、text/translate，Tenant） | `client.Translation.*`（生成） | ✅ | `GeneratedServiceWave4Tests.Translation_...`（路径核对自 Go resource.go） |
| optical_char_recognition/v1（image/basic_recognize） | `client.Ocr.*`（生成） | ✅ | 同上 |
| cardkit/v1（卡片实体 5 + 组件 5 端点：create/batch_update/id_convert/settings/update、element create/content/update/patch/delete，Tenant） | `client.Cardkit.*`（生成） | ✅ | `Cardkit_Full_Lifecycle_...` |
| moments/v1（posts/:id） | `client.Moments.*`（生成） | ✅ | `Moments_And_Verification_...` |
| verification/v1（企业认证信息） | `client.Verification.*`（生成） | ✅ | 同上 |
| acs/v1 智能门禁（14 端点：门禁记录分页/抓拍图下载、设备、权限组 CRUD(User-only)、用户 GET/LIST/PATCH、人脸下载/上传、访客(User-only)） | `client.Acs.*`（生成） | ✅ | `Acs_Mixed_Token_Types_...`（含 User-only 校验拒绝） |
| attendance/v1 考勤（**36 端点**：考勤组/班次/排班/打卡流水/审批回写/统计查询/归档报表/人脸照片 16 资源，路径核对自 Go resource.go） | `client.Attendance.*`（生成） | ✅ | `Attendance_Core_Resources_...` |
| helpdesk/v1 服务台（**44 端点**：客服/日程/技能/知识库分类/FAQ+搜索/推送全生命周期/工单+消息（自动携带服务台鉴权头，生成器新增 needHelpdeskAuth 支持）） | `client.Helpdesk.*`（生成） | ✅ | `Helpdesk_Ticket_Should_Carry_HelpdeskAuth_Header` 等 4 例（含未配置凭证报错） |
| minutes/v1 妙记（8 端点：详情/AI产物/搜索/订阅、媒体/统计/文字记录导出，路径核对自 Go resource.go） | `client.Minutes.*`（生成） | ✅ | `Minutes_And_EventSvc_...` |
| event/v1 事件订阅管理（长连接在线数量 / 事件出口 IP） | `client.EventSvc.*`（生成） | ✅ | 同上 |
| okr/v1（12 端点：OKR 批量获取、周期 CRUD、周期规则、进展记录 CRUD、复盘查询、用户 OKR 列表、图片上传） | `client.Okr.*`（生成） | ✅ | `Okr_Full_Resources_...` |
| vc/v1 视频会议（**全量 68 端点/20 资源**——机读提取）：告警、机器人 5 接口、导出 6 接口、会议 9 接口（End User-only/PATCH、Kickout Tenant）、录制 4 接口、会议明细、纪要、参会人、质量、报告、预约全资源、会议室 room/room_level/room_config/scope_config 全部资源 | `client.Vc.*`（生成） | ✅ | `Vc_Meeting_Lifecycle_...` + `Vc_Export_And_Bot_...` + `Apaas_FullCoverage_...`（含 vc 尾部资源断言） |
| admin/v1 企业管理（**14 端点**：部门/用户活跃统计、行为日志、勋章 CRUD+授予名单 CRUD+图片上传、邮箱密码重置，路径核对自 Go resource.go） | `client.Admin.*`（生成） | ✅ | `Admin_Badge_And_Grant_...` + `Admin_Stats_Should_Page` |
| base/v2 多维表格自定义角色（3 端点：create/list 分页/update，User+Tenant） | `client.Base.*`（生成） | ✅ | `Base_AppRole_And_Block_Entity_...` |
| block/v2 小黑板（3 端点：entity create/update、协同消息推送） | `client.Block.*`（生成） | ✅ | 同上 |
| meeting_room/v1 与 elearning/v2 | — | ✅（空壳） | Go 侧 resource.go 无任何端点（结构与注释核对），无需对应实现 |
| report/v1 汇报（3 端点：规则查询/规则看板移除/任务查询，路径核对自 Go resource.go） | `client.Report.*`（生成） | ✅ | `Report_And_PersonalSettings_...` |
| personal_settings/v1（6 端点：系统状态 CRUD + 批量开/关） | `client.PersonalSettings.*`（生成） | ✅ | 同上 |
| directory/v1 通讯录企业版（**21 端点**：关联组织规则 CRUD、共享范围、部门 create/patch/delete/filter/mget/search、员工全生命周期含待离职 PATCH/离职 DELETE/恢复 POST/转在职 PATCH，User+Tenant） | `client.Directory.*`（生成） | ✅ | `Directory_Employee_Lifecycle_...`（动词语义断言）+ `Directory_Department_...` |
| lingo/v1 飞书词典（**15 端点**：分类、草稿 create/update、词条免审 CRUD + 详情/列表/模糊·精准搜索/高亮、图片上传下载、词库列表，路径核对自 Go resource.go；免审写接口 Tenant-only、查询 User+Tenant） | `client.Lingo.*`（生成） | ✅ | `Lingo_Entity_And_Draft_...` |
| trust_party/v1 信任组织（5 端点：关联组织详情/列表/可见成员、部门与成员详情（双层路径参数）） | `client.TrustParty.*`（生成） | ✅ | `TrustParty_Double_Path_Params_...` |
| document_ai/v1 文档 AI（**18 端点**：身份证/银行卡/名片/营业执照/护照/驾驶证/行驶证/健康证(Tenant+User)/两证/食品两证/增值税·出租车·火车·机动车发票/合同字段提取/简历解析，全部 POST+文件上传，路径核对自 Go resource.go） | `client.DocumentAi.*`（生成） | ✅ | `DocumentAi_Recognition_Family_...`（契约测试抓出并修复了 snake_case 路径 bug） |
| speech_to_text/v1 语音识别（2 端点：整段文件识别 / 流式识别，Tenant） | `client.SpeechToText.*`（生成） | ✅ | `SpeechToText_And_HumanAuthentication_...` |
| human_authentication/v1 实名认证（1 端点：身份信息录入，Tenant） | `client.HumanAuthentication.*`（生成） | ✅ | 同上 |
| passport/v1 账密安全（3 端点：重置密码 **PUT** /password、批量查脱敏登录信息、退出登录，Tenant，路径核对自 Go resource.go） | `client.Passport.*`（生成） | ✅ | `Passport_Should_Match_...`（PUT 动词断言） |
| spark/v1 妙搭低代码（**22 端点**：应用 CRUD+可用范围(kebab 路径 access-scope)+SQL+HTML 发布+图标、枚举、存储直传+分片四步、数据表记录 POST/PATCH/批量 PATCH/DELETE、视图、ID 互转；全 User-only 除 id_convert User+Tenant） | `client.Spark.*`（生成） | ✅ | `Spark_UserOnly_And_Kebab_...` + `Spark_Table_Records_Multi_Verb_...` |
| workplace/v1 工作台数据（3 端点：默认/定制/小组件访问数据搜索，Tenant） | `client.Workplace.*`（生成） | ✅ | `Workplace_And_Mdm_...` |
| mdm v1+v3 主数据管理（4 端点：数据维度绑定/解绑、按 mdmcode 批量查国家、分页查国家，Tenant） | `client.Mdm.*`（生成） | ✅ | 同上 |
| compensation/v1 薪酬（**21 端点**：薪资档案创建/查询、定调薪原因、统计指标、薪资项+分类、一次性/经常性支付 batch_create/update/remove+查询、社保档案/增减员/险种/参保方案，路径核对自 Go resource.go；lump_sum_payment 单数） | `client.Compensation.*`（生成） | ✅ | `Compensation_And_Payroll_...` |
| payroll/v1 算薪（**12 端点**：算薪项、成本分摊三表、外部数据源+记录 query/save、薪资组、发薪活动列表+封存（activitys 拼写与 Go 一致）、发薪明细，User+Tenant） | `client.Payroll.*`（生成） | ✅ | 同上 |
| performance/v2 绩效（**16 端点**：项目/评估项/指标库/字段/标签/模板/填写题/绩效详情/模板/被评估人/周期人员 全 POST-query 风格 + 补充信息 import/query/batch-delete + 关键指标 import/query + 人员组 write，Tenant） | `client.Performance.*`（生成） | ✅ | `Performance_Query_Style_...` |
| unified_kms/v1 统一密钥（8 端点：密钥 CRUD+删除计划 create/delete+恢复+导入材料，Tenant） | `client.UnifiedKms.*`（生成） | ✅ | `UnifiedKms_Key_Lifecycle_...` |
| aily/v1 智能伙伴（**31 端点**：智能体对话/会话/附件/产物/可见性(User-only)、Aily 会话 CRUD+消息+运行(create/get/list/cancel)、数据知识+分类+问答、技能 list/get/start、应用统计，User+Tenant） | `client.Aily.*`（生成） | ✅ | `Aily_Session_Run_Flow_...` |
| security_and_compliance/v2 安全合规（7 端点：设备申报审批 PUT、设备 CRUD+列表+mine(User-only)，Tenant） | `client.SecurityAndCompliance.*`（生成） | ✅ | `SecurityAndCompliance_Device_Mgmt_...` |
| apaas/v1 低代码平台（**全量 49 端点/19 资源**——机读提取）：应用、审计日志 4 接口、环境变量、流程执行、函数调用、OQL/搜索、记录单条+批量、权限授权、审批任务四操作、席位两项、运营指标、人工任务六操作、workspace SQL 命令；`namespace` 为 C# 保留字以 `@namespace` 处理 | `client.Apaas.*`（生成） | ✅ | `Apaas_Namespace_Keyword_Param_...` + `Apaas_Approval_Task_Actions_...` + `Apaas_FullCoverage_...` |
| ehr/v1 电子劳动合同（2 端点：花名册列表、附件下载） | `client.Ehr.*`（生成） | ✅ | `Ehr_Two_Endpoints_...` |
| hire/v1 招聘（**全量 179 端点/78 资源**——机读提取）：职位广告、猎头 7 接口（protection_period/search kebab）、投递全生命周期 10 接口、面试 7 资源、附件、背调、生态 10 资源、EHR 导入两任务、员工、评估、考试三资源、外部 6 资源、Offer 6 资源、职位 12 资源、问卷、内推、简历来源、人才 9 资源、终止原因、待办、三方协议、用户角色、官网 6 资源（combined_create 是真实路径后缀） | `client.Hire.*`（生成） | ✅ | `Hire_Application_Lifecycle_...` + `Hire_FullCoverage_...` |
| corehr/v1 飞书人事企业版（**全量 105 端点/33 资源**——机读提取：curl 原始源码 + 正则解析，绕过 zread 50KB 上限）：授权 5 接口、ID 转换、枚举值、公司 CRUD、薪资标准、合同 CRUD、国家地区、货币、自定义字段、部门 CRUD、人员类型/雇佣/职务/职级/职务族 CRUD、请假 6 接口、地址、证件类型、离职查询(Tenant+User)、人员 CRUD+批量上传、待入职 CRUD、流程表单变量、安全组、行政区划、异动原因/类型、工时制度 | `client.Corehr.*`（生成） | ✅ | `CorehrV1_FullCoverage_...` ×2 + 既有 3 例 |
| corehr/v2 飞书人事企业版 v2（**全量 164 端点/60 资源**——机读提取）：组织架构调整、审批人、**基础信息 11 类 Search**（bank_branchs 复数）、HRBP、公司 v2、合同搜索、成本分摊、成本中心三资源、自定义组织、默认成本中心、部门 batch_get、草稿、员工四资源、枚举、职务五资源、地址、离职、路径、人员、岗位、待入职、试用期两资源、**流程 12 资源**（approver/cc/extra/form_variable/node/query_flow_data_template/status/transfer/comment_info/revoke/start/withdraw）、报表明细行、**签名四资源**、人力规划三资源 | `client.CorehrV2.*`（生成） | ✅ | `CorehrV2_ApprovalGroups_...` + `CorehrV2_BasicInfo_Search_...` + `CorehrV2_Company_BatchGet_...` |
| bitable/v1（apps/tables/records 13 端点，含 search 分页） | `client.Bitable.*`（生成） | ✅ | `Bitable_CreateRecord_...`、`Bitable_ListTables_...` |
| drive/v1（upload_all/download/files/permissions 5 端点） | `client.Drive.*`（生成；download 透传字节） | ✅ | `Drive_Download_...` |
| approval/v4（approvals/instances 7 端点） | `client.Approval.*`（生成） | ✅ | `Approval_CreateInstance_...` |
| task/v2（5 端点，分页） | `client.Task.*`（生成） | ✅ | `Task_And_Docx_...` |
| docx/v1（documents/blocks 5 端点，分页） | `client.Docx.*`（生成） | ✅ | 同上 |
| 其余 50+ 服务（约数千 API，100% 由 oapi-sdk-gen 生成） | **未移植**（需代码生成器） | ⚠️ | 未覆盖端点用 `client.PostAsync/DoAsync` 原始调用直达（管道完全一致） |

## 8. 剩余差距（最终收窄）

| Go 模块 | 处理 |
|---|---|
| `service/*` | **全部 59 服务端点清单已收口**：53 生成服务 **992 端点** + im/contact/authen/ext 手写（含 2 空壳）。截断区已通过**机读提取**（curl 原始源码 + 正则解析 Go resource.go，绕过 zread 50KB 上限）全量核对：corehr v1 105 + v2 164 + hire 179 + apaas 49 + vc 68 = 565 端点。生成器处理 C# 保留字（@namespace 参数、PascalCase 资源属性） | ✅ 全收口 | 各波契约测试 + 第十六批全量抽样 4 例 |；剩余为纯清单数据补录（无新代码形态），未录入端点用 `client.PostAsync/DoAsync` 直达，管道与生成代码完全一致 |
| 真实飞书服务端端到端联调 | **待用户提供测试应用凭证**（FEISHU_APP_ID/APP_SECRET + FEISHU_DEMO_RECEIVE_ID，后台开长连接订阅）。**已达成前置等价验证**：本地 mock 真机链路 E2E（`LiveMockE2ETests`）——真实 HttpClient/ClientWebSocket socket + 本地 HttpListener 服务器，**五项全链路通过**：发消息/token 自动获取/卡片 DSL/查用户/Channel 流式回复/**WS 长连接收事件**（bootstrap HTTP → 真实 WebSocket 拨号 → pbbp2 事件帧 → 分发器回调 → 回执 `{"code":200}`，并顺带抓出并修复 `StartAsync` 异步信号量自死锁）；与真机唯一差异是 BaseUrl/Domain 指向 127.0.0.1，凭证到位改 URL 即跑 |
| sample/、doc/、mocksend* | 不适用（示例与文档） |

> 框架层（core/accesstoken/event/card/ws/httpserverext/channel/ext/scene）已全部一对一；
> 剩余差距仅为生成端点的清单数据量与真机联调两项。

## 9. 有意超越 Go 版的优化（协议不变，实现更强）

1. token 失效先驱逐缓存再重试（Go 带旧 token 空转一轮）。
2. `SingleFlight` 按 key 单飞，杜绝 token 惊群（10 并发 → 1 次端点命中，有测试）。
3. WS 重连为循环实现 + `Random.Shared`，无 goroutine 递归栈增长。
4. 分页 `IAsyncEnumerable<T>`（语言级异步流）替代手动 Iterator。
5. 验签比较用 `CryptographicOperations.FixedTimeEquals`（抗时序）。

## 10. 测试总览

- **233/233 通过**（Release 构建零警告零错误；含本地 mock 真机链路 E2E 1 例，覆盖全部五项含 WS）。
- 覆盖面：请求管道（14）、token 三模式与单飞（7）、ClientAssertion/OAuth（8）、事件（10）、卡片回调（9）、卡片 DSL（3）、contact（4）、authen（3）、**Channel 组件与编排（含端到端 24）**、**SSRF 防护（26 断言）**、**音视频时长（4）**、**ext（2）**、**registration（9）**、**生成服务（6+4+4 波）**、WS 协议（7 + 帧 6）、分页（2）、缓存/序列化（6）、AspNetCore 端点（3）、URL 构建（1）等。
- 协议向量均为独立来源预计算（Python sha1/sha256），非自我印证。
- 真实飞书凭证 E2E 与真实 WS 服务端联调仍待凭证（第十九次确认缺失）；其余链路已由本地 mock 真机链路 E2E（五项全链路）+ 帧级契约测试覆盖。
