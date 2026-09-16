using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Feishu.Events;

/// <summary>入站 webhook 请求。</summary>
public sealed class EventRequest
{
    /// <summary>header 名不区分大小写。</summary>
    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    public required byte[] Body { get; init; }

    public string Path { get; init; } = "";

    public string RequestId =>
        TryHeader("X-Tt-Logid") ?? TryHeader("X-Request-Id") ?? "";

    internal string? TryHeader(string name) =>
        Headers.TryGetValue(name, out var v) ? v : null;
}

/// <summary>出站 webhook 响应。</summary>
public sealed class EventResponse
{
    public static EventResponse Json(int statusCode, string body) => new()
    {
        StatusCode = statusCode,
        ContentType = "application/json; charset=utf-8",
        Body = Encoding.UTF8.GetBytes(body),
    };

    public static EventResponse Success => Json(200, @"{""msg"":""success""}");

    public required int StatusCode { get; init; }

    public string ContentType { get; init; } = "application/json; charset=utf-8";

    public required byte[] Body { get; init; }
}

/// <summary>webhook 处理器公共接口（事件分发器与卡片处理器共用同一端点适配）。</summary>
public interface IWebhookHandler
{
    Task<EventResponse> HandleAsync(EventRequest request, CancellationToken cancellationToken = default);
}

/// <summary>v2 事件信封（schema = "2.0"）。</summary>
public sealed class EventV2<T>
{
    [JsonPropertyName("schema")]
    public string? Schema { get; set; }

    [JsonPropertyName("header")]
    public EventV2Header? Header { get; set; }

    [JsonPropertyName("event")]
    public T? Event { get; set; }
}

public sealed class EventV2Header
{
    [JsonPropertyName("event_id")]
    public string? EventId { get; set; }

    [JsonPropertyName("event_type")]
    public string? EventType { get; set; }

    [JsonPropertyName("create_time")]
    public string? CreateTime { get; set; }

    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("app_id")]
    public string? AppId { get; set; }

    [JsonPropertyName("tenant_key")]
    public string? TenantKey { get; set; }
}

internal sealed class FuzzyEvent
{
    [JsonPropertyName("encrypt")]
    public string? Encrypt { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("challenge")]
    public string? Challenge { get; set; }

    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("schema")]
    public string? Schema { get; set; }

    [JsonPropertyName("header")]
    public EventV2Header? Header { get; set; }

    [JsonPropertyName("event")]
    public JsonElement? Event { get; set; }
}

/// <summary>app_ticket 推送事件（ISV 应用必须处理，SDK 默认已处理）。</summary>
public sealed class AppTicketEvent
{
    [JsonPropertyName("app_ticket")]
    public string? AppTicket { get; set; }
}

/// <summary>
/// 事件分发器：解密 → 验签 → challenge → 按 header.event_type 分发到强类型处理器。
/// 同一事件类型允许多个处理器（按注册顺序执行）；回调处理器（OnCallback）返回值作为响应体。
/// 实现 <see cref="EventBus.IEventHub"/> 契约，可直接绑定为 WS 客户端的事件枢纽；
/// 需要通配订阅/生命周期/IObservable 时改用 <see cref="EventBus.FeishuEventBus"/>。
/// </summary>
public sealed class EventDispatcher : IWebhookHandler, EventBus.IEventHub
{
    public const string ReqTypeChallenge = "url_verification";

    private readonly object _gate = new();
    private readonly Dictionary<string, List<HandlerEntry>> _handlers = new();
    private readonly Dictionary<string, CallbackEntry> _callbacks = new();
    private readonly string? _verificationToken;
    private readonly string? _encryptKey;
    private readonly IFeishuSerializer _serializer;
    private readonly IFeishuLogger _logger;

    /// <summary>跳过签名校验（本地调试）。对齐 Go Config.SkipSignVerify。</summary>
    public bool SkipSignVerify { get; set; }

    /// <summary>app_ticket 写入回调（由 FeishuClient 关联 AppTicketManager）。</summary>
    internal Func<string, CancellationToken, Task>? AppTicketSink { get; set; }

    private readonly record struct HandlerEntry(Type EventType, Func<object, CancellationToken, Task> Invoke, object? Tag = null);

    private sealed record CallbackEntry(Type EventType, Func<object, CancellationToken, Task<object?>> Invoke);

    public EventDispatcher(
        string? verificationToken = null,
        string? encryptKey = null,
        IFeishuSerializer? serializer = null,
        IFeishuLogger? logger = null)
    {
        _verificationToken = verificationToken;
        _encryptKey = encryptKey;
        _serializer = serializer ?? SystemTextJsonFeishuSerializer.Instance;
        _logger = logger ?? NullFeishuLogger.Instance;
    }

    /// <summary>注册强类型事件处理器。事件类型见各服务的事件常量（如 ImEventTypes）。</summary>
    public EventDispatcher On<TEvent>(string eventType, Func<TEvent, CancellationToken, Task> handler) where TEvent : class
    {
        lock (_gate)
        {
            if (!_handlers.TryGetValue(eventType, out var list))
                _handlers[eventType] = list = new List<HandlerEntry>();
            list.Add(new HandlerEntry(typeof(TEvent), async (raw, ct) => await handler((TEvent)raw, ct)));
        }
        return this;
    }

    /// <summary>注册原始字节处理器（自定义事件 / 需要完整报文的场景）。</summary>
    public EventDispatcher OnRaw(string eventType, Func<byte[], CancellationToken, Task> handler)
    {
        lock (_gate)
        {
            if (!_handlers.TryGetValue(eventType, out var list))
                _handlers[eventType] = list = new List<HandlerEntry>();
            list.Add(new HandlerEntry(typeof(byte[]), async (raw, ct) => await handler((byte[])raw, ct), Tag: handler));
        }
        return this;
    }

    /// <summary>IEventHub 契约实现：与 <see cref="OnRaw"/> 等价，但返回退订句柄（供 Channel 等按契约接线）。</summary>
    IDisposable EventBus.IEventHub.SubscribeRaw(string eventType, Func<byte[], CancellationToken, Task> handler)
    {
        OnRaw(eventType, handler);
        return new RawUnsubscriber(this, eventType, handler);
    }

    private sealed class RawUnsubscriber(EventDispatcher owner, string eventType, Func<byte[], CancellationToken, Task> handler) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate)
            {
                if (owner._handlers.TryGetValue(eventType, out var list))
                    list.RemoveAll(e => ReferenceEquals(e.Tag, handler));
            }
        }
    }

    /// <summary>
    /// 注册回调处理器：返回值序列化后作为 HTTP 响应体（对齐 Go CallbackHandler，
    /// 供卡片回调等需要同步回传数据的场景使用）。
    /// </summary>
    public EventDispatcher OnCallback<TEvent, TResult>(string eventType, Func<TEvent, CancellationToken, Task<TResult?>> handler)
        where TEvent : class where TResult : class
    {
        lock (_gate)
        {
            _callbacks[eventType] = new CallbackEntry(
                typeof(TEvent),
                async (raw, ct) => await handler((TEvent)raw, ct));
        }
        return this;
    }

    /// <summary>把 app_ticket 事件写进 FeishuClient 的 token 缓存（ISV 必需）。</summary>
    public EventDispatcher Bind(FeishuClient client)
    {
        AppTicketSink = (ticket, ct) => client.Pipeline.AppTickets.SetAsync(ticket, ct);
        On<AppTicketEvent>("app_ticket", async (e, ct) =>
        {
            if (e.AppTicket is { Length: > 0 } && AppTicketSink != null)
                await AppTicketSink(e.AppTicket, ct);
        });
        return this;
    }

    /// <summary>
    /// HTTP webhook 完整处理：解密 → 验签 → challenge → 分发。
    /// 处理器抛异常时返回 500（飞书将按策略重推）。
    /// </summary>
    public async Task<EventResponse> HandleAsync(EventRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var plainJson = await DecryptIfNeededAsync(request);
            if (plainJson == null)
                return EventResponse.Json(500, @"{""msg"":""event message decryption failed""}");

            var fuzzy = _serializer.Deserialize<FuzzyEvent>(plainJson);
            if (fuzzy.Encrypt is { Length: > 0 })
                return EventResponse.Json(500, @"{""msg"":""event data is encrypted, need EncryptKey""}");

            if (fuzzy.Type != ReqTypeChallenge && !SkipSignVerify && !VerifySign(request))
                return EventResponse.Json(500, @"{""msg"":""the result of signature verification failed""}");

            // challenge（URL 验证）
            if (fuzzy.Type == ReqTypeChallenge)
            {
                if (_verificationToken != null && fuzzy.Token != _verificationToken)
                    return EventResponse.Json(500, @"{""msg"":""the result of auth by challenge failed""}");
                return EventResponse.Json(200, $@"{{""challenge"":""{fuzzy.Challenge}""}}");
            }

            var eventType = ResolveEventType(fuzzy);
            if (eventType == null)
                return EventResponse.Json(500, @"{""msg"":""event type missing""}");

            // 回调处理器优先：返回值即响应体（对齐 Go DoHandle 的 callbackHandler 分支）
            CallbackEntry? callback;
            lock (_gate) _callbacks.TryGetValue(eventType, out callback);
            if (callback != null)
            {
                var arg = DeserializeEvent(callback.EventType, plainJson);
                var result = await callback.Invoke(arg, cancellationToken);
                var body = result == null
                    ? @"{""msg"":""success""}"
                    : _serializer.Serialize(result);
                return EventResponse.Json(200, body);
            }

            await DispatchCoreAsync(eventType, plainJson, cancellationToken);
            return EventResponse.Success;
        }
        catch (Exception ex)
        {
            _logger.Error($"handle event, path: {request.Path}, err: {ex.Message}", ex);
            return EventResponse.Json(500, $@"{{""msg"":""{EscapeJsonString(ex.Message)}""}}");
        }
    }

    /// <summary>
    /// WebSocket 通道的裸分发（已解密载荷），返回是否成功（用于组上行 Response 帧）。
    /// </summary>
    public async Task<bool> DispatchAsync(byte[] plainPayload, CancellationToken cancellationToken = default)
    {
        var fuzzy = _serializer.Deserialize<FuzzyEvent>(plainPayload);
        var eventType = ResolveEventType(fuzzy);
        if (eventType == null)
        {
            _logger.Warn("ws event: event type missing");
            return false;
        }
        await DispatchCoreAsync(eventType, plainPayload, cancellationToken);
        return true;
    }

    private async Task DispatchCoreAsync(string eventType, byte[] plainJson, CancellationToken cancellationToken)
    {
        List<HandlerEntry> entries;
        lock (_gate)
        {
            if (!_handlers.TryGetValue(eventType, out entries!))
            {
                _logger.Error($"event type: {eventType}, not found handler");
                return;
            }
            entries = [.. entries];
        }

        foreach (var entry in entries)
        {
            object arg = entry.EventType == typeof(byte[]) ? plainJson : DeserializeEvent(entry.EventType, plainJson);
            await entry.Invoke(arg, cancellationToken);
        }
    }

    private object DeserializeEvent(Type eventType, byte[] plainJson)
    {
        // 信封裁剪：只把 event 字段反序列化为 TEvent
        var envelope = JsonSerializer.Deserialize<FuzzyEvent>(plainJson, SystemTextJsonFeishuSerializer.Instance.Options);
        if (envelope?.Event is { ValueKind: JsonValueKind.Object } eventJson)
        {
            return JsonSerializer.Deserialize(eventJson.GetRawText(), eventType, SystemTextJsonFeishuSerializer.Instance.Options)!;
        }
        // v1 裸事件：整体反序列化
        return JsonSerializer.Deserialize(plainJson, eventType, SystemTextJsonFeishuSerializer.Instance.Options)!;
    }

    private async Task<byte[]?> DecryptIfNeededAsync(EventRequest request)
    {
        if (_encryptKey is not { Length: > 0 })
            return request.Body;
        try
        {
            var encrypted = _serializer.Deserialize<FuzzyEvent>(request.Body);
            if (encrypted?.Encrypt is not { Length: > 0 })
            {
                _logger.Warn("encrypted message is blank");
                return null;
            }
            return EventCrypto.Decrypt(encrypted.Encrypt, _encryptKey);
        }
        catch (Exception ex)
        {
            _logger.Error($"event message decryption failed: {ex.Message}", ex);
            return null;
        }
    }

    private bool VerifySign(EventRequest request)
    {
        if (_encryptKey is not { Length: > 0 })
            return true; // 与 Go 版一致：未配置 EncryptKey 时不验签

        var timestamp = request.TryHeader("X-Lark-Request-Timestamp") ?? "";
        var nonce = request.TryHeader("X-Lark-Request-Nonce") ?? "";
        var sourceSign = request.TryHeader("X-Lark-Signature") ?? "";
        var targetSign = EventCrypto.Signature(timestamp, nonce, _encryptKey, Encoding.UTF8.GetString(request.Body));
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(targetSign),
            Encoding.UTF8.GetBytes(sourceSign));
    }

    private static string? ResolveEventType(FuzzyEvent fuzzy)
    {
        if (fuzzy.Header?.EventType is { Length: > 0 } t) return t;
        if (fuzzy.Event is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty("type", out var type))
            return type.GetString();
        return null;
    }

    internal static string EscapeJsonString(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
