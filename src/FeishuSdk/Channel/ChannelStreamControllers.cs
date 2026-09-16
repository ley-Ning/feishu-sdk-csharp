using Feishu.Services.Im;

namespace Feishu.Channel;

/// <summary>
/// Markdown 流式控制器：Append 的内容按代码块围栏重新分片——
/// 已有分片用 PATCH 原地更新最后一条，产生新分片时用 REPLY 接续（消息串成长龙）。
/// 发送节奏由 <see cref="ThrottleController"/> 节流，Close 时冲刷收尾。
/// </summary>
internal sealed class MarkdownStreamController : FeishuChannel.IStreamController, IDisposable
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

    /// <summary>追加文本并按节流间隔触发出书（PATCH 或 REPLY）。</summary>
    public async Task AppendAsync(string text, CancellationToken ct = default)
    {
        lock (_gate) _content += text;
        await _throttle.TriggerAsync(ct);
    }

    /// <summary>Markdown 流不支持换卡（用 Card 输入开启的流才支持）。</summary>
    public Task UpdateCardAsync(string cardJson, CancellationToken ct = default) =>
        throw new FeishuChannelException(ChannelErrorCode.FormatError, "UpdateCard is not supported for MarkdownStreamController, use Append");

    /// <summary>跳过节流间隔立即出书。</summary>
    public Task FlushAsync(CancellationToken ct = default) => _throttle.FlushAsync();

    /// <summary>结束流式会话：冲刷缓冲并释放节流器。</summary>
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
            var reply = await _channel.Client.Im.Message.ReplyAsync(messageId, new ReplyMessageRequest
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

        var patch = await _channel.Client.Im.Message.PatchAsync(messageId, new PatchMessageRequest
        {
            Body = new PatchMessageBody { Content = postJson },
        });
        if (!patch.Success) throw new FeishuCodeException(patch.Code, patch.Msg ?? "");
    }

    public void Dispose() => _throttle.Dispose();
}

/// <summary>
/// 卡片流式控制器：Card 输入首发后，UpdateCard 整卡替换（PATCH interactive），
/// 节流由 <see cref="ThrottleController"/> 保证高频换卡不轰接口。
/// </summary>
internal sealed class CardStreamController : FeishuChannel.IStreamController, IDisposable
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

    /// <summary>卡片流不支持追加文本（用 Markdown 输入开启的流才支持）。</summary>
    public Task AppendAsync(string text, CancellationToken ct = default) =>
        throw new FeishuChannelException(ChannelErrorCode.FormatError, "Append is not supported for CardStreamController, use UpdateCard");

    /// <summary>整体替换卡片 JSON（节流窗口内多次调用只发最后一张）。</summary>
    public async Task UpdateCardAsync(string cardJson, CancellationToken ct = default)
    {
        _pendingCard = cardJson;
        await _throttle.TriggerAsync(ct);
    }

    /// <summary>跳过节流间隔立即换卡。</summary>
    public Task FlushAsync(CancellationToken ct = default) => _throttle.FlushAsync();

    /// <summary>结束流式会话：冲刷缓冲并释放节流器。</summary>
    public Task CloseAsync(CancellationToken ct = default) => _throttle.CloseAsync();

    private async Task UpdateAsync()
    {
        var card = _pendingCard;
        if (card == null) return;
        var patch = await _channel.Client.Im.Message.PatchAsync(_messageId, new PatchMessageRequest
        {
            Body = new PatchMessageBody { Content = card },
        });
        if (!patch.Success) throw new FeishuCodeException(patch.Code, patch.Msg ?? "");
    }

    public void Dispose() => _throttle.Dispose();
}
