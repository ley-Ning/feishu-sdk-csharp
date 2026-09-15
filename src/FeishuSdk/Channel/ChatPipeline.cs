using System.Threading.Channels;

namespace Feishu.Channel;

/// <summary>
/// 单作用域流水线（对齐 Go pipeline.ChatPipeline）：去抖批量合并 + 串行执行。
/// - Push 进缓冲；满 MaxMessages/MaxChars 立即冲刷，否则按 Delay（长文 LongDelay）定时冲刷。
/// - Run：绕过批量、按队列串行执行任务（同作用域互斥）。
/// - 冲刷时把缓冲合并为一条消息（内容以 \n\n 连接，资源/提及去重合并）。
/// </summary>
public sealed class ChatPipeline : IDisposable
{
    private readonly object _gate = new();
    private readonly ChannelBatchConfig _config;
    private readonly bool _serialOnly;
    private readonly System.Threading.Channels.Channel<Func<Task>> _tasks =
        System.Threading.Channels.Channel.CreateBounded<Func<Task>>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.Wait });
    private readonly Task _worker;

    private List<NormalizedMessage> _buffer = new();
    private int _bufferChars;
    private Timer? _timer;
    private Func<BatchedDispatch, Task>? _pendingHandler;
    private bool _disposed;

    public ChatPipeline(ChannelBatchConfig config, bool serialOnly)
    {
        _config = config;
        _serialOnly = serialOnly;
        _worker = Task.Run(WorkerLoopAsync);
    }

    private async Task WorkerLoopAsync()
    {
        await foreach (var task in _tasks.Reader.ReadAllAsync())
        {
            try { await task(); }
            catch (Exception) { /* 与 Go 一致：处理器错误不中断队列 */ }
        }
    }

    public void Push(NormalizedMessage msg, Func<BatchedDispatch, Task> handler)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _buffer.Add(msg);
            _bufferChars += msg.Content.Length;
            _pendingHandler ??= handler;

            if (_buffer.Count >= _config.MaxMessages || _bufferChars >= _config.MaxChars)
            {
                ClearTimer();
                EnqueueFlushLocked();
                return;
            }

            if (_config.Delay <= TimeSpan.Zero || _serialOnly)
            {
                ClearTimer();
                EnqueueFlushLocked();
                return;
            }

            ClearTimer();
            var delay = _bufferChars >= _config.LongThresholdChars ? _config.LongDelay : _config.Delay;
            _timer = new Timer(_ =>
            {
                lock (_gate)
                {
                    _timer?.Dispose();
                    _timer = null;
                    EnqueueFlushLocked();
                }
            }, null, delay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>同作用域串行执行（先冲刷缓冲中的批量）。</summary>
    public async Task RunAsync(Func<Task> task)
    {
        lock (_gate)
        {
            if (_buffer.Count > 0)
            {
                ClearTimer();
                EnqueueFlushLocked();
            }
        }

        var tcs = new TaskCompletionSource();
        await _tasks.Writer.WriteAsync(async () =>
        {
            await task();
            tcs.TrySetResult();
        });
        await tcs.Task;
    }

    /// <summary>立即冲刷并等待队列排空。</summary>
    public async Task FlushNowAsync()
    {
        lock (_gate)
        {
            if (_buffer.Count > 0)
            {
                ClearTimer();
                EnqueueFlushLocked();
            }
        }
        var tcs = new TaskCompletionSource();
        await _tasks.Writer.WriteAsync(() => { tcs.TrySetResult(); return Task.CompletedTask; });
        await tcs.Task;
    }

    public bool IsIdle
    {
        get { lock (_gate) return _buffer.Count == 0 && _timer == null; }
    }

    private void EnqueueFlushLocked()
    {
        if (_buffer.Count == 0) return;

        var batch = _buffer;
        var handler = _pendingHandler;
        _buffer = new List<NormalizedMessage>();
        _bufferChars = 0;
        _pendingHandler = null;
        if (handler == null) return;

        var dispatch = new BatchedDispatch(MergeBatch(batch), batch.Select(m => m.MessageId).ToList());
        _ = _tasks.Writer.WriteAsync(() => handler(dispatch)).AsTask();
    }

    private void ClearTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>合并批量（对齐 Go mergeBatch：取最后一条为骨架，内容 \n\n 连接，提及/资源去重）。</summary>
    internal static NormalizedMessage MergeBatch(IReadOnlyList<NormalizedMessage> batch)
    {
        if (batch.Count == 1) return batch[0];

        var merged = batch[^1];
        var content = string.Join("\n\n", batch.Where(m => m.Content.Length > 0).Select(m => m.Content));
        var mentionAll = batch.Any(m => m.MentionAll);
        var mentionedBot = batch.Any(m => m.MentionedBot);

        var seenResources = new HashSet<string>();
        var resources = new List<ChannelResource>();
        var seenMentions = new HashSet<string>();
        var mentions = new List<ChannelMention>();
        foreach (var m in batch)
        {
            foreach (var r in m.Resources)
                if (seenResources.Add(r.FileKey))
                    resources.Add(r);
            foreach (var mn in m.Mentions)
            {
                var id = mn.UserId.Length > 0 ? mn.UserId : mn.Name;
                if (seenMentions.Add(id))
                    mentions.Add(mn);
            }
        }

        return new NormalizedMessage
        {
            EventId = merged.EventId,
            MessageId = merged.MessageId,
            ChatId = merged.ChatId,
            ChatType = merged.ChatType,
            UserId = merged.UserId,
            Content = content,
            RawContentType = merged.RawContentType,
            MentionAll = mentionAll,
            MentionedBot = mentionedBot,
            Resources = resources,
            Mentions = mentions,
            CreateTimeMs = merged.CreateTimeMs,
        };
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ClearTimer();
            if (_buffer.Count > 0) EnqueueFlushLocked();
        }
        _tasks.Writer.TryComplete();
    }
}

/// <summary>按作用域（chatId/messageId/fileToken）管理多条流水线（对齐 Go ChatPipelineManager）。</summary>
public sealed class ChatPipelineManager : IDisposable
{
    private readonly object _gate = new();
    private readonly ChannelBatchConfig _config;
    private readonly Dictionary<string, ChatPipeline> _pipelines = new();

    public ChatPipelineManager(ChannelBatchConfig config) => _config = config;

    private ChatPipeline GetOrCreate(string scope, bool serialOnly)
    {
        lock (_gate)
        {
            if (!_pipelines.TryGetValue(scope, out var p))
                _pipelines[scope] = p = new ChatPipeline(_config, serialOnly);
            return p;
        }
    }

    public void Push(string scope, NormalizedMessage msg, Func<BatchedDispatch, Task> handler) =>
        GetOrCreate(scope, false).Push(msg, handler);

    public Task RunAsync(string scope, Func<Task> task) => GetOrCreate(scope, true).RunAsync(task);

    public async Task FlushAllAsync()
    {
        List<ChatPipeline> all;
        lock (_gate) all = _pipelines.Values.ToList();
        await Task.WhenAll(all.Select(p => p.FlushNowAsync()));
    }

    public void Dispose()
    {
        List<ChatPipeline> all;
        lock (_gate)
        {
            all = _pipelines.Values.ToList();
            _pipelines.Clear();
        }
        foreach (var p in all) p.Dispose();
    }
}

/// <summary>
/// 节流控制器（对齐 Go stream.throttleController）：间隔未到则挂定时器合并更新。
/// </summary>
internal sealed class ThrottleController : IDisposable
{
    private readonly object _gate = new();
    private readonly TimeSpan _interval;
    private readonly Func<Task> _exec;
    private DateTimeOffset _lastExec = DateTimeOffset.MinValue;
    private Timer? _timer;
    private bool _closed;

    public ThrottleController(TimeSpan interval, Func<Task> exec)
    {
        _interval = interval <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(500) : interval;
        _exec = exec;
    }

    public Task TriggerAsync() => TriggerAsync(CancellationToken.None);

    public async Task TriggerAsync(CancellationToken cancellationToken)
    {
        Timer? pendingTimer;
        lock (_gate)
        {
            if (_closed) throw new FeishuChannelException(ChannelErrorCode.Unknown, "stream is closed");
            var now = DateTimeOffset.UtcNow;
            if (now - _lastExec >= _interval)
            {
                _timer?.Dispose();
                _timer = null;
                _lastExec = now;
                pendingTimer = null;
            }
            else
            {
                if (_timer == null)
                {
                    var wait = _interval - (now - _lastExec);
                    _timer = new Timer(_ =>
                    {
                        Func<Task> run;
                        lock (_gate)
                        {
                            if (_closed) return;
                            _timer?.Dispose();
                            _timer = null;
                            _lastExec = DateTimeOffset.UtcNow;
                            run = _exec;
                        }
                        _ = run();
                    }, null, wait, Timeout.InfiniteTimeSpan);
                }
                pendingTimer = _timer;
            }
        }
        if (pendingTimer != null) return; // 已排队
        await _exec();
    }

    public async Task FlushAsync()
    {
        lock (_gate)
        {
            if (_closed) throw new FeishuChannelException(ChannelErrorCode.Unknown, "stream is closed");
            _timer?.Dispose();
            _timer = null;
            _lastExec = DateTimeOffset.UtcNow;
        }
        await _exec();
    }

    public async Task CloseAsync()
    {
        bool runLast;
        lock (_gate)
        {
            if (_closed) return;
            runLast = _timer != null;
            _timer?.Dispose();
            _timer = null;
            _closed = true;
        }
        if (runLast) await _exec(); // 有挂起更新则补发一次
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _closed = true;
        }
    }
}
