using Feishu.Ws;

namespace Feishu.EventBus;

/// <summary>
/// WS 生命周期桥接器：把 <see cref="FeishuWsClient"/> 的 5 个 C# 事件翻译成总线生命周期事件，
/// 使连接状态变化与业务事件在同一根总线上可订阅。用法：
/// <code>using var bridge = bus.ObserveWsLifecycle(wsClient);</code>
/// </summary>
public static class WsLifecycleAdapter
{
    /// <summary>挂接 WS 客户端生命周期；返回的 IDisposable 解除挂接（一般随连接生命周期存活）。</summary>
    public static IDisposable ObserveWsLifecycle(this FeishuEventBus bus, FeishuWsClient wsClient)
    {
        var handlers = new List<IDisposable>(5);

        wsClient.OnReady += OnReady;
        wsClient.OnReconnecting += OnReconnecting;
        wsClient.OnReconnected += OnReconnected;
        wsClient.OnDisconnected += OnDisconnected;
        wsClient.OnError += OnError;

        return new BridgeDisposer(() =>
        {
            wsClient.OnReady -= OnReady;
            wsClient.OnReconnecting -= OnReconnecting;
            wsClient.OnReconnected -= OnReconnected;
            wsClient.OnDisconnected -= OnDisconnected;
            wsClient.OnError -= OnError;
            foreach (var h in handlers) h.Dispose();
        });

        void OnReady() => bus.PublishLifecycle(new FeishuLifecycleEvent(FeishuLifecycleKind.WsReady, null, DateTimeOffset.UtcNow));
        void OnReconnecting() => bus.PublishLifecycle(new FeishuLifecycleEvent(FeishuLifecycleKind.WsReconnecting, null, DateTimeOffset.UtcNow));
        void OnReconnected() => bus.PublishLifecycle(new FeishuLifecycleEvent(FeishuLifecycleKind.WsReconnected, null, DateTimeOffset.UtcNow));
        void OnDisconnected() => bus.PublishLifecycle(new FeishuLifecycleEvent(FeishuLifecycleKind.WsDisconnected, null, DateTimeOffset.UtcNow));
        void OnError(Exception ex) => bus.PublishLifecycle(new FeishuLifecycleEvent(FeishuLifecycleKind.WsError, ex, DateTimeOffset.UtcNow));
    }

    private sealed class BridgeDisposer(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
