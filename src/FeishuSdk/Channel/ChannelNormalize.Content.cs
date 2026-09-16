using System.Text.Json;

namespace Feishu.Channel;

/// <summary>
/// FeishuChannel 归一化分部：消息内容抽取（对齐 Go normalize.ParseContent 主类型）。
/// 每种消息类型映射为「一段 Markdown 化文本 + 若干 ChannelResource 资源引用」。
/// </summary>
public static partial class ChannelNormalize
{
    /// <summary>按消息类型抽取文本与资源；不识别的类型返回 "[unsupported message]"。</summary>
    /// <param name="msgType">飞书消息类型（text/image/file/audio/media/post/...）。</param>
    /// <param name="content">消息 content 字段的 JSON 字符串。</param>
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

    /// <summary>XML 属性值转义（资源标签的 name 属性等）。</summary>
    private static string EscapeAttr(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;").Replace("'", "&#39;");

    /// <summary>毫秒时长格式化为 m:ss / h:mm:ss（audio duration 属性用）。</summary>
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
}
