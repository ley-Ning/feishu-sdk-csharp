using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Feishu.Channel;

/// <summary>
/// 事件归一化（对齐 Go channel/normalize：信封解析 + 按消息类型的文本/资源抽取）。
/// </summary>
public static partial class ChannelNormalize
{
    // ==================== 消息 ====================

    /// <summary>归一化 im.message.receive_v1 完整信封（含 header 的 event_id/create_time）。</summary>
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

    // ==================== 内容解析（对齐 Go normalize.ParseContent 主类型）====================

    public static (string Content, List<ChannelResource> Resources) ParseContent(string msgType, string content)
    {
        var resources = new List<ChannelResource>();
        try
        {
            switch (msgType)
            {
                case "text":
                {
                    using var doc = JsonDocument.Parse(content);
                    var root = doc.RootElement;
                    return (GetString(root, "text"), resources);
                }
                case "image":
                {
                    using var doc = JsonDocument.Parse(content);
                    var root = doc.RootElement;
                    var key = GetString(root, "image_key");
                    if (key.Length == 0) return ("[image]", resources);
                    resources.Add(new ChannelResource { Type = "image", FileKey = key });
                    return ($"![image]({key})", resources);
                }
                case "file":
                case "folder":
                {
                    using var doc = JsonDocument.Parse(content);
                    var root = doc.RootElement;
                    var key = GetString(root, "file_key");
                    var name = GetString(root, "file_name");
                    if (key.Length == 0) return ($"[{msgType}]", resources);
                    if (msgType == "file")
                        resources.Add(new ChannelResource { Type = "file", FileKey = key, FileName = name });
                    var attr = name.Length > 0 ? $" name=\"{EscapeAttr(name)}\"" : "";
                    return ($"<{msgType} key=\"{key}\"{attr}/>", resources);
                }
                case "audio":
                {
                    using var doc = JsonDocument.Parse(content);
                    var root = doc.RootElement;
                    var key = GetString(root, "file_key");
                    if (key.Length == 0) return ("[audio]", resources);
                    var res = new ChannelResource { Type = "audio", FileKey = key };
                    var attr = "";
                    if (root.TryGetProperty("duration", out var dur) && dur.TryGetInt32(out var ms))
                    {
                        res.DurationMs = ms;
                        var d = FormatDuration(ms);
                        if (d.Length > 0) attr = $" duration=\"{d}\"";
                    }
                    resources.Add(res);
                    return ($"<audio key=\"{key}\"{attr}/>", resources);
                }
                case "media":
                case "video":
                {
                    using var doc = JsonDocument.Parse(content);
                    var root = doc.RootElement;
                    var key = GetString(root, "file_key");
                    if (key.Length == 0) return ("[video]", resources);
                    var name = GetString(root, "file_name");
                    var res = new ChannelResource { Type = "video", FileKey = key, FileName = name, CoverImageKey = GetString(root, "image_key") };
                    var attr = name.Length > 0 ? $" name=\"{EscapeAttr(name)}\"" : "";
                    resources.Add(res);
                    return ($"<video key=\"{key}\"{attr}/>", resources);
                }
                case "sticker":
                {
                    using var doc = JsonDocument.Parse(content);
                    var root = doc.RootElement;
                    var key = GetString(root, "file_key");
                    if (key.Length == 0) return ("[sticker]", resources);
                    resources.Add(new ChannelResource { Type = "sticker", FileKey = key });
                    return ($"<sticker key=\"{key}\"/>", resources);
                }
                case "share_chat":
                {
                    using var doc = JsonDocument.Parse(content);
                    var root = doc.RootElement;
                    return ($"<group_card id=\"{GetString(root, "chat_id")}\"/>", resources);
                }
                case "share_user":
                {
                    using var doc = JsonDocument.Parse(content);
                    var root = doc.RootElement;
                    return ($"<contact_card id=\"{GetString(root, "user_id")}\"/>", resources);
                }
                case "post":
                    return ParsePost(content);
                case "interactive":
                    return ("[interactive card]", resources);
                case "merge_forward":
                    return (content, resources);
                default:
                    return ("[unsupported message]", resources);
            }
        }
        catch (JsonException)
        {
            if (msgType == "merge_forward") return (content, resources);
            return ("[unsupported message]", resources);
        }
    }

    /// <summary>富文本（post）：content_v2 优先，其次 content；识别 md/text/a/at/img/media/code_block/hr 元素。</summary>
    private static (string, List<ChannelResource>) ParsePost(string content)
    {
        var resources = new List<ChannelResource>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(content); }
        catch (JsonException) { return ("[unsupported message]", resources); }
        using var _ = doc;

        JsonElement body = default;
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                body = prop.Value;
                break;
            }
        }
        if (body.ValueKind != JsonValueKind.Object) return ("[rich text message]", resources);

        JsonElement paragraphs;
        if (body.TryGetProperty("content_v2", out var cv2) && cv2.ValueKind == JsonValueKind.Array)
            paragraphs = cv2;
        else if (body.TryGetProperty("content", out var cl) && cl.ValueKind == JsonValueKind.Array)
            paragraphs = cl;
        else
            return ("[rich text message]", resources);

        var lines = new List<string>();
        var title = GetString(body, "title");
        if (title.Length > 0)
        {
            lines.Add($"**{title}**");
            lines.Add("");
        }

        foreach (var paragraph in paragraphs.EnumerateArray())
        {
            if (paragraph.ValueKind != JsonValueKind.Array) continue;
            var parts = new List<string>();
            foreach (var el in paragraph.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var tag = GetString(el, "tag");
                var text = GetString(el, "text");
                switch (tag)
                {
                    case "md":
                        parts.Add(text);
                        break;
                    case "text":
                        parts.Add(text);
                        break;
                    case "a":
                    {
                        var href = GetString(el, "href");
                        var label = text.Length > 0 ? text : href;
                        parts.Add(href.Length > 0 ? $"[{label}]({href})" : label);
                        break;
                    }
                    case "at":
                    {
                        var userId = GetString(el, "user_id");
                        var userName = GetString(el, "user_name");
                        if (userId is "all" or "all_members") parts.Add("@all");
                        else parts.Add("@" + (userName.Length > 0 ? userName : userId));
                        break;
                    }
                    case "img":
                    {
                        var key = GetString(el, "image_key");
                        if (key.Length > 0)
                        {
                            resources.Add(new ChannelResource { Type = "image", FileKey = key });
                            parts.Add($"![image]({key})");
                        }
                        break;
                    }
                    case "media":
                    {
                        var key = GetString(el, "file_key");
                        if (key.Length > 0)
                        {
                            resources.Add(new ChannelResource { Type = "file", FileKey = key });
                            parts.Add($"<file key=\"{key}\"/>");
                        }
                        break;
                    }
                    case "code_block":
                        parts.Add($"\n```{GetString(el, "language")}\n{text}\n```\n");
                        break;
                    case "hr":
                        parts.Add("\n---\n");
                        break;
                    default:
                        parts.Add(text);
                        break;
                }
            }
            lines.Add(string.Join("", parts));
        }

        var result = string.Join("\n", lines).Trim();
        return (result.Length == 0 ? "[rich text message]" : result, resources);
    }

    // ==================== Markdown → Post（对齐 Go SimpleMarkdownToPost）====================

    public static string SimpleMarkdownToPost(string? title, string markdown, List<ChannelMention>? mentions)
    {
        var paragraphs = new List<List<Dictionary<string, string?>>>();

        if (mentions is { Count: > 0 })
        {
            var first = new List<Dictionary<string, string?>>();
            foreach (var m in mentions)
            {
                if (m.UserId.Length == 0) continue;
                first.Add(new() { ["tag"] = "at", ["user_id"] = m.UserId, ["user_name"] = m.Name });
                first.Add(new() { ["tag"] = "text", ["text"] = " " });
            }
            if (first.Count > 0) paragraphs.Add(first);
        }
        paragraphs.Add([new() { ["tag"] = "md", ["text"] = markdown }]);

        var post = new Dictionary<string, object?>
        {
            ["zh_cn"] = new Dictionary<string, object?>
            {
                ["title"] = string.IsNullOrEmpty(title) ? null : title,
                ["content"] = paragraphs,
            },
        };
        return SystemTextJsonFeishuSerializer.Instance.Serialize(post);
    }

    /// <summary>文本消息的提及前缀：`<at user_id="...">name</at> ` 空格连接。</summary>
    public static string ComposeMentionsTextPrefix(List<ChannelMention>? mentions)
    {
        if (mentions is not { Count: > 0 }) return "";
        var parts = new List<string>();
        foreach (var m in mentions)
        {
            if (m.UserId.Length == 0) continue;
            parts.Add($"<at user_id=\"{m.UserId}\">{m.Name}</at>");
        }
        return parts.Count == 0 ? "" : string.Join(" ", parts) + " ";
    }

    // ==================== 分片（对齐 Go outbound.SplitWithCodeFences）====================

    [GeneratedRegex("^```(\\w*)$")]
    private static partial Regex FenceRegex();

    [GeneratedRegex("^#{1,6}\\s")]
    private static partial Regex HeadingRegex();

    /// <summary>按字符上限切片；代码围栏跨界时闭合并在下一片重开；尽量在标题前断开。</summary>
    public static List<string> SplitWithCodeFences(string text, int limit)
    {
        if (text.Length <= limit) return [text];

        var lines = text.Split('\n');
        var output = new List<string>();
        var buffer = new List<string>();
        var bufferLen = 0;
        string? fenceLang = null;

        void Flush()
        {
            if (buffer.Count == 0) return;
            var chunk = string.Join("\n", buffer);
            if (fenceLang != null) chunk += "\n```";
            output.Add(chunk);
            buffer = new List<string>();
            bufferLen = 0;
            if (fenceLang != null)
            {
                var reopen = "```" + fenceLang;
                buffer.Add(reopen);
                bufferLen = reopen.Length;
            }
        }

        foreach (var line in lines)
        {
            var fenceMatch = FenceRegex().Match(line);
            var lineLen = line.Length + (buffer.Count > 0 ? 1 : 0);
            var isHeading = HeadingRegex().IsMatch(line);
            var nearFull = bufferLen > limit * 0.75;

            if (bufferLen + lineLen > limit || (isHeading && nearFull && buffer.Count > 0))
                Flush();

            buffer.Add(line);
            bufferLen += lineLen;

            if (fenceMatch.Success)
                fenceLang = fenceLang == null ? fenceMatch.Groups[1].Value : null;
        }
        Flush();
        return output;
    }

    // ==================== receive_id 类型推断（对齐 Go outbound.DetectReceiveIdType）====================

    public static string DetectReceiveIdType(string to) => to switch
    {
        "" => throw new FeishuChannelException(ChannelErrorCode.Unknown, "empty receive_id"),
        _ when to.StartsWith("oc_") => "chat_id",
        _ when to.StartsWith("ou_") => "open_id",
        _ when to.StartsWith("on_") => "union_id",
        _ when to.Contains('@') => "email",
        _ => "user_id",
    };

    // ==================== 工具 ====================

    internal static string ReactionDedupKey(ChannelReactionEvent e) =>
        $"rx:{e.MessageId}:{e.UserId}:{e.ReactionType}:{e.Action}:{e.CreateTimeMs}";

    internal static string CommentDedupKey(ChannelCommentEvent e) =>
        $"comment:{e.FileToken}:{e.CommentId}";

    internal static string CardActionDedupKey(ChannelCardActionEvent e) =>
        e.EventId.Length > 0
            ? e.EventId
            : $"card:{e.MessageId}:{e.OperatorOpenId}:{e.ActionTag}:{SystemTextJsonFeishuSerializer.Instance.Serialize(e.ActionValue ?? new Dictionary<string, object?>())}";

    private static string GetString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static string? NonEmpty(params string?[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrEmpty(v)) return v;
        return null;
    }

    private static long ParseLong(string? s) =>
        long.TryParse(s, out var v) ? v : 0;

    private static string EscapeAttr(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;").Replace("'", "&#39;");

    internal static string FormatDuration(int ms)
    {
        if (ms <= 0) return "";
        var sec = ms / 1000;
        if (sec < 60) return $"0:{sec:00}";
        var min = sec / 60;
        sec %= 60;
        if (min < 60) return $"{min}:{sec:00}";
        var hr = min / 60;
        min %= 60;
        return $"{hr}:{min:00}:{sec:00}";
    }

    internal static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    // ==================== 事件模型 ====================

    private sealed class Envelope<T>
    {
        [JsonPropertyName("header")]
        public EnvelopeHeader? Header { get; set; }

        [JsonPropertyName("event")]
        public T? Event { get; set; }
    }

    private sealed class EnvelopeHeader
    {
        [JsonPropertyName("event_id")]
        public string? EventId { get; set; }

        [JsonPropertyName("event_type")]
        public string? EventType { get; set; }

        [JsonPropertyName("create_time")]
        public string? CreateTime { get; set; }
    }

    private sealed class P2MessageReceiveEvent
    {
        [JsonPropertyName("sender")]
        public P2Sender? Sender { get; set; }

        [JsonPropertyName("message")]
        public P2Message? Message { get; set; }
    }

    private sealed class P2Sender
    {
        [JsonPropertyName("sender_id")]
        public P2SenderId? SenderId { get; set; }
    }

    private sealed class P2SenderId
    {
        [JsonPropertyName("open_id")]
        public string? OpenId { get; set; }

        [JsonPropertyName("user_id")]
        public string? UserId { get; set; }
    }

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

    private sealed class P2Mention
    {
        [JsonPropertyName("key")]
        public string? Key { get; set; }

        [JsonPropertyName("id")]
        public P2SenderId? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

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

    private sealed class ReactionType
    {
        [JsonPropertyName("emoji_type")]
        public string? EmojiType { get; set; }
    }

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

    private sealed class CommentUser
    {
        [JsonPropertyName("user_id")]
        public string? UserId { get; set; }

        [JsonPropertyName("open_id")]
        public string? OpenId { get; set; }

        [JsonPropertyName("union_id")]
        public string? UnionId { get; set; }
    }

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

    private sealed class BotAddedEventBody
    {
        [JsonPropertyName("chat_id")]
        public string? ChatId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("operator_id")]
        public P2SenderId? OperatorId { get; set; }
    }

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

    private sealed class CardOperator
    {
        [JsonPropertyName("open_id")]
        public string? OpenId { get; set; }

        [JsonPropertyName("user_id")]
        public string? UserId { get; set; }
    }

    private sealed class CardActionPayload
    {
        [JsonPropertyName("tag")]
        public string? Tag { get; set; }

        [JsonPropertyName("value")]
        public Dictionary<string, object?>? Value { get; set; }
    }

    private sealed class CardActionContextBody
    {
        [JsonPropertyName("open_message_id")]
        public string? OpenMessageId { get; set; }

        [JsonPropertyName("open_chat_id")]
        public string? OpenChatId { get; set; }
    }
}
