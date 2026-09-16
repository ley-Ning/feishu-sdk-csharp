namespace Feishu.EventBus;

/// <summary>事件来源：同一根总线可以同时接入多个入站通道。</summary>
public enum FeishuEventSource
{
    /// <summary>WS 长连接（免公网端点）。</summary>
    Ws,

    /// <summary>HTTP webhook 回调（含解密/验签后的明文载荷）。</summary>
    Webhook,

    /// <summary>卡片回传（card_action_trigger）。</summary>
    Card,

    /// <summary>手工发布（测试或进程内事件）。</summary>
    Manual,
}

/// <summary>
/// 事件枢纽契约：入站载荷的注册与分发统一入口。
/// <see cref="Events.EventDispatcher"/>（webhook 场景）与 <see cref="FeishuEventBus"/>（统一总线）
/// 均实现本契约，因此 WS 客户端、Channel 等事件消费者面向契约编程，事件源可插拔。
/// </summary>
public interface IEventHub
{
    /// <summary>注册原始字节处理器（拿到完整事件 JSON 载荷）。返回的 IDisposable 用于退订。</summary>
    IDisposable SubscribeRaw(string eventType, Func<byte[], CancellationToken, Task> handler);

    /// <summary>分发一条已解密的明文事件载荷；返回 false 表示存在处理失败（WS 场景用于组 500 回执）。</summary>
    Task<bool> DispatchAsync(byte[] plainPayload, CancellationToken cancellationToken = default);
}
