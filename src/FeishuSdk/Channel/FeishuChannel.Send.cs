using Feishu.Services.Im;

namespace Feishu.Channel;

/// <summary>
/// FeishuChannel 分部：结构化发送、降级与重试。
/// 内容优先级对齐 Go channel.Send：image/audio/video/file/card/post/share/sticker → markdown → text；
/// Markdown 自动转 Post 并按代码块围栏分片，纯文本按长度硬切。
/// </summary>
public sealed partial class FeishuChannel
{
    // ==================== 发送 ====================

    /// <summary>结构化发送（对齐 Go channel.Send：内容优先级/分片/上传/降级）。</summary>
    /// <param name="input">发送入参；UserId/ReceiveId/ChatId 三选一，内容字段按优先级取一个。</param>
    /// <returns>首条消息 id 与 chat_id；分片发送时 ExtraMessageIds 带后续分片。</returns>
    /// <exception cref="FeishuException">没有任何接收者或内容字段时抛出。</exception>
    public async Task<ChannelSendResult> SendAsync(ChannelSendInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var receiveIdType = "open_id";
        var receiveId = input.UserId ?? "";
        if (!string.IsNullOrEmpty(input.ReceiveId))
        {
            receiveId = input.ReceiveId;
            receiveIdType = ChannelNormalize.DetectReceiveIdType(receiveId);
        }
        else if (!string.IsNullOrEmpty(input.ChatId))
        {
            receiveIdType = "chat_id";
            receiveId = input.ChatId;
        }
        if (receiveId.Length == 0)
            throw new FeishuException("ReceiveId, ChatId, or UserId must be provided");

        // 本地文件先上传
        if (!string.IsNullOrEmpty(input.ImagePath) && string.IsNullOrEmpty(input.ImageKey))
            input.ImageKey = await UploadImageAsync(input.ImagePath, ct);
        if (!string.IsNullOrEmpty(input.FilePath) && string.IsNullOrEmpty(input.FileKey))
            input.FileKey = await UploadFileAsync(input.FilePath, ct);

        // 内容组装（优先级对齐 Go）
        string msgType = input.MsgType ?? "";
        string content = "";

        if (!string.IsNullOrEmpty(input.ImageKey))
            (msgType, content) = ("image", Json(new Dictionary<string, string?> { ["image_key"] = input.ImageKey }));
        else if (!string.IsNullOrEmpty(input.AudioKey))
            (msgType, content) = ("audio", Json(new Dictionary<string, string?> { ["file_key"] = input.AudioKey }));
        else if (!string.IsNullOrEmpty(input.VideoKey))
            (msgType, content) = ("media", Json(new Dictionary<string, string?> { ["file_key"] = input.VideoKey }));
        else if (!string.IsNullOrEmpty(input.FileKey))
            (msgType, content) = ("file", Json(new Dictionary<string, string?> { ["file_key"] = input.FileKey }));
        else if (!string.IsNullOrEmpty(input.Card))
            (msgType, content) = ("interactive", input.Card);
        else if (!string.IsNullOrEmpty(input.Post))
            (msgType, content) = ("post", input.Post);
        else if (!string.IsNullOrEmpty(input.ShareChatId))
            (msgType, content) = ("share_chat", Json(new Dictionary<string, string?> { ["chat_id"] = input.ShareChatId }));
        else if (!string.IsNullOrEmpty(input.ShareUserId))
            (msgType, content) = ("share_user", Json(new Dictionary<string, string?> { ["user_id"] = input.ShareUserId }));
        else if (!string.IsNullOrEmpty(input.StickerFileKey))
            (msgType, content) = ("sticker", Json(new Dictionary<string, string?> { ["file_key"] = input.StickerFileKey }));
        else if (!string.IsNullOrEmpty(input.Markdown))
        {
            // Markdown → Post 分片发送
            var chunks = ChannelNormalize.SplitWithCodeFences(input.Markdown, _config.TextChunkLimit);
            var ids = new List<string>();
            string? firstChatId = null;
            for (var i = 0; i < chunks.Count; i++)
            {
                var mentions = i == 0 ? input.Mentions : null;
                var postJson = ChannelNormalize.SimpleMarkdownToPost(input.Title, chunks[i], mentions);
                var (id, chatId) = await SendOneWithFallbackAsync(receiveIdType, receiveId, "post", postJson, input, ct);
                ids.Add(id);
                firstChatId ??= chatId;
            }
            return new ChannelSendResult(ids[0], firstChatId, ids.Count > 1 ? ids : null);
        }
        else if (!string.IsNullOrEmpty(input.Text))
        {
            var fullText = ChannelNormalize.ComposeMentionsTextPrefix(input.Mentions) + input.Text;
            var chunks = SplitPlain(fullText, _config.TextChunkLimit);
            var ids = new List<string>();
            string? firstChatId = null;
            foreach (var chunk in chunks)
            {
                var (id, chatId) = await SendOneWithFallbackAsync(receiveIdType, receiveId, "text", Json(new Dictionary<string, string?> { ["text"] = chunk }), input, ct);
                ids.Add(id);
                firstChatId ??= chatId;
            }
            return new ChannelSendResult(ids[0], firstChatId, ids.Count > 1 ? ids : null);
        }

        if (msgType.Length == 0 || content.Length == 0)
            throw new FeishuException("no valid message content provided");

        var (messageId, chat) = await SendOneWithFallbackAsync(receiveIdType, receiveId, msgType, content, input, ct);
        return new ChannelSendResult(messageId, chat);
    }

    /// <summary>发送 + 降级：目标失效（回复对象被撤）→ 去掉 reply 重发；格式错误 → 降级纯文本。</summary>
    private async Task<(string MessageId, string? ChatId)> SendOneWithFallbackAsync(
        string idType, string id, string msgType, string content, ChannelSendInput input, CancellationToken ct)
    {
        try
        {
            return await RawSendWithRetryAsync(idType, id, msgType, content, input.ReplyMessageId, ct);
        }
        catch (Exception ex)
        {
            var code = FeishuChannelException.Classify(ex);
            if (code == ChannelErrorCode.TargetRevoked && !string.IsNullOrEmpty(input.ReplyMessageId))
            {
                input.ReplyMessageId = null; // 回复对象失效 → 降级为新消息
                return await SendOneWithFallbackAsync(idType, id, msgType, content, input, ct);
            }
            if (code == ChannelErrorCode.FormatError && msgType != "text")
            {
                var fallback = input.Markdown ?? input.Text;
                if (fallback == null) throw;
                var fullText = ChannelNormalize.ComposeMentionsTextPrefix(input.Mentions) + fallback;
                return await RawSendWithRetryAsync(idType, id, "text", Json(new Dictionary<string, string?> { ["text"] = fullText }), input.ReplyMessageId, ct);
            }
            throw;
        }
    }

    /// <summary>裸发送 + 指数退避重试。仅 RateLimited / Unknown 可重试（对齐 Go outbound.Retry）。</summary>
    /// <remarks>有 ReplyMessageId 走 im/v1/messages/:id/reply，否则走 im/v1/messages 创建。</remarks>
    private async Task<(string MessageId, string? ChatId)> RawSendWithRetryAsync(
        string idType, string id, string msgType, string content, string? replyMessageId, CancellationToken ct)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= _config.RetryMaxAttempts; attempt++)
        {
            try
            {
                if (!string.IsNullOrEmpty(replyMessageId))
                {
                    var reply = await _client.Im.Message.ReplyAsync(replyMessageId, new ReplyMessageRequest
                    {
                        Body = new ReplyMessageBody { MsgType = msgType, Content = content },
                    }, null, ct);
                    if (!reply.Success) throw new FeishuCodeException(reply.Code, reply.Msg ?? "");
                    return (reply.Data?.MessageId ?? "", null);
                }

                var created = await _client.Im.Message.CreateAsync(new SendMessageRequest
                {
                    ReceiveIdType = idType,
                    Body = new SendMessageBody { ReceiveId = id, MsgType = msgType, Content = content },
                }, null, ct);
                if (!created.Success) throw new FeishuCodeException(created.Code, created.Msg ?? "");
                return (created.Data?.MessageId ?? "", null);
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt >= _config.RetryMaxAttempts) break;
                // 对齐 Go outbound.Retry：仅 RateLimited / Unknown 可重试（格式错误、目标失效等立即上抛走降级）
                var code = FeishuChannelException.Classify(ex);
                if (code is not (ChannelErrorCode.RateLimited or ChannelErrorCode.Unknown)) break;
                await Task.Delay(TimeSpan.FromMilliseconds(_config.RetryBaseDelay.TotalMilliseconds * (1 << (attempt - 1))), ct);
            }
        }
        throw lastError!;
    }

    /// <summary>纯文本按长度硬切（无围栏感知；Markdown 分片请用 ChannelNormalize.SplitWithCodeFences）。</summary>
    private static List<string> SplitPlain(string text, int limit)
    {
        if (text.Length <= limit) return [text];
        var chunks = new List<string>();
        for (var i = 0; i < text.Length; i += limit)
            chunks.Add(text.Substring(i, Math.Min(limit, text.Length - i)));
        return chunks;
    }

    /// <summary>序列化字典为 JSON（复用 SDK 统一序列化器，中文不转义）。</summary>
    private static string Json(Dictionary<string, string?> map) =>
        SystemTextJsonFeishuSerializer.Instance.Serialize(map);
}
