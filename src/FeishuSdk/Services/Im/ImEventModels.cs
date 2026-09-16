using System.Text.Json.Serialization;

namespace Feishu.Services.Im;

// ==================== 接收消息事件模型 ====================

/// <summary>接收消息事件（im.message.receive_v1 的 event 字段结构）。</summary>
public sealed class P2MessageReceiveV1
{
    /// <summary>发送者。</summary>
    [JsonPropertyName("sender")]
    public P2MessageReceiveSender? Sender { get; set; }

    /// <summary>消息体。</summary>
    [JsonPropertyName("message")]
    public P2MessageReceiveMessage? Message { get; set; }
}

/// <summary>接收消息事件的 sender 段。</summary>
public sealed class P2MessageReceiveSender
{
    /// <summary>发送者标识（三 id）。</summary>
    [JsonPropertyName("sender_id")]
    public ImSenderId? SenderId { get; set; }

    /// <summary>发送者类型（user/app）。</summary>
    [JsonPropertyName("sender_type")]
    public string? SenderType { get; set; }

    /// <summary>租户 key。</summary>
    [JsonPropertyName("tenant_key")]
    public string? TenantKey { get; set; }
}

/// <summary>用户三 id 载荷（open_id/user_id/union_id）。</summary>
public sealed class ImSenderId
{
    /// <summary>open_id（ou_ 开头）。</summary>
    [JsonPropertyName("open_id")]
    public string? OpenId { get; set; }

    /// <summary>user_id。</summary>
    [JsonPropertyName("user_id")]
    public string? UserId { get; set; }

    /// <summary>union_id（on_ 开头）。</summary>
    [JsonPropertyName("union_id")]
    public string? UnionId { get; set; }
}

/// <summary>接收消息事件的 message 段。</summary>
public sealed class P2MessageReceiveMessage
{
    /// <summary>消息 id。</summary>
    [JsonPropertyName("message_id")]
    public string? MessageId { get; set; }

    /// <summary>根消息 id。</summary>
    [JsonPropertyName("root_id")]
    public string? RootId { get; set; }

    /// <summary>父消息 id。</summary>
    [JsonPropertyName("parent_id")]
    public string? ParentId { get; set; }

    /// <summary>创建时间（毫秒字符串）。</summary>
    [JsonPropertyName("create_time")]
    public string? CreateTime { get; set; }

    /// <summary>所属群 id。</summary>
    [JsonPropertyName("chat_id")]
    public string? ChatId { get; set; }

    /// <summary>群类型（p2p/group）。</summary>
    [JsonPropertyName("chat_type")]
    public string? ChatType { get; set; }

    /// <summary>消息类型。</summary>
    [JsonPropertyName("message_type")]
    public string? MessageType { get; set; }

    /// <summary>消息内容 JSON 字符串。</summary>
    [JsonPropertyName("content")]
    public string? Content { get; set; }
}
