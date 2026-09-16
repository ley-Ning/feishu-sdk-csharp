using System.Text.Json;
using System.Text.Json.Serialization;

namespace Feishu.Channel;

/// <summary>
/// 事件归一化（对齐 Go channel/normalize：信封解析 + 按消息类型的文本/资源抽取）。
/// 本文件：入站事件解析与工具方法。分部文件：
/// ChannelWireModels.cs（事件 wire DTO）、ChannelNormalize.Content.cs（消息内容抽取）、
/// ChannelNormalize.Markdown.cs（出站 Markdown→Post 与分片）。
/// </summary>
public static partial class ChannelNormalize
{
    // ==================== 消息 ====================

    /// <summary>归一化 im.message.receive_v1 完整信封（含 header 的 event_id/create_time）。</summary>
    /// <returns>解析失败或缺关键字段时返回 null（调用方直接丢弃）。</returns>
    public static NormalizedMessage? ParseMessage(ReadOnlySpan<byte> envelopeJson)
    {
        Envelope<P2MessageReceiveEvent>? envelope;
        try { envelope = JsonSerializer.Deserialize<Envelope<P2MessageReceiveEvent>>(envelopeJson, JsonOpts); }
        catch (JsonException) { return null; }
        if (envelope?.Event?.Message == null) return null;

        var msg = envelope.Event.Message;
        var norm = new NormalizedMessage
        {
            EventId = envelope.Header?.EventId ?? "",
            CreateTimeMs = ParseLong(envelope.Header?.CreateTime),
            MessageId = msg.MessageId ?? "",
            ChatId = msg.ChatId ?? "",
            ChatType = msg.ChatType ?? "",
            RawContentType = msg.MessageType ?? "",
        };

        var sender = envelope.Event.Sender;
        norm.UserId = sender?.SenderId?.OpenId ?? sender?.SenderId?.UserId ?? "";

        if (msg.Mentions != null)
        {
            foreach (var m in msg.Mentions)
            {
                norm.Mentions.Add(new ChannelMention
                {
                    Key = m.Key ?? "",
                    UserId = m.Id?.UserId ?? m.Id?.OpenId ?? "",
                    OpenId = m.Id?.OpenId ?? "",
                    Name = m.Name ?? "",
                });
                if (m.Key is "@_all" or "@all")
                    norm.MentionAll = true;
            }
        }

        if (msg.Content != null && msg.MessageType != null)
        {
            var (content, resources) = ParseContent(msg.MessageType, msg.Content);
            if (content.Length > 0)
            {
                norm.Content = content;
                if (content.Contains("@_all") || content.Contains("@all"))
                    norm.MentionAll = true;
            }
            norm.Resources.AddRange(resources);
        }

        return norm;
    }

    // ==================== 表情回复 ====================

    /// <summary>归一化 im.message.reaction.created/deleted_v1；eventType 以 deleted 结尾时 Action=removed。</summary>
    public static ChannelReactionEvent? ParseReaction(string eventType, ReadOnlySpan<byte> envelopeJson)
    {
        Envelope<ReactionEventBody>? envelope;
        try { envelope = JsonSerializer.Deserialize<Envelope<ReactionEventBody>>(envelopeJson, JsonOpts); }
        catch (JsonException) { return null; }
        if (envelope?.Event == null) return null;

        var ev = envelope.Event;
        var userId = ev.UserId?.OpenId ?? ev.UserId?.UserId ?? ev.AppId ?? "";
        return new ChannelReactionEvent(
            EventId: envelope.Header?.EventId ?? "",
            MessageId: ev.MessageId ?? "",
            ReactionType: ev.ReactionType?.EmojiType ?? "",
            UserId: userId,
            Action: eventType.EndsWith("deleted_v1") ? "removed" : "added",
            CreateTimeMs: ParseLong(envelope.Header?.CreateTime));
    }

    // ==================== 评论 ====================

    /// <summary>归一化 drive.notice.comment_add_v1；file_token/file_type/评论人缺失时返回 null。</summary>
    public static ChannelCommentEvent? ParseComment(ReadOnlySpan<byte> envelopeJson)
    {
        Envelope<CommentEventBody>? envelope;
        try { envelope = JsonSerializer.Deserialize<Envelope<CommentEventBody>>(envelopeJson, JsonOpts); }
        catch (JsonException) { return null; }
        if (envelope?.Event == null) return null;

        var ev = envelope.Event;
        var fileToken = NonEmpty(ev.FileToken, ev.NoticeMeta?.FileToken);
        var fileType = NonEmpty(ev.FileType, ev.NoticeMeta?.FileType);
        var fromUser = ev.NoticeMeta?.FromUserId ?? ev.UserId;
        if (fileToken == null || fileType == null || string.IsNullOrEmpty(ev.CommentId) || string.IsNullOrEmpty(fromUser?.OpenId))
            return null;

        var ts = ParseLong(NonEmpty(ev.CreateTime, NonEmpty(ev.NoticeMeta?.Timestamp, ev.ActionTime)));
        var mentioned = ev.IsMentioned ?? ev.NoticeMeta?.IsMentioned ?? ev.IsMention ?? false;

        return new ChannelCommentEvent(
            EventId: envelope.Header?.EventId ?? "",
            CommentId: ev.CommentId ?? "",
            FileToken: fileToken,
            FileType: fileType,
            ReplyId: ev.ReplyId ?? "",
            OperatorOpenId: fromUser.OpenId ?? "",
            OperatorUserId: fromUser.UserId ?? "",
            OperatorUnionId: fromUser.UnionId ?? "",
            MentionedBot: mentioned,
            Timestamp: ts);
    }

    // ==================== 机器人进群 ====================

    /// <summary>归一化 im.chat.member.bot.added_v1（含群名与操作者）。</summary>
    public static ChannelBotAddedEvent? ParseBotAdded(ReadOnlySpan<byte> envelopeJson)
    {
        Envelope<BotAddedEventBody>? envelope;
        try { envelope = JsonSerializer.Deserialize<Envelope<BotAddedEventBody>>(envelopeJson, JsonOpts); }
        catch (JsonException) { return null; }
        if (envelope?.Event == null) return null;

        return new ChannelBotAddedEvent(
            EventId: envelope.Header?.EventId ?? "",
            ChatId: envelope.Event.ChatId ?? "",
            ChatName: envelope.Event.Name ?? "",
            UserId: envelope.Event.OperatorId?.OpenId ?? envelope.Event.OperatorId?.UserId ?? "",
            CreateTimeMs: ParseLong(envelope.Header?.CreateTime));
    }

    // ==================== 卡片行为 ====================

    /// <summary>归一化 card_action_trigger（卡片按钮回传：操作者 + action tag/value + 消息上下文）。</summary>
    public static ChannelCardActionEvent? ParseCardAction(ReadOnlySpan<byte> envelopeJson)
    {
        Envelope<CardActionEventBody>? envelope;
        try { envelope = JsonSerializer.Deserialize<Envelope<CardActionEventBody>>(envelopeJson, JsonOpts); }
        catch (JsonException) { return null; }
        if (envelope?.Event == null) return null;

        var ev = envelope.Event;
        return new ChannelCardActionEvent(
            EventId: envelope.Header?.EventId ?? "",
            MessageId: ev.Context?.OpenMessageId ?? "",
            ChatId: ev.Context?.OpenChatId ?? "",
            Token: ev.Token ?? "",
            OperatorOpenId: ev.Operator?.OpenId,
            OperatorUserId: ev.Operator?.UserId,
            ActionTag: ev.Action?.Tag,
            ActionValue: ev.Action?.Value);
    }

    // ==================== 工具 ====================

    /// <summary>表情回复去重键：(message_id, user, emoji, action, create_time)。</summary>
    internal static string ReactionDedupKey(ChannelReactionEvent e) =>
        $"rx:{e.MessageId}:{e.UserId}:{e.ReactionType}:{e.Action}:{e.CreateTimeMs}";

    /// <summary>评论去重键：(file_token, comment_id)。</summary>
    internal static string CommentDedupKey(ChannelCommentEvent e) =>
        $"comment:{e.FileToken}:{e.CommentId}";

    /// <summary>卡片回传去重键：优先 event_id；缺失时退化为 (消息, 操作者, action, value) 组合。</summary>
    internal static string CardActionDedupKey(ChannelCardActionEvent e) =>
        e.EventId.Length > 0
            ? e.EventId
            : $"card:{e.MessageId}:{e.OperatorOpenId}:{e.ActionTag}:{SystemTextJsonFeishuSerializer.Instance.Serialize(e.ActionValue ?? new Dictionary<string, object?>())}";

    /// <summary>安全读取 JSON 字符串属性（缺失/非字符串返回空串）。</summary>
    private static string GetString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    /// <summary>取第一个非空值（用于事件 body 与 notice_meta 双来源字段）。</summary>
    private static string? NonEmpty(params string?[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrEmpty(v)) return v;
        return null;
    }

    /// <summary>字符串转 long（失败返回 0；create_time 等时间戳字段容错）。</summary>
    private static long ParseLong(string? s) =>
        long.TryParse(s, out var v) ? v : 0;

    /// <summary>事件反序列化选项：Web 命名 + 数字按字符串读取（兼容服务端两种形态）。</summary>
    internal static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}
