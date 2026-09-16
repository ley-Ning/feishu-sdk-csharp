using System.Text.Json.Serialization;

namespace Feishu.Services.Im;

// ==================== 消息资源 ====================

/// <summary>im/v1 消息资源：发送/回复/查询/编辑/撤回（对应 Go im.v1.message）。</summary>
public sealed class ImMessageResource
{
    private readonly RequestPipeline _pipeline;

    internal ImMessageResource(RequestPipeline pipeline) => _pipeline = pipeline;

    /// <summary>发送消息（POST /open-apis/im/v1/messages）。</summary>
    public Task<SendMessageResponse> CreateAsync(SendMessageRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var api = new ApiRequest
        {
            Method = HttpMethod.Post,
            Path = ImPaths.Messages,
            Body = request.Body,
            SupportedTokenTypes = [AccessTokenType.Tenant, AccessTokenType.User],
        };
        if (request.ReceiveIdType is { Length: > 0 })
            api.QueryParams.Add("receive_id_type", request.ReceiveIdType);
        return _pipeline.SendForAsync<SendMessageResponse>(api, options, cancellationToken);
    }

    /// <summary>回复消息（POST /open-apis/im/v1/messages/:message_id/reply）。</summary>
    public Task<SendMessageResponse> ReplyAsync(string messageId, ReplyMessageRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _pipeline.SendForAsync<SendMessageResponse>(new ApiRequest
        {
            Method = HttpMethod.Post,
            Path = ImPaths.MessageReply,
            PathParams = { ["message_id"] = messageId },
            Body = request.Body,
            SupportedTokenTypes = [AccessTokenType.Tenant, AccessTokenType.User],
        }, options, cancellationToken);

    /// <summary>查询消息（GET /open-apis/im/v1/messages/:message_id）。</summary>
    public Task<GetMessageResponse> GetAsync(string messageId, string? userIdType = null, RequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var api = new ApiRequest
        {
            Method = HttpMethod.Get,
            Path = ImPaths.MessageItem,
            PathParams = { ["message_id"] = messageId },
            SupportedTokenTypes = [AccessTokenType.Tenant, AccessTokenType.User],
        };
        if (userIdType != null)
            api.QueryParams.Add("user_id_type", userIdType);
        return _pipeline.SendForAsync<GetMessageResponse>(api, options, cancellationToken);
    }

    /// <summary>编辑消息（PUT /open-apis/im/v1/messages/:message_id）。</summary>
    public Task<PatchMessageResponse> PatchAsync(string messageId, PatchMessageRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _pipeline.SendForAsync<PatchMessageResponse>(new ApiRequest
        {
            Method = HttpMethod.Put,
            Path = ImPaths.MessageItem,
            PathParams = { ["message_id"] = messageId },
            Body = request.Body,
            SupportedTokenTypes = [AccessTokenType.Tenant, AccessTokenType.User],
        }, options, cancellationToken);

    /// <summary>撤回消息（DELETE /open-apis/im/v1/messages/:message_id）。</summary>
    public Task<RecallMessageResponse> DeleteAsync(string messageId, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _pipeline.SendForAsync<RecallMessageResponse>(new ApiRequest
        {
            Method = HttpMethod.Delete,
            Path = ImPaths.MessageItem,
            PathParams = { ["message_id"] = messageId },
            SupportedTokenTypes = [AccessTokenType.Tenant, AccessTokenType.User],
        }, options, cancellationToken);
}

// ==================== 消息请求 ====================

/// <summary>发送消息入参（receive_id_type 走 query 参数）。</summary>
public sealed class SendMessageRequest
{
    /// <summary>接收者 id 类型：open_id / user_id / union_id / chat_id / email。</summary>
    public string? ReceiveIdType { get; set; }

    /// <summary>消息体。</summary>
    public SendMessageBody? Body { get; set; }
}

/// <summary>回复消息入参。</summary>
public sealed class ReplyMessageRequest
{
    /// <summary>回复消息体。</summary>
    public ReplyMessageBody? Body { get; set; }
}

/// <summary>编辑消息入参。</summary>
public sealed class PatchMessageRequest
{
    /// <summary>编辑消息体（仅 content）。</summary>
    public PatchMessageBody? Body { get; set; }
}

/// <summary>发送消息体。</summary>
public sealed class SendMessageBody
{
    /// <summary>接收者 id（形态与 query 的 receive_id_type 匹配）。</summary>
    [JsonPropertyName("receive_id")]
    public string? ReceiveId { get; set; }

    /// <summary>消息类型：text / post / image / interactive / ...。</summary>
    [JsonPropertyName("msg_type")]
    public string? MsgType { get; set; }

    /// <summary>消息内容，JSON 字符串（如 <c>{"text":"hello"}</c>）。</summary>
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    /// <summary>幂等 uuid（可选，服务端去重）。</summary>
    [JsonPropertyName("uuid")]
    public string? Uuid { get; set; }
}

/// <summary>回复消息体。</summary>
public sealed class ReplyMessageBody
{
    /// <summary>消息类型。</summary>
    [JsonPropertyName("msg_type")]
    public string? MsgType { get; set; }

    /// <summary>消息内容 JSON 字符串。</summary>
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    /// <summary>幂等 uuid（可选）。</summary>
    [JsonPropertyName("uuid")]
    public string? Uuid { get; set; }
}

/// <summary>编辑消息体（目前仅支持改 content）。</summary>
public sealed class PatchMessageBody
{
    /// <summary>新内容 JSON 字符串。</summary>
    [JsonPropertyName("content")]
    public string? Content { get; set; }
}

// ==================== 消息响应 ====================

/// <summary>发送/回复消息响应。</summary>
public sealed class SendMessageResponse : FeishuResponse
{
    /// <summary>消息载荷。</summary>
    [JsonPropertyName("data")]
    public SendMessageResponseData? Data { get; set; }
}

/// <summary>发送/回复消息的 data 载荷。</summary>
public sealed class SendMessageResponseData
{
    /// <summary>消息 id（om_ 开头）。</summary>
    [JsonPropertyName("message_id")]
    public string? MessageId { get; set; }

    /// <summary>实际接收者 id。</summary>
    [JsonPropertyName("receive_id")]
    public string? ReceiveId { get; set; }
}

/// <summary>查询消息响应。</summary>
public sealed class GetMessageResponse : FeishuResponse
{
    /// <summary>消息载荷。</summary>
    [JsonPropertyName("data")]
    public GetMessageResponseData? Data { get; set; }
}

/// <summary>查询消息的 data 载荷。</summary>
public sealed class GetMessageResponseData
{
    /// <summary>消息条目（实际返回单条）。</summary>
    [JsonPropertyName("items")]
    public List<ImMessage>? Items { get; set; }
}

/// <summary>编辑消息响应。</summary>
public sealed class PatchMessageResponse : FeishuResponse
{
    /// <summary>编辑后的消息载荷（复用发送响应结构）。</summary>
    [JsonPropertyName("data")]
    public SendMessageResponseData? Data { get; set; }
}

/// <summary>撤回消息响应（无 data）。</summary>
public sealed class RecallMessageResponse : FeishuResponse;

// ==================== 消息模型 ====================

/// <summary>消息实体（查询接口返回）。</summary>
public sealed class ImMessage
{
    /// <summary>消息 id。</summary>
    [JsonPropertyName("message_id")]
    public string? MessageId { get; set; }

    /// <summary>根消息 id（话题根）。</summary>
    [JsonPropertyName("root_id")]
    public string? RootId { get; set; }

    /// <summary>父消息 id（回复链）。</summary>
    [JsonPropertyName("parent_id")]
    public string? ParentId { get; set; }

    /// <summary>话题 id。</summary>
    [JsonPropertyName("thread_id")]
    public string? ThreadId { get; set; }

    /// <summary>消息类型。</summary>
    [JsonPropertyName("msg_type")]
    public string? MsgType { get; set; }

    /// <summary>创建时间（毫秒字符串）。</summary>
    [JsonPropertyName("create_time")]
    public string? CreateTime { get; set; }

    /// <summary>更新时间（毫秒字符串）。</summary>
    [JsonPropertyName("update_time")]
    public string? UpdateTime { get; set; }

    /// <summary>是否已删除（撤回）。</summary>
    [JsonPropertyName("deleted")]
    public bool? Deleted { get; set; }

    /// <summary>是否被编辑过。</summary>
    [JsonPropertyName("updated")]
    public bool? Updated { get; set; }

    /// <summary>所属群 id。</summary>
    [JsonPropertyName("chat_id")]
    public string? ChatId { get; set; }

    /// <summary>发送者。</summary>
    [JsonPropertyName("sender")]
    public ImSender? Sender { get; set; }

    /// <summary>消息内容 JSON 字符串。</summary>
    [JsonPropertyName("content")]
    public string? Content { get; set; }
}

/// <summary>消息发送者（查询接口视角）。</summary>
public sealed class ImSender
{
    /// <summary>发送者 id。</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>id 类型。</summary>
    [JsonPropertyName("id_type")]
    public string? IdType { get; set; }

    /// <summary>发送者类型（user/app）。</summary>
    [JsonPropertyName("sender_type")]
    public string? SenderType { get; set; }

    /// <summary>租户 key。</summary>
    [JsonPropertyName("tenant_key")]
    public string? TenantKey { get; set; }
}
