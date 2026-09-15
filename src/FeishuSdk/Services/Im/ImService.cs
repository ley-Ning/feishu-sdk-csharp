using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Feishu.Services.Im;

/// <summary>
/// IM 服务聚合（对应 Go 版 service/im）。当前为手写的示范实现，
/// 展示代码生成器应产出的目标形态；后续服务由生成器批量产出。
/// </summary>
public sealed class ImService
{
    internal ImService(RequestPipeline pipeline)
    {
        V1 = new ImV1(pipeline);
    }

    public ImV1 V1 { get; }

    public ImMessageResource Message => V1.Message;

    public ImChatResource Chat => V1.Chat;
}

public sealed class ImV1
{
    internal ImV1(RequestPipeline pipeline)
    {
        Message = new ImMessageResource(pipeline);
        Chat = new ImChatResource(pipeline);
    }

    public ImMessageResource Message { get; }

    public ImChatResource Chat { get; }
}

internal static class ImPaths
{
    public const string Messages = "/open-apis/im/v1/messages";
    public const string MessageItem = "/open-apis/im/v1/messages/:message_id";
    public const string MessageReply = "/open-apis/im/v1/messages/:message_id/reply";
    public const string Chats = "/open-apis/im/v1/chats";
    public const string ChatItem = "/open-apis/im/v1/chats/:chat_id";
}

// ==================== 消息 ====================

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

public sealed class SendMessageRequest
{
    /// <summary>接收者 id 类型：open_id / user_id / union_id / chat_id / email。</summary>
    public string? ReceiveIdType { get; set; }

    public SendMessageBody? Body { get; set; }
}

public sealed class ReplyMessageRequest
{
    public ReplyMessageBody? Body { get; set; }
}

public sealed class PatchMessageRequest
{
    public PatchMessageBody? Body { get; set; }
}

public sealed class SendMessageBody
{
    [JsonPropertyName("receive_id")]
    public string? ReceiveId { get; set; }

    [JsonPropertyName("msg_type")]
    public string? MsgType { get; set; }

    /// <summary>消息内容，JSON 字符串（如 <c>{"text":"hello"}</c>）。</summary>
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; set; }
}

public sealed class ReplyMessageBody
{
    [JsonPropertyName("msg_type")]
    public string? MsgType { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; set; }
}

public sealed class PatchMessageBody
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }
}

public sealed class SendMessageResponse : FeishuResponse
{
    [JsonPropertyName("data")]
    public SendMessageResponseData? Data { get; set; }
}

public sealed class SendMessageResponseData
{
    [JsonPropertyName("message_id")]
    public string? MessageId { get; set; }

    [JsonPropertyName("receive_id")]
    public string? ReceiveId { get; set; }
}

public sealed class GetMessageResponse : FeishuResponse
{
    [JsonPropertyName("data")]
    public GetMessageResponseData? Data { get; set; }
}

public sealed class GetMessageResponseData
{
    [JsonPropertyName("items")]
    public List<ImMessage>? Items { get; set; }
}

public sealed class PatchMessageResponse : FeishuResponse
{
    [JsonPropertyName("data")]
    public SendMessageResponseData? Data { get; set; }
}

public sealed class RecallMessageResponse : FeishuResponse;

public sealed class ImMessage
{
    [JsonPropertyName("message_id")]
    public string? MessageId { get; set; }

    [JsonPropertyName("root_id")]
    public string? RootId { get; set; }

    [JsonPropertyName("parent_id")]
    public string? ParentId { get; set; }

    [JsonPropertyName("thread_id")]
    public string? ThreadId { get; set; }

    [JsonPropertyName("msg_type")]
    public string? MsgType { get; set; }

    [JsonPropertyName("create_time")]
    public string? CreateTime { get; set; }

    [JsonPropertyName("update_time")]
    public string? UpdateTime { get; set; }

    [JsonPropertyName("deleted")]
    public bool? Deleted { get; set; }

    [JsonPropertyName("updated")]
    public bool? Updated { get; set; }

    [JsonPropertyName("chat_id")]
    public string? ChatId { get; set; }

    [JsonPropertyName("sender")]
    public ImSender? Sender { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }
}

public sealed class ImSender
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("id_type")]
    public string? IdType { get; set; }

    [JsonPropertyName("sender_type")]
    public string? SenderType { get; set; }

    [JsonPropertyName("tenant_key")]
    public string? TenantKey { get; set; }
}

// ==================== 群组 ====================

public sealed class ImChatResource
{
    private readonly RequestPipeline _pipeline;

    internal ImChatResource(RequestPipeline pipeline) => _pipeline = pipeline;

    /// <summary>创建群（POST /open-apis/im/v1/chats）。</summary>
    public Task<CreateChatResponse> CreateAsync(CreateChatRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var api = new ApiRequest
        {
            Method = HttpMethod.Post,
            Path = ImPaths.Chats,
            Body = request.Body,
            SupportedTokenTypes = [AccessTokenType.Tenant, AccessTokenType.User],
        };
        if (request.UserIdType is { Length: > 0 })
            api.QueryParams.Add("user_id_type", request.UserIdType);
        return _pipeline.SendForAsync<CreateChatResponse>(api, options, cancellationToken);
    }

    /// <summary>获取群信息（GET /open-apis/im/v1/chats/:chat_id）。</summary>
    public Task<GetChatResponse> GetAsync(string chatId, string? userIdType = null, RequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var api = new ApiRequest
        {
            Method = HttpMethod.Get,
            Path = ImPaths.ChatItem,
            PathParams = { ["chat_id"] = chatId },
            SupportedTokenTypes = [AccessTokenType.Tenant, AccessTokenType.User],
        };
        if (userIdType != null)
            api.QueryParams.Add("user_id_type", userIdType);
        return _pipeline.SendForAsync<GetChatResponse>(api, options, cancellationToken);
    }

    /// <summary>获取用户所在群列表（GET /open-apis/im/v1/chats，分页）。</summary>
    public Task<ListChatsResponse> ListAsync(ListChatsRequest? request = null, RequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var api = new ApiRequest
        {
            Method = HttpMethod.Get,
            Path = ImPaths.Chats,
            SupportedTokenTypes = [AccessTokenType.User],
        };
        if (request?.PageSize is { } pageSize)
            api.QueryParams.Add("page_size", pageSize.ToString());
        if (request?.PageToken is { Length: > 0 } token)
            api.QueryParams.Add("page_token", token);
        return _pipeline.SendForAsync<ListChatsResponse>(api, options, cancellationToken);
    }

    /// <summary>分页枚举用户所在群（IAsyncEnumerable，自动翻页）。</summary>
    public async IAsyncEnumerable<ImChat> EnumerateAsync(ListChatsRequest? request = null, RequestOptions? options = null, int? limit = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var emitted = 0;
        string? pageToken = null;
        do
        {
            var page = await ListAsync(ApplyToken(request, pageToken), options, cancellationToken);
            page.EnsureSuccess();
            var items = page.Data?.Items ?? [];
            foreach (var item in items)
            {
                if (limit.HasValue && emitted >= limit.Value) yield break;
                emitted++;
                yield return item;
            }
            pageToken = page.Data?.PageToken is { Length: > 0 } ? page.Data.PageToken : null;
        } while (pageToken != null);
    }

    private static ListChatsRequest? ApplyToken(ListChatsRequest? request, string? pageToken) =>
        pageToken == null ? request : (request ?? new ListChatsRequest()) with { PageToken = pageToken };
}

public sealed class CreateChatRequest
{
    public string? UserIdType { get; set; }

    public CreateChatBody? Body { get; set; }
}

public sealed record ListChatsRequest
{
    public int? PageSize { get; init; }

    public string? PageToken { get; init; }
}

public sealed class CreateChatBody
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("owner_id")]
    public string? OwnerId { get; set; }

    [JsonPropertyName("user_id_list")]
    public List<string>? UserIdList { get; set; }

    [JsonPropertyName("chat_mode")]
    public string? ChatMode { get; set; }

    [JsonPropertyName("chat_type")]
    public string? ChatType { get; set; }

    [JsonPropertyName("external")]
    public bool? External { get; set; }
}

public sealed class CreateChatResponse : FeishuResponse
{
    [JsonPropertyName("data")]
    public CreateChatResponseData? Data { get; set; }
}

public sealed class CreateChatResponseData
{
    [JsonPropertyName("chat_id")]
    public string? ChatId { get; set; }
}

public sealed class GetChatResponse : FeishuResponse
{
    [JsonPropertyName("data")]
    public ImChat? Data { get; set; }
}

public sealed class ListChatsResponse : FeishuResponse
{
    [JsonPropertyName("data")]
    public ListChatsResponseData? Data { get; set; }
}

public sealed class ListChatsResponseData
{
    [JsonPropertyName("items")]
    public List<ImChat>? Items { get; set; }

    [JsonPropertyName("page_token")]
    public string? PageToken { get; set; }

    [JsonPropertyName("has_more")]
    public bool? HasMore { get; set; }
}

public sealed class ImChat
{
    [JsonPropertyName("chat_id")]
    public string? ChatId { get; set; }

    [JsonPropertyName("avatar")]
    public string? Avatar { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("owner_id")]
    public string? OwnerId { get; set; }

    [JsonPropertyName("owner_id_type")]
    public string? OwnerIdType { get; set; }

    [JsonPropertyName("external")]
    public bool? External { get; set; }

    [JsonPropertyName("tenant_key")]
    public string? TenantKey { get; set; }

    [JsonPropertyName("chat_status")]
    public string? ChatStatus { get; set; }
}

// ==================== 事件 ====================

public static class ImEventTypes
{
    public const string MessageReceiveV1 = "im.message.receive_v1";
    public const string MessageReadV1 = "im.message.message_read_v1";
    public const string ChatCreatedV1 = "im.chat.created_v1";
}

/// <summary>接收消息事件（im.message.receive_v1 的 event 字段结构）。</summary>
public sealed class P2MessageReceiveV1
{
    [JsonPropertyName("sender")]
    public P2MessageReceiveSender? Sender { get; set; }

    [JsonPropertyName("message")]
    public P2MessageReceiveMessage? Message { get; set; }
}

public sealed class P2MessageReceiveSender
{
    [JsonPropertyName("sender_id")]
    public ImSenderId? SenderId { get; set; }

    [JsonPropertyName("sender_type")]
    public string? SenderType { get; set; }

    [JsonPropertyName("tenant_key")]
    public string? TenantKey { get; set; }
}

public sealed class ImSenderId
{
    [JsonPropertyName("open_id")]
    public string? OpenId { get; set; }

    [JsonPropertyName("user_id")]
    public string? UserId { get; set; }

    [JsonPropertyName("union_id")]
    public string? UnionId { get; set; }
}

public sealed class P2MessageReceiveMessage
{
    [JsonPropertyName("message_id")]
    public string? MessageId { get; set; }

    [JsonPropertyName("root_id")]
    public string? RootId { get; set; }

    [JsonPropertyName("parent_id")]
    public string? ParentId { get; set; }

    [JsonPropertyName("create_time")]
    public string? CreateTime { get; set; }

    [JsonPropertyName("chat_id")]
    public string? ChatId { get; set; }

    [JsonPropertyName("chat_type")]
    public string? ChatType { get; set; }

    [JsonPropertyName("message_type")]
    public string? MessageType { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }
}
