using System.Text.RegularExpressions;

namespace Feishu.Channel;

/// <summary>
/// FeishuChannel 归一化分部：出站方向——Markdown→Post 结构转换、@提及拼装、
/// 长文分片（代码块围栏感知）与 receive_id 类型推断（对齐 Go outbound 对应函数）。
/// </summary>
public static partial class ChannelNormalize
{
    // ==================== Markdown → Post（对齐 Go SimpleMarkdownToPost）====================

    /// <summary>把一段 Markdown 包成 zh_cn post JSON：mentions 生成首段 at 行，正文走 md 元素。</summary>
    /// <param name="title">帖子标题（空则省略）。</param>
    /// <param name="markdown">正文 Markdown（原样放入 md 元素，由客户端渲染）。</param>
    /// <param name="mentions">需要在首段 @ 的用户列表（可为 null）。</param>
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

    /// <summary>围栏开/闭行：```lang 或 ```。</summary>
    [GeneratedRegex("^```(\\w*)$")]
    private static partial Regex FenceRegex();

    /// <summary>Markdown 标题行（# ~ ######）。</summary>
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

    /// <summary>按 ID 前缀/形态推断 receive_id_type：oc_→chat_id、ou_→open_id、on_→union_id、含@→email、其余→user_id。</summary>
    /// <exception cref="FeishuChannelException">空字符串时抛 Unknown 错误。</exception>
    public static string DetectReceiveIdType(string to) => to switch
    {
        "" => throw new FeishuChannelException(ChannelErrorCode.Unknown, "empty receive_id"),
        _ when to.StartsWith("oc_") => "chat_id",
        _ when to.StartsWith("ou_") => "open_id",
        _ when to.StartsWith("on_") => "union_id",
        _ when to.Contains('@') => "email",
        _ => "user_id",
    };
}
