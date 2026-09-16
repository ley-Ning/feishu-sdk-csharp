using System.Text.Json;

namespace Feishu.EventBus;

/// <summary>WS 连接生命周期事件种类（由 <see cref="WsLifecycleAdapter"/> 从 FeishuWsClient 桥接）。</summary>
public enum FeishuLifecycleKind
{
    /// <summary>首次建连成功。</summary>
    WsReady,

    /// <summary>连接断开，即将开始重连（含首次随机抖动）。</summary>
    WsReconnecting,

    /// <summary>重连成功。</summary>
    WsReconnected,

    /// <summary>连接断开且不再重连（主动停机或致命错误）。</summary>
    WsDisconnected,

    /// <summary>连接/处理过程出错（不中断重连循环）。</summary>
    WsError,
}

/// <summary>生命周期事件载荷。</summary>
/// <param name="Kind">事件种类。</param>
/// <param name="Error">WsError 时的异常（其余为 null）。</param>
/// <param name="OccurredAt">发生时刻。</param>
public sealed record FeishuLifecycleEvent(FeishuLifecycleKind Kind, Exception? Error, DateTimeOffset OccurredAt);

/// <summary>处理器异常事件（一个 handler 抛错不影响其他 handler，但会在此可观测）。</summary>
public sealed record FeishuHandlerErrorEvent(string EventType, FeishuEventSource Source, Exception Error);

/// <summary>
/// 统一事件总线：一切入站流量（WS / webhook / 卡片回传 / 手工发布）皆事件，
/// 在同一根总线上订阅、过滤与组合。架构角色——
/// <list type="bullet">
/// <item>强类型订阅：<see cref="Subscribe{TEvent}"/>（拿到信封 + 强类型视图）；</item>
/// <item>原始订阅：<see cref="SubscribeRaw"/>（完整 JSON 字节，实现 <see cref="IEventHub"/> 契约）；</item>
/// <item>通配订阅：<see cref="SubscribePattern"/>（如 "im.message.*"）与 <see cref="SubscribeAll"/>；</item>
/// <item>生命周期：<see cref="OnLifecycle"/>（连接状态变化）与 <see cref="OnHandlerError"/>（异常可观测）；</item>
/// <item>流式消费：<see cref="AsObservable"/>（IObservable，Rx 兼容）。</item>
/// </list>
/// 分发语义（对齐 Go ws 回执）：任一订阅者抛错 → 其余订阅者仍执行、<see cref="IEventHub.DispatchAsync"/> 返回 false（WS 回执 500）；无订阅者 → 返回 true（回执 200，避免无谓重推）。
/// </summary>
public sealed class FeishuEventBus : IEventHub
{
    private static readonly JsonSerializerOptions Options = SystemTextJsonFeishuSerializer.Instance.Options;

    private readonly object _gate = new();
    private readonly List<TypedEntry> _typed = new();
    private readonly List<RawEntry> _raw = new();
    private readonly List<PatternEntry> _patterns = new();
    private readonly List<AllEntry> _all = new();
    private readonly List<Action<FeishuLifecycleEvent>> _lifecycle = new();
    private readonly List<Action<FeishuHandlerErrorEvent>> _handlerErrors = new();

    private readonly record struct TypedEntry(string EventType, Type ArgType, Func<FeishuEventEnvelope, CancellationToken, Task> Invoke);
    private readonly record struct RawEntry(string EventType, Func<byte[], CancellationToken, Task> Invoke);
    private readonly record struct PatternEntry(string Prefix, Func<FeishuEventEnvelope, CancellationToken, Task> Invoke);
    private readonly record struct AllEntry(Func<FeishuEventEnvelope, CancellationToken, Task> Invoke);

    /// <summary>注册强类型订阅：信封携带元数据，payload 反序列化为 TEvent。</summary>
    /// <typeparam name="TEvent">event 段的强类型（如 P2MessageReceiveV1）。</typeparam>
    /// <returns>退订句柄。</returns>
    public IDisposable Subscribe<TEvent>(string eventType, Func<FeishuEventEnvelope, TEvent?, CancellationToken, Task> handler) where TEvent : class
    {
        var entry = new TypedEntry(eventType, typeof(TEvent),
            (envelope, ct) => handler(envelope, envelope.As<TEvent>(), ct));
        lock (_gate) _typed.Add(entry);
        return Unsubscribe(_typed, entry);
    }

    /// <summary>注册原始字节订阅（完整事件 JSON；实现 <see cref="IEventHub"/> 契约）。</summary>
    public IDisposable SubscribeRaw(string eventType, Func<byte[], CancellationToken, Task> handler)
    {
        var entry = new RawEntry(eventType, handler);
        lock (_gate) _raw.Add(entry);
        return Unsubscribe(_raw, entry);
    }

    /// <summary>注册前缀通配订阅：pattern 以 * 结尾按前缀匹配（"im.message.*" 命中 im.message. 下全部事件）。</summary>
    public IDisposable SubscribePattern(string pattern, Func<FeishuEventEnvelope, CancellationToken, Task> handler)
    {
        if (!pattern.EndsWith('*'))
            throw new ArgumentException("pattern must end with '*' (e.g. 'im.message.*')", nameof(pattern));
        var entry = new PatternEntry(pattern[..^1], handler);
        lock (_gate) _patterns.Add(entry);
        return Unsubscribe(_patterns, entry);
    }

    /// <summary>订阅所有事件（无论类型与来源）。</summary>
    public IDisposable SubscribeAll(Func<FeishuEventEnvelope, CancellationToken, Task> handler)
    {
        var entry = new AllEntry(handler);
        lock (_gate) _all.Add(entry);
        return Unsubscribe(_all, entry);
    }

    /// <summary>订阅 WS 连接生命周期（经 <see cref="WsLifecycleAdapter.ObserveWsLifecycle"/> 桥接）。</summary>
    public IDisposable OnLifecycle(Action<FeishuLifecycleEvent> handler)
    {
        lock (_gate) _lifecycle.Add(handler);
        return Unsubscribe(_lifecycle, handler);
    }

    /// <summary>订阅处理器异常（默认静默隔离；挂此回调可做告警/日志）。</summary>
    public IDisposable OnHandlerError(Action<FeishuHandlerErrorEvent> handler)
    {
        lock (_gate) _handlerErrors.Add(handler);
        return Unsubscribe(_handlerErrors, handler);
    }

    /// <summary>把总线暴露为可观察事件流（冷订阅：只观察订阅之后发布的事件；不引入 Rx 依赖）。</summary>
    public IObservable<FeishuEventEnvelope> AsObservable() => new BusObservable(this);

    /// <summary>手工发布一条事件载荷（测试或进程内事件注入；来源标记为 Manual）。</summary>
    public Task<bool> PublishAsync(byte[] plainPayload, CancellationToken ct = default) =>
        DispatchAsync(plainPayload, ct);

    /// <summary>发布生命周期事件（供适配器调用；业务代码一般用 <see cref="OnLifecycle"/> 订阅）。</summary>
    public void PublishLifecycle(FeishuLifecycleEvent lifecycle)
    {
        Action<FeishuLifecycleEvent>[] snapshot;
        lock (_gate) snapshot = [.. _lifecycle];
        foreach (var h in snapshot)
        {
            try { h(lifecycle); }
            catch { /* 生命周期订阅者异常不扩散 */ }
        }
    }

    /// <summary>分发一条明文事件载荷（实现 <see cref="IEventHub"/>：WS/webhook 适配器由此进入）。</summary>
    public async Task<bool> DispatchAsync(byte[] plainPayload, CancellationToken cancellationToken = default)
    {
        FeishuEventEnvelope envelope;
        try
        {
            envelope = BuildEnvelope(plainPayload, FeishuEventSource.Ws);
        }
        catch (JsonException)
        {
            return false;
        }
        if (envelope.EventType.Length == 0)
            return false;

        List<TypedEntry> typed; List<RawEntry> raw; List<PatternEntry> patterns; List<AllEntry> all;
        lock (_gate)
        {
            typed = [.. _typed];
            raw = [.. _raw];
            patterns = [.. _patterns];
            all = [.. _all];
        }

        var allOk = true;

        foreach (var entry in typed)
            if (entry.EventType == envelope.EventType)
                allOk &= await InvokeSafeAsync(entry.Invoke, envelope, cancellationToken);

        foreach (var entry in raw)
            if (entry.EventType == envelope.EventType)
                allOk &= await InvokeRawSafeAsync(entry.Invoke, plainPayload, cancellationToken);

        foreach (var entry in patterns)
            if (envelope.EventType.StartsWith(entry.Prefix, StringComparison.Ordinal))
                allOk &= await InvokeSafeAsync(entry.Invoke, envelope, cancellationToken);

        foreach (var entry in all)
            allOk &= await InvokeSafeAsync(entry.Invoke, envelope, cancellationToken);

        return allOk;
    }

    private async Task<bool> InvokeSafeAsync(Func<FeishuEventEnvelope, CancellationToken, Task> invoke, FeishuEventEnvelope envelope, CancellationToken ct)
    {
        try
        {
            await invoke(envelope, ct);
            return true;
        }
        catch (Exception ex)
        {
            ReportHandlerError(new FeishuHandlerErrorEvent(envelope.EventType, envelope.Source, ex));
            return false;
        }
    }

    private async Task<bool> InvokeRawSafeAsync(Func<byte[], CancellationToken, Task> invoke, byte[] payload, CancellationToken ct)
    {
        try
        {
            await invoke(payload, ct);
            return true;
        }
        catch (Exception ex)
        {
            ReportHandlerError(new FeishuHandlerErrorEvent("", FeishuEventSource.Ws, ex));
            return false;
        }
    }

    private void ReportHandlerError(FeishuHandlerErrorEvent error)
    {
        Action<FeishuHandlerErrorEvent>[] snapshot;
        lock (_gate) snapshot = [.. _handlerErrors];
        foreach (var h in snapshot)
        {
            try { h(error); }
            catch { /* 告警订阅者异常不扩散 */ }
        }
    }

    private static FeishuEventEnvelope BuildEnvelope(byte[] plainPayload, FeishuEventSource source)
    {
        var envelope = JsonSerializer.Deserialize<Events.FuzzyEvent>(plainPayload, Options);
        var header = envelope?.Header;
        return new FeishuEventEnvelope
        {
            EventType = header?.EventType ?? "",
            EventId = header?.EventId ?? "",
            CreateTime = header?.CreateTime ?? "",
            AppId = header?.AppId ?? "",
            TenantKey = header?.TenantKey ?? "",
            Token = header?.Token ?? "",
            Source = source,
            RawPayload = plainPayload,
        };
    }

    private IDisposable Unsubscribe<T>(List<T> list, T entry) => new Unsubscriber(() =>
    {
        lock (_gate) list.Remove(entry);
    });

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    /// <summary>总线 → IObservable 桥（冷订阅：每个观察者独立挂一个 SubscribeAll）。</summary>
    private sealed class BusObservable(FeishuEventBus bus) : IObservable<FeishuEventEnvelope>
    {
        public IDisposable Subscribe(IObserver<FeishuEventEnvelope> observer) =>
            bus.SubscribeAll(async (envelope, _) =>
            {
                observer.OnNext(envelope);
                await Task.CompletedTask;
            });
    }
}
