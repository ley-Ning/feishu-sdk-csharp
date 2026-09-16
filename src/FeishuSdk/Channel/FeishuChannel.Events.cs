using Feishu.Events;
using Feishu.Services.Im;
using Feishu.Ws;

namespace Feishu.Channel;

/// <summary>
/// FeishuChannel 分部：事件订阅注册与入站处理管道。
/// 处理顺序（对齐 Go channel）：解析归一化 → 机器人自发消息回环保护 →
/// 过期检测 → 去重 → 策略门控（不通过回调 OnReject）→ 处理锁 → 按会话批量合并分发。
/// </summary>
public sealed partial class FeishuChannel
{
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

    // ==================== 处理器注册 ====================

    /// <summary>注册消息处理器（可多次调用叠加多个）。首次注册时自动向 WS 订阅 im.message.receive_v1。</summary>
    public FeishuChannel OnMessage(Func<NormalizedMessage, CancellationToken, Task> handler)
    {
        _onMessage.Add(handler);
        WireMessage();
        return this;
    }

    /// <summary>注册表情回复处理器。首次注册时自动向 WS 订阅 im.message.reaction.created/deleted_v1。</summary>
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

    /// <summary>注册云文档评论处理器。首次注册时自动向 WS 订阅 drive.notice.comment_add_v1。</summary>
    public FeishuChannel OnComment(Func<ChannelCommentEvent, CancellationToken, Task> handler)
    {
        _onComment.Add(handler);
        if (_commentWired || _ws == null) return this;
        _commentWired = true;
        _ws.EventHandler()?.OnRaw("drive.notice.comment_add_v1", HandleComment);
        return this;
    }

    /// <summary>注册机器人进群处理器。首次注册时自动向 WS 订阅 im.chat.member.bot.added_v1。</summary>
    public FeishuChannel OnBotAdded(Func<ChannelBotAddedEvent, CancellationToken, Task> handler)
    {
        _onBotAdded.Add(handler);
        if (_botAddedWired || _ws == null) return this;
        _botAddedWired = true;
        _ws.EventHandler()?.OnRaw("im.chat.member.bot.added_v1", HandleBotAdded);
        return this;
    }

    /// <summary>注册卡片回传处理器。首次注册时自动向 WS 订阅 card_action_trigger。</summary>
    public FeishuChannel OnCardAction(Func<ChannelCardActionEvent, CancellationToken, Task> handler)
    {
        _onCardAction.Add(handler);
        if (_cardActionWired || _ws == null) return this;
        _cardActionWired = true;
        _ws.EventHandler()?.OnRaw("card_action_trigger", HandleCardAction);
        return this;
    }

    /// <summary>注册策略门控拒绝回调（同步；被拦截的消息不会进入 OnMessage）。</summary>
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

    /// <summary>im.message.receive_v1 入站入口：归一化 → 回环保护 → 过期/去重 → 门控 → 锁 → 批量分发。</summary>
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

    /// <summary>表情回复入站：按 (message_id, reaction_type, action) 去重后进会话管道。</summary>
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

    /// <summary>云文档评论入站：按 (file_token, comment_id) 去重后进会话管道。</summary>
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

    /// <summary>机器人进群入站：按 event_id 去重后进会话管道。</summary>
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

    /// <summary>卡片回传入站：按 (operator_id, action_value, timestamp) 去重后进会话管道。</summary>
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

    /// <summary>调 bot/v3/info 拉取机器人身份（非 200 / code≠0 抛异常）。</summary>
    private async Task<BotIdentity> FetchBotIdentityAsync(CancellationToken ct)
    {
        var resp = await _client.GetAsync("/open-apis/bot/v3/info", null, AccessTokenType.Tenant, cancellationToken: ct);
        if (resp.StatusCode != 200)
            throw new FeishuException($"unexpected status code from bot/v3/info: {resp.StatusCode}");
        var result = System.Text.Json.JsonSerializer.Deserialize<BotInfoResponse>(resp.RawBody)!;
        if (result.Code != 0)
            throw new FeishuCodeException(result.Code, "");
        var name = string.IsNullOrEmpty(result.Bot?.AppName) ? "bot" : result.Bot!.AppName!;
        return new BotIdentity(result.Bot?.OpenId ?? "", null, name, result.Bot?.ActivateStatus ?? 0);
    }

    /// <summary>bot/v3/info 响应壳。</summary>
    private sealed class BotInfoResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("code")]
        public int Code { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("bot")]
        public BotInfo? Bot { get; set; }
    }

    /// <summary>bot/v3/info 的 data.bot 载荷。</summary>
    private sealed class BotInfo
    {
        [System.Text.Json.Serialization.JsonPropertyName("open_id")]
        public string? OpenId { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("app_name")]
        public string? AppName { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("activate_status")]
        public int? ActivateStatus { get; set; }
    }
}
