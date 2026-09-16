using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Feishu.Services.Im;

// ==================== 群组资源 ====================

/// <summary>im/v1 群组资源：建群/查群/群列表（对应 Go im.v1.chat）。</summary>
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

    /// <summary>获取用户所在群列表（GET /open-apis/im/v1/chats，分页；user_access_token 场景）。</summary>
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
    /// <param name="limit">最多产出条数（null 表示翻完为止）。</param>
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

// ==================== 群组请求 ====================

/// <summary>创建群入参。</summary>
public sealed class CreateChatRequest
{
    /// <summary>成员 id 类型（query 参数）。</summary>
    public string? UserIdType { get; set; }

    /// <summary>建群参数体。</summary>
    public CreateChatBody? Body { get; set; }
}

/// <summary>群列表查询入参（分页）。</summary>
public sealed record ListChatsRequest
{
    /// <summary>页大小。</summary>
    public int? PageSize { get; init; }

    /// <summary>分页 token（首页不传）。</summary>
    public string? PageToken { get; init; }
}

/// <summary>建群参数体。</summary>
public sealed class CreateChatBody
{
    /// <summary>群名称。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>群描述。</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>群主 id。</summary>
    [JsonPropertyName("owner_id")]
    public string? OwnerId { get; set; }

    /// <summary>初始成员 id 列表。</summary>
    [JsonPropertyName("user_id_list")]
    public List<string>? UserIdList { get; set; }

    /// <summary>群模式（group）。</summary>
    [JsonPropertyName("chat_mode")]
    public string? ChatMode { get; set; }

    /// <summary>群类型（p2p/group）。</summary>
    [JsonPropertyName("chat_type")]
    public string? ChatType { get; set; }

    /// <summary>是否外部群。</summary>
    [JsonPropertyName("external")]
    public bool? External { get; set; }
}

// ==================== 群组响应 ====================

/// <summary>建群响应。</summary>
public sealed class CreateChatResponse : FeishuResponse
{
    /// <summary>建群结果。</summary>
    [JsonPropertyName("data")]
    public CreateChatResponseData? Data { get; set; }
}

/// <summary>建群响应 data 载荷。</summary>
public sealed class CreateChatResponseData
{
    /// <summary>新群 id（oc_ 开头）。</summary>
    [JsonPropertyName("chat_id")]
    public string? ChatId { get; set; }
}

/// <summary>查群响应。</summary>
public sealed class GetChatResponse : FeishuResponse
{
    /// <summary>群信息。</summary>
    [JsonPropertyName("data")]
    public ImChat? Data { get; set; }
}

/// <summary>群列表响应。</summary>
public sealed class ListChatsResponse : FeishuResponse
{
    /// <summary>分页载荷。</summary>
    [JsonPropertyName("data")]
    public ListChatsResponseData? Data { get; set; }
}

/// <summary>群列表 data 载荷。</summary>
public sealed class ListChatsResponseData
{
    /// <summary>群条目。</summary>
    [JsonPropertyName("items")]
    public List<ImChat>? Items { get; set; }

    /// <summary>下一页 token。</summary>
    [JsonPropertyName("page_token")]
    public string? PageToken { get; set; }

    /// <summary>是否还有下一页。</summary>
    [JsonPropertyName("has_more")]
    public bool? HasMore { get; set; }
}

// ==================== 群组模型 ====================

/// <summary>群实体。</summary>
public sealed class ImChat
{
    /// <summary>群 id（oc_ 开头）。</summary>
    [JsonPropertyName("chat_id")]
    public string? ChatId { get; set; }

    /// <summary>群头像 key。</summary>
    [JsonPropertyName("avatar")]
    public string? Avatar { get; set; }

    /// <summary>群名称。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>群描述。</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>群主 id。</summary>
    [JsonPropertyName("owner_id")]
    public string? OwnerId { get; set; }

    /// <summary>群主 id 类型。</summary>
    [JsonPropertyName("owner_id_type")]
    public string? OwnerIdType { get; set; }

    /// <summary>是否外部群。</summary>
    [JsonPropertyName("external")]
    public bool? External { get; set; }

    /// <summary>租户 key。</summary>
    [JsonPropertyName("tenant_key")]
    public string? TenantKey { get; set; }

    /// <summary>群状态（normal/deleted/...）。</summary>
    [JsonPropertyName("chat_status")]
    public string? ChatStatus { get; set; }
}
