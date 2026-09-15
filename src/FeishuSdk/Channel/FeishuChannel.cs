using System.Text.Json;
using Feishu.Events;
using Feishu.Services.Im;
using Feishu.Ws;

namespace Feishu.Channel;

/// <summary>
/// 高层机器人编排（对齐 Go channel.Channel）：
/// 消息归一化 → 过期/去重/处理锁 → 策略门控 → 按会话批量合并分发；
/// 发送侧统一 SendInput（自动识别 receive_id_type、Markdown→Post、长文分片、
/// 上传本地文件、回复目标失效/格式错误自动降级）；Stream 提供节流式流式回复。
/// </summary>
public sealed class FeishuChannel : IDisposable
{
    private readonly FeishuClient _client;
    private readonly FeishuWsClient? _ws;
    private readonly ChannelConfig _config;
    private readonly DedupCache _dedup;
    private readonly ChatPipelineManager _pipelines;
    private readonly PolicyGate _policyGate;
    private readonly ProcessingLock _processLock;

    private readonly object _botGate = new();
    private BotIdentity? _botIdentity;
    private DateTimeOffset _botFetchedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _botLastFailureAt = DateTimeOffset.MinValue;

    private readonly List<Func<NormalizedMessage, CancellationToken, Task>> _onMessage = new();
    private readonly List<Func<ChannelReactionEvent, CancellationToken, Task>> _onReaction = new();
    private readonly List<Func<ChannelCommentEvent, CancellationToken, Task>> _onComment = new();
    private readonly List<Func<ChannelBotAddedEvent, CancellationToken, Task>> _onBotAdded = new();
    private readonly List<Func<ChannelCardActionEvent, CancellationToken, Task>> _onCardAction = new();
    private readonly List<Action<ChannelRejectEvent>> _onReject = new();

    private bool _messageWired;
    private bool _reactionWired;
    private bool _commentWired;
    private bool _botAddedWired;
    private bool _cardActionWired;

    public FeishuChannel(FeishuClient client, FeishuWsClient? ws = null, Action<ChannelConfig>? configure = null)
    {
        _client = client;
        _ws = ws;
        _config = new ChannelConfig();
        configure?.Invoke(_config);
        _dedup = new DedupCache(_config.DedupMaxEntries, _config.DedupTtl);
        _pipelines = new ChatPipelineManager(_config.Batch);
        _policyGate = new PolicyGate(_config.Policy);
        _processLock = new ProcessingLock(TimeSpan.FromMinutes(5));
    }

    // ==================== 处理器注册 ====================

    public FeishuChannel OnMessage(Func<NormalizedMessage, CancellationToken, Task> handler)
    {
        _onMessage.Add(handler);
        WireMessage();
        return this;
    }

    public FeishuChannel OnReaction(Func<ChannelReactionEvent, CancellationToken, Task> handler)
    {
        _onReaction.Add(handler);
        if (_reactionWired || _ws == null) return this;
        _reactionWired = true;
        var dispatcher = _ws.EventHandler();
        dispatcher?.OnRaw("im.message.reaction.created_v1", (raw, ct) => HandleReaction(raw, ct));
        dispatcher?.OnRaw("im.message.reaction.deleted_v1", (raw, ct) => HandleReaction(raw, ct));
        return this;
    }

    public FeishuChannel OnComment(Func<ChannelCommentEvent, CancellationToken, Task> handler)
    {
        _onComment.Add(handler);
        if (_commentWired || _ws == null) return this;
        _commentWired = true;
        _ws.EventHandler()?.OnRaw("drive.notice.comment_add_v1", HandleComment);
        return this;
    }

    public FeishuChannel OnBotAdded(Func<ChannelBotAddedEvent, CancellationToken, Task> handler)
    {
        _onBotAdded.Add(handler);
        if (_botAddedWired || _ws == null) return this;
        _botAddedWired = true;
        _ws.EventHandler()?.OnRaw("im.chat.member.bot.added_v1", HandleBotAdded);
        return this;
    }

    public FeishuChannel OnCardAction(Func<ChannelCardActionEvent, CancellationToken, Task> handler)
    {
        _onCardAction.Add(handler);
        if (_cardActionWired || _ws == null) return this;
        _cardActionWired = true;
        _ws.EventHandler()?.OnRaw("card_action_trigger", HandleCardAction);
        return this;
    }

    public FeishuChannel OnReject(Action<ChannelRejectEvent> handler)
    {
        _onReject.Add(handler);
        return this;
    }

    private void WireMessage()
    {
        if (_messageWired || _ws == null) return;
        _messageWired = true;
        _ws.EventHandler()?.OnRaw(ImEventTypes.MessageReceiveV1, (raw, ct) => HandleMessageAsync(raw, ct));
    }

    // ==================== 入站处理管道 ====================

    internal async Task HandleMessageAsync(byte[] payload, CancellationToken ct)
    {
        if (_onMessage.Count == 0) return;
        var norm = ChannelNormalize.ParseMessage(payload);
        if (norm == null) return;

        var bot = await GetBotIdentityAsync(ct);
        if (bot != null)
        {
            if (norm.UserId == bot.OpenId) return; // 自发消息回环保护
            foreach (var m in norm.Mentions)
            {
                if (m.OpenId == bot.OpenId || m.UserId == bot.OpenId)
                {
                    norm.MentionedBot = true;
                    m.IsBot = true;
                }
            }
        }

        if (StaleDetector.IsStale(norm.CreateTimeMs, _config.StaleMessageWindow))
            return;
        if (_dedup.IsDuplicate(norm.MessageId))
            return;

        var decision = _policyGate.Evaluate(norm);
        if (!decision.Allowed)
        {
            var reject = new ChannelRejectEvent(norm.MessageId, norm.ChatId, norm.UserId, decision.Reason?.ToString() ?? "");
            foreach (var h in _onReject) h(reject);
            return;
        }

        if (!_processLock.Acquire(norm.MessageId)) return;
        _pipelines.Push(norm.ChatId, norm, async batch =>
        {
            try
            {
                foreach (var h in _onMessage)
                    await h(batch.Message, ct);
            }
            finally
            {
                foreach (var id in batch.SourceIds)
                    _processLock.Release(id);
            }
        });
    }

    private async Task HandleReaction(byte[] payload, CancellationToken ct)
    {
        if (_onReaction.Count == 0) return;
        var evt = ChannelNormalize.ParseReaction("", payload);
        if (evt == null) return;

        var key = ChannelNormalize.ReactionDedupKey(evt);
        if (_dedup.IsDuplicate(key)) return;
        if (!_processLock.Acquire(key)) return;
        try
        {
            await _pipelines.RunAsync(evt.MessageId, async () =>
            {
                foreach (var h in _onReaction)
                    await h(evt, ct);
            });
        }
        finally
        {
            _processLock.Release(key);
        }
    }

    private async Task HandleComment(byte[] payload, CancellationToken ct)
    {
        if (_onComment.Count == 0) return;
        var evt = ChannelNormalize.ParseComment(payload);
        if (evt == null || evt.CommentId.Length == 0) return;

        var key = ChannelNormalize.CommentDedupKey(evt);
        if (_dedup.IsDuplicate(key)) return;
        if (!_processLock.Acquire(key)) return;
        try
        {
            await _pipelines.RunAsync(evt.FileToken, async () =>
            {
                foreach (var h in _onComment)
                    await h(evt, ct);
            });
        }
        finally
        {
            _processLock.Release(key);
        }
    }

    private async Task HandleBotAdded(byte[] payload, CancellationToken ct)
    {
        if (_onBotAdded.Count == 0) return;
        var evt = ChannelNormalize.ParseBotAdded(payload);
        if (evt == null) return;

        if (_dedup.IsDuplicate(evt.EventId)) return;
        if (!_processLock.Acquire(evt.EventId)) return;
        try
        {
            await _pipelines.RunAsync(evt.ChatId, async () =>
            {
                foreach (var h in _onBotAdded)
                    await h(evt, ct);
            });
        }
        finally
        {
            _processLock.Release(evt.EventId);
        }
    }

    private async Task HandleCardAction(byte[] payload, CancellationToken ct)
    {
        if (_onCardAction.Count == 0) return;
        var evt = ChannelNormalize.ParseCardAction(payload);
        if (evt == null) return;

        var key = ChannelNormalize.CardActionDedupKey(evt);
        if (_dedup.IsDuplicate(key)) return;
        if (!_processLock.Acquire(key)) return;
        try
        {
            var scope = evt.ChatId.Length > 0 ? evt.ChatId : evt.MessageId;
            await _pipelines.RunAsync(scope, async () =>
            {
                foreach (var h in _onCardAction)
                    await h(evt, ct);
            });
        }
        finally
        {
            _processLock.Release(key);
        }
    }

    // ==================== 机器人身份 ====================

    /// <summary>获取并缓存机器人身份（TTL 30 分钟；失败 1 分钟内用旧值兜底，对齐 Go GetBotIdentity）。</summary>
    public async Task<BotIdentity?> GetBotIdentityAsync(CancellationToken ct = default)
    {
        bool fresh;
        lock (_botGate) fresh = _botIdentity != null && DateTimeOffset.UtcNow - _botFetchedAt < _config.BotIdentityTtl;
        if (fresh) return _botIdentity;

        lock (_botGate)
        {
            if (_botIdentity != null && DateTimeOffset.UtcNow - _botFetchedAt < _config.BotIdentityTtl)
                return _botIdentity;
            if (_botLastFailureAt != DateTimeOffset.MinValue &&
                DateTimeOffset.UtcNow - _botLastFailureAt < _config.BotIdentityMinRefreshInterval)
                return _botIdentity; // 失败节流
        }

        BotIdentity? identity;
        try
        {
            identity = await FetchBotIdentityAsync(ct);
        }
        catch
        {
            lock (_botGate) _botLastFailureAt = DateTimeOffset.UtcNow;
            return _botIdentity; // 旧值兜底
        }

        lock (_botGate)
        {
            _botIdentity = identity;
            _botFetchedAt = DateTimeOffset.UtcNow;
            _botLastFailureAt = DateTimeOffset.MinValue;
        }
        return identity;
    }

    private async Task<BotIdentity> FetchBotIdentityAsync(CancellationToken ct)
    {
        var resp = await _client.GetAsync("/open-apis/bot/v3/info", null, AccessTokenType.Tenant, cancellationToken: ct);
        if (resp.StatusCode != 200)
            throw new FeishuException($"unexpected status code from bot/v3/info: {resp.StatusCode}");
        var result = JsonSerializer.Deserialize<BotInfoResponse>(resp.RawBody)!;
        if (result.Code != 0)
            throw new FeishuCodeException(result.Code, "");
        var name = string.IsNullOrEmpty(result.Bot?.AppName) ? "bot" : result.Bot!.AppName!;
        return new BotIdentity(result.Bot?.OpenId ?? "", null, name, result.Bot?.ActivateStatus ?? 0);
    }

    private sealed class BotInfoResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("code")]
        public int Code { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("bot")]
        public BotInfo? Bot { get; set; }
    }

    private sealed class BotInfo
    {
        [System.Text.Json.Serialization.JsonPropertyName("open_id")]
        public string? OpenId { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("app_name")]
        public string? AppName { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("activate_status")]
        public int? ActivateStatus { get; set; }
    }

    // ==================== 发送 ====================

    /// <summary>结构化发送（对齐 Go channel.Send：内容优先级/分片/上传/降级）。</summary>
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

    private static List<string> SplitPlain(string text, int limit)
    {
        if (text.Length <= limit) return [text];
        var chunks = new List<string>();
        for (var i = 0; i < text.Length; i += limit)
            chunks.Add(text.Substring(i, Math.Min(limit, text.Length - i)));
        return chunks;
    }

    private static string Json(Dictionary<string, string?> map) =>
        SystemTextJsonFeishuSerializer.Instance.Serialize(map);

    private async Task<string> UploadImageAsync(string path, CancellationToken ct)
    {
        var multipart = new MultipartRequestBody()
            .AddField("image_type", "message")
            .AddFileFromPath("image", path);
        var resp = await _client.DoAsync(new ApiRequest
        {
            Method = HttpMethod.Post,
            Path = "/open-apis/im/v1/images",
            Body = multipart,
            SupportedTokenTypes = [AccessTokenType.Tenant],
        }, null, ct);
        var data = JsonSerializer.Deserialize<UploadResp>(resp.RawBody)!;
        if (data.Code != 0 || data.Data?.ImageKey == null)
            throw new FeishuCodeException(data.Code, data.Msg ?? "");
        return data.Data.ImageKey;
    }

    /// <summary>
    /// 从 URL / 本地路径 / 字节上传媒体（对齐 Go uploader.UploadMedia 的入口形态）：
    /// URL 来源先过 <see cref="SsrfGuard"/>；音频(ogg)/视频(mp4)自动探测时长。
    /// 返回 file/image key（image 走 im/v1/images，其余走 im/v1/files）。
    /// </summary>
    public async Task<(string FileKey, int? DurationMs)> UploadMediaAsync(ChannelUploadInput input, CancellationToken ct = default)
    {
        byte[] data;
        string fileName;
        if (input.SourceUrl != null)
        {
            await SsrfGuard.AssertPublicUrlAsync(input.SourceUrl, allowlist: null, ct);
            using var http = new HttpClient();
            data = await http.GetByteArrayAsync(input.SourceUrl, ct);
            fileName = input.FileName ?? new Uri(input.SourceUrl).GetComponents(UriComponents.Path, UriFormat.UriEscaped).Split('/')[^1];
        }
        else if (input.SourcePath != null)
        {
            data = await File.ReadAllBytesAsync(input.SourcePath, ct);
            fileName = input.FileName ?? Path.GetFileName(input.SourcePath);
        }
        else if (input.SourceBytes != null)
        {
            data = input.SourceBytes;
            fileName = input.FileName ?? "upload.bin";
        }
        else
        {
            throw new FeishuException("UploadMedia requires SourceUrl / SourcePath / SourceBytes");
        }

        // 时长探测：OGG 头尾页 / MP4 mvhd
        int? durationMs = null;
        if (input.Kind is "audio" or "video" or "media")
        {
            try
            {
                using var probe = new MemoryStream(data);
                durationMs = LooksLikeOgg(data) ? MediaDuration.ParseOpusDuration(probe) : MediaDuration.ParseMp4Duration(probe);
            }
            catch (FeishuChannelException)
            {
                durationMs = null; // 无法解析时不上报时长（与 Go 探测失败兜底一致）
            }
        }

        var isImage = input.Kind == "image";
        var multipart = new MultipartRequestBody()
            .AddField(isImage ? "image_type" : "file_type", isImage ? "message" : "stream");
        var key = await UploadBytesAsync(isImage, fileName, data, ct);
        return (key, durationMs);
    }

    private static bool LooksLikeOgg(byte[] data) =>
        data.Length >= 4 && data[0] == 0x4f && data[1] == 0x67 && data[2] == 0x67 && data[3] == 0x53;

    private async Task<string> UploadBytesAsync(bool isImage, string fileName, byte[] data, CancellationToken ct)
    {
        var multipart = new MultipartRequestBody()
            .AddField(isImage ? "image_type" : "file_type", isImage ? "message" : "stream")
            .AddFile(isImage ? "image" : "file", fileName, new MemoryStream(data));
        var resp = await _client.DoAsync(new ApiRequest
        {
            Method = HttpMethod.Post,
            Path = isImage ? "/open-apis/im/v1/images" : "/open-apis/im/v1/files",
            Body = multipart,
            SupportedTokenTypes = [AccessTokenType.Tenant],
        }, null, ct);
        var parsed = JsonSerializer.Deserialize<UploadResp>(resp.RawBody)!;
        if (parsed.Code != 0)
            throw new FeishuCodeException(parsed.Code, parsed.Msg ?? "");
        var key = isImage ? parsed.Data?.ImageKey : parsed.Data?.FileKey;
        if (key == null) throw new FeishuCodeException(parsed.Code, "upload returned empty key");
        return key;
    }

    private async Task<string> UploadFileAsync(string path, CancellationToken ct)
    {
        var multipart = new MultipartRequestBody()
            .AddField("file_type", "stream")
            .AddFileFromPath("file", path);
        var resp = await _client.DoAsync(new ApiRequest
        {
            Method = HttpMethod.Post,
            Path = "/open-apis/im/v1/files",
            Body = multipart,
            SupportedTokenTypes = [AccessTokenType.Tenant],
        }, null, ct);
        var data = JsonSerializer.Deserialize<UploadResp>(resp.RawBody)!;
        if (data.Code != 0 || data.Data?.FileKey == null)
            throw new FeishuCodeException(data.Code, data.Msg ?? "");
        return data.Data.FileKey;
    }

    private sealed class UploadResp
    {
        [System.Text.Json.Serialization.JsonPropertyName("code")]
        public int Code { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("msg")]
        public string? Msg { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("data")]
        public UploadData? Data { get; set; }
    }

    private sealed class UploadData
    {
        [System.Text.Json.Serialization.JsonPropertyName("image_key")]
        public string? ImageKey { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("file_key")]
        public string? FileKey { get; set; }
    }

    // ==================== 媒体下载 ====================

    /// <summary>下载媒体（mediaType: image / file / audio / video / media）。</summary>
    public async Task<byte[]> DownloadFileAsync(string fileKey, string mediaType, CancellationToken ct = default)
    {
        if (fileKey.Length == 0) throw new FeishuException("fileKey cannot be empty");

        string path;
        if (mediaType == "image") path = "/open-apis/im/v1/images/" + Uri.EscapeDataString(fileKey);
        else if (mediaType is "file" or "audio" or "video" or "media") path = "/open-apis/im/v1/files/" + Uri.EscapeDataString(fileKey);
        else throw new FeishuException($"unsupported mediaType: {mediaType}");

        var resp = await _client.GetAsync(path, null, AccessTokenType.Tenant,
            new RequestOptions().WithFileDownload(), ct);
        if (resp.StatusCode != 200)
            throw new FeishuException($"download {mediaType} failed: status {resp.StatusCode}");
        return resp.RawBody;
    }

    // ==================== 流式回复 ====================

    /// <summary>开启流式消息会话（Markdown 节流追加；Card 直接首发并支持换卡）。</summary>
    public async Task<IStreamController> StreamAsync(ChannelSendInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!string.IsNullOrEmpty(input.Card))
        {
            var res = await SendAsync(input, ct);
            return new CardStreamController(this, res.MessageId, _config);
        }

        if (string.IsNullOrEmpty(input.Markdown) && string.IsNullOrEmpty(input.Text))
            input.Markdown = "...";
        var initial = await SendAsync(input, ct);
        return new MarkdownStreamController(this, initial.MessageId, input.Markdown ?? "", input.Title, _config);
    }

    /// <summary>流式控制器（Append 追加 / UpdateCard 换卡 / Flush 立即发 / Close 收尾）。</summary>
    public interface IStreamController
    {
        Task AppendAsync(string text, CancellationToken ct = default);

        Task UpdateCardAsync(string cardJson, CancellationToken ct = default);

        Task FlushAsync(CancellationToken ct = default);

        Task CloseAsync(CancellationToken ct = default);
    }

    internal sealed class MarkdownStreamController : IStreamController, IDisposable
    {
        private readonly FeishuChannel _channel;
        private readonly string _title;
        private readonly ChannelConfig _config;
        private readonly object _gate = new();
        private string _content;
        private string _messageId;
        private int _chunkIndex;
        private readonly ThrottleController _throttle;

        public MarkdownStreamController(FeishuChannel channel, string messageId, string initialContent, string? title, ChannelConfig config)
        {
            _channel = channel;
            _messageId = messageId;
            _content = initialContent;
            _title = title ?? "";
            _config = config;
            _throttle = new ThrottleController(config.StreamThrottle, UpdateAsync);
        }

        public async Task AppendAsync(string text, CancellationToken ct = default)
        {
            lock (_gate) _content += text;
            await _throttle.TriggerAsync(ct);
        }

        public Task UpdateCardAsync(string cardJson, CancellationToken ct = default) =>
            throw new FeishuChannelException(ChannelErrorCode.FormatError, "UpdateCard is not supported for MarkdownStreamController, use Append");

        public Task FlushAsync(CancellationToken ct = default) => _throttle.FlushAsync();

        public Task CloseAsync(CancellationToken ct = default) => _throttle.CloseAsync();

        private async Task UpdateAsync()
        {
            string content, messageId;
            int currentIndex;
            lock (_gate)
            {
                content = _content;
                messageId = _messageId;
                currentIndex = _chunkIndex;
            }

            var chunks = ChannelNormalize.SplitWithCodeFences(content, _config.TextChunkLimit);
            if (chunks.Count == 0) return;

            var targetIndex = chunks.Count - 1;
            var postJson = ChannelNormalize.SimpleMarkdownToPost(_title, chunks[targetIndex], null);

            if (targetIndex > currentIndex)
            {
                // 新分片：回复上一条
                var reply = await _channel._client.Im.Message.ReplyAsync(messageId, new ReplyMessageRequest
                {
                    Body = new ReplyMessageBody { MsgType = "post", Content = postJson },
                });
                if (!reply.Success) throw new FeishuCodeException(reply.Code, reply.Msg ?? "");
                lock (_gate)
                {
                    _messageId = reply.Data?.MessageId ?? messageId;
                    _chunkIndex = targetIndex;
                }
                return;
            }

            var patch = await _channel._client.Im.Message.PatchAsync(messageId, new PatchMessageRequest
            {
                Body = new PatchMessageBody { Content = postJson },
            });
            if (!patch.Success) throw new FeishuCodeException(patch.Code, patch.Msg ?? "");
        }

        public void Dispose() => _throttle.Dispose();
    }

    internal sealed class CardStreamController : IStreamController, IDisposable
    {
        private readonly FeishuChannel _channel;
        private readonly string _messageId;
        private string? _pendingCard;
        private readonly ThrottleController _throttle;

        public CardStreamController(FeishuChannel channel, string messageId, ChannelConfig config)
        {
            _channel = channel;
            _messageId = messageId;
            _throttle = new ThrottleController(config.StreamThrottle, UpdateAsync);
        }

        public Task AppendAsync(string text, CancellationToken ct = default) =>
            throw new FeishuChannelException(ChannelErrorCode.FormatError, "Append is not supported for CardStreamController, use UpdateCard");

        public async Task UpdateCardAsync(string cardJson, CancellationToken ct = default)
        {
            _pendingCard = cardJson;
            await _throttle.TriggerAsync(ct);
        }

        public Task FlushAsync(CancellationToken ct = default) => _throttle.FlushAsync();

        public Task CloseAsync(CancellationToken ct = default) => _throttle.CloseAsync();

        private async Task UpdateAsync()
        {
            var card = _pendingCard;
            if (card == null) return;
            var patch = await _channel._client.Im.Message.PatchAsync(_messageId, new PatchMessageRequest
            {
                Body = new PatchMessageBody { Content = card },
            });
            if (!patch.Success) throw new FeishuCodeException(patch.Code, patch.Msg ?? "");
        }

        public void Dispose() => _throttle.Dispose();
    }

    // ==================== 生命周期 ====================

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_ws == null) return Task.CompletedTask;
        return _ws.StartAsync(ct);
    }

    public async Task StopAsync()
    {
        if (_ws != null) await _ws.ShutdownAsync();
        await _pipelines.FlushAllAsync();
    }

    public void UpdatePolicy(Action<ChannelPolicyConfig> mutate) => _policyGate.UpdateConfig(mutate);

    public ChannelPolicyConfig GetPolicy() => _policyGate.GetConfig();

    internal DedupCache DedupForTest => _dedup;

    internal ChatPipelineManager PipelinesForTest => _pipelines;

    public void Dispose() => _pipelines.Dispose();
}
