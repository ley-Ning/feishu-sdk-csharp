using System.Text.Json;

namespace Feishu.EventBus;

/// <summary>
/// 统一事件信封：无论来自 WS、webhook 还是卡片回传，订阅者拿到的都是同一形态——
/// header 元数据 + 原始载荷 + 来源 + <see cref="As{T}"/> 强类型视图。
/// </summary>
public sealed class FeishuEventEnvelope
{
    private static readonly JsonSerializerOptions Options = SystemTextJsonFeishuSerializer.Instance.Options;

    /// <summary>事件类型（header.event_type，如 im.message.receive_v1）。</summary>
    public string EventType { get; init; } = "";

    /// <summary>事件 id（去重/排查用）。</summary>
    public string EventId { get; init; } = "";

    /// <summary>事件产生时间（毫秒字符串，服务端原始形态）。</summary>
    public string CreateTime { get; init; } = "";

    /// <summary>发布应用 id（事件订阅模式）。</summary>
    public string AppId { get; init; } = "";

    /// <summary>租户 key。</summary>
    public string TenantKey { get; init; } = "";

    /// <summary>verification token（webhook 模式）。</summary>
    public string Token { get; init; } = "";

    /// <summary>入站通道。</summary>
    public FeishuEventSource Source { get; init; }

    /// <summary>完整明文事件 JSON（含 header 与 event 两段）。</summary>
    public ReadOnlyMemory<byte> RawPayload { get; init; }

    /// <summary>本地接收时刻。</summary>
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>把 event 段反序列化为强类型视图（如 P2MessageReceiveV1）。</summary>
    public T? As<T>() where T : class
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<Events.FuzzyEvent>(RawPayload.Span, Options);
            if (envelope?.Event is { ValueKind: JsonValueKind.Object } eventJson)
                return JsonSerializer.Deserialize<T>(eventJson.GetRawText(), Options);
            // v1 裸事件：整体反序列化
            return JsonSerializer.Deserialize<T>(RawPayload.Span, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
