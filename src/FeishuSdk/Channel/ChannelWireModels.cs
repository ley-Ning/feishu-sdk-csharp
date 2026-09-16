using System.Text.Json.Serialization;

namespace Feishu.Channel;

/// <summary>
/// FeishuChannel 归一化分部：入站事件的 wire 反序列化 DTO（私有、仅字段映射，无行为）。
/// 字段名与飞书事件 payload 一一对应（snake_case 原样保留）。
/// </summary>
public static partial class ChannelNormalize
{
    // ==================== 事件模型 ====================

    /// <summary>事件 v2 信封：header + event 两段。</summary>
    private sealed class Envelope<T>
    {
        [JsonPropertyName("header")]
        public EnvelopeHeader? Header { get; set; }

        [JsonPropertyName("event")]
        public T? Event { get; set; }
    }

    /// <summary>信封 header（event_id/event_type/create_time）。</summary>
    private sealed class EnvelopeHeader
    {
        [JsonPropertyName("event_id")]
        public string? EventId { get; set; }

        [JsonPropertyName("event_type")]
        public string? EventType { get; set; }

        [JsonPropertyName("create_time")]
        public string? CreateTime { get; set; }
    }

    /// <summary>im.message.receive_v1 的 event 载荷。</summary>
    private sealed class P2MessageReceiveEvent
    {
        [JsonPropertyName("sender")]
        public P2Sender? Sender { get; set; }

        [JsonPropertyName("message")]
        public P2Message? Message { get; set; }
    }

    /// <summary>消息事件的发送者段。</summary>
    private sealed class P2Sender
    {
        [JsonPropertyName("sender_id")]
        public P2SenderId? SenderId { get; set; }
    }

    /// <summary>用户标识（open_id 优先，兼容 user_id）。</summary>
    private sealed class P2SenderId
    {
        [JsonPropertyName("open_id")]
        public string? OpenId { get; set; }

        [JsonPropertyName("user_id")]
        public string? UserId { get; set; }
    }

    /// <summary>消息体（message_id/chat_id/message_type/content/mentions）。</summary>
    private sealed class P2Message
    {
        [JsonPropertyName("message_id")]
        public string? MessageId { get; set; }

        [JsonPropertyName("chat_id")]
        public string? ChatId { get; set; }

        [JsonPropertyName("chat_type")]
        public string? ChatType { get; set; }

        [JsonPropertyName("message_type")]
        public string? MessageType { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("mentions")]
        public List<P2Mention>? Mentions { get; set; }
    }

    /// <summary>@提及（key 匹配正文占位符；@_user_all 表示 @所有人）。</summary>
    private sealed class P2Mention
    {
        [JsonPropertyName("key")]
        public string? Key { get; set; }

        [JsonPropertyName("id")]
        public P2SenderId? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    /// <summary>im.message.reaction.* 的 event 载荷。</summary>
    private sealed class ReactionEventBody
    {
        [JsonPropertyName("message_id")]
        public string? MessageId { get; set; }

        [JsonPropertyName("reaction_type")]
        public ReactionType? ReactionType { get; set; }

        [JsonPropertyName("user_id")]
        public P2SenderId? UserId { get; set; }

        [JsonPropertyName("app_id")]
        public string? AppId { get; set; }
    }

    /// <summary>表情类型（emoji_type，如 SMILE）。</summary>
    private sealed class ReactionType
    {
        [JsonPropertyName("emoji_type")]
        public string? EmojiType { get; set; }
    }

    /// <summary>drive.notice.comment_add_v1 的 event 载荷（部分字段与 notice_meta 双来源）。</summary>
    private sealed class CommentEventBody
    {
        [JsonPropertyName("comment_id")]
        public string? CommentId { get; set; }

        [JsonPropertyName("reply_id")]
        public string? ReplyId { get; set; }

        [JsonPropertyName("file_token")]
        public string? FileToken { get; set; }

        [JsonPropertyName("file_type")]
        public string? FileType { get; set; }

        [JsonPropertyName("create_time")]
        public string? CreateTime { get; set; }

        [JsonPropertyName("action_time")]
        public string? ActionTime { get; set; }

        [JsonPropertyName("is_mentioned")]
        public bool? IsMentioned { get; set; }

        [JsonPropertyName("is_mention")]
        public bool? IsMention { get; set; }

        [JsonPropertyName("user_id")]
        public CommentUser? UserId { get; set; }

        [JsonPropertyName("notice_meta")]
        public CommentNoticeMeta? NoticeMeta { get; set; }
    }

    /// <summary>评论用户标识（三 id 全量）。</summary>
    private sealed class CommentUser
    {
        [JsonPropertyName("user_id")]
        public string? UserId { get; set; }

        [JsonPropertyName("open_id")]
        public string? OpenId { get; set; }

        [JsonPropertyName("union_id")]
        public string? UnionId { get; set; }
    }

    /// <summary>评论事件的 notice_meta 段（老版字段来源）。</summary>
    private sealed class CommentNoticeMeta
    {
        [JsonPropertyName("file_token")]
        public string? FileToken { get; set; }

        [JsonPropertyName("file_type")]
        public string? FileType { get; set; }

        [JsonPropertyName("timestamp")]
        public string? Timestamp { get; set; }

        [JsonPropertyName("is_mentioned")]
        public bool? IsMentioned { get; set; }

        [JsonPropertyName("from_user_id")]
        public CommentUser? FromUserId { get; set; }
    }

    /// <summary>im.chat.member.bot.added_v1 的 event 载荷。</summary>
    private sealed class BotAddedEventBody
    {
        [JsonPropertyName("chat_id")]
        public string? ChatId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("operator_id")]
        public P2SenderId? OperatorId { get; set; }
    }

    /// <summary>card_action_trigger 的 event 载荷。</summary>
    private sealed class CardActionEventBody
    {
        [JsonPropertyName("token")]
        public string? Token { get; set; }

        [JsonPropertyName("operator")]
        public CardOperator? Operator { get; set; }

        [JsonPropertyName("action")]
        public CardActionPayload? Action { get; set; }

        [JsonPropertyName("context")]
        public CardActionContextBody? Context { get; set; }
    }

    /// <summary>卡片操作者标识。</summary>
    private sealed class CardOperator
    {
        [JsonPropertyName("open_id")]
        public string? OpenId { get; set; }

        [JsonPropertyName("user_id")]
        public string? UserId { get; set; }
    }

    /// <summary>卡片按钮行为（tag=按钮 tag，value=回调值）。</summary>
    private sealed class CardActionPayload
    {
        [JsonPropertyName("tag")]
        public string? Tag { get; set; }

        [JsonPropertyName("value")]
        public Dictionary<string, object?>? Value { get; set; }
    }

    /// <summary>卡片行为上下文（原始消息/群）。</summary>
    private sealed class CardActionContextBody
    {
        [JsonPropertyName("open_message_id")]
        public string? OpenMessageId { get; set; }

        [JsonPropertyName("open_chat_id")]
        public string? OpenChatId { get; set; }
    }
}
