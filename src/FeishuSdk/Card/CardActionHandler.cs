using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Feishu.Events;

namespace Feishu.Card;

/// <summary>
/// 卡片回调行为数据（对齐 Go 版 card.CardAction.Action）。
/// </summary>
public sealed class CardActionData
{
    [JsonPropertyName("value")]
    public Dictionary<string, object?>? Value { get; set; }

    [JsonPropertyName("tag")]
    public string? Tag { get; set; }

    [JsonPropertyName("option")]
    public string? Option { get; set; }

    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("form_value")]
    public Dictionary<string, object?>? FormValue { get; set; }

    [JsonPropertyName("input_value")]
    public string? InputValue { get; set; }

    [JsonPropertyName("options")]
    public List<string>? Options { get; set; }

    [JsonPropertyName("checked")]
    public bool? Checked { get; set; }
}

/// <summary>卡片回调请求（对齐 Go 版 card.CardAction）。</summary>
public sealed class CardAction
{
    [JsonPropertyName("open_id")]
    public string? OpenId { get; set; }

    [JsonPropertyName("user_id")]
    public string? UserId { get; set; }

    [JsonPropertyName("open_message_id")]
    public string? OpenMessageId { get; set; }

    [JsonPropertyName("open_chat_id")]
    public string? OpenChatId { get; set; }

    [JsonPropertyName("tenant_key")]
    public string? TenantKey { get; set; }

    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }

    /// <summary>URL 验证请求时回显的挑战串。</summary>
    [JsonPropertyName("challenge")]
    public string? Challenge { get; set; }

    /// <summary>url_verification 或空（普通卡片回调）。</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("action")]
    public CardActionData? Action { get; set; }

    /// <summary>原始请求（header/body），供需要验签上下文的处理器使用。</summary>
    [JsonIgnore]
    public EventRequest? RawRequest { get; internal set; }
}

/// <summary>
/// 处理器返回的自定义响应：可指定状态码 + body（对齐 Go 版 CustomResp）。
/// </summary>
public sealed class CardCustomResponse
{
    public int StatusCode { get; init; } = 200;

    /// <summary>任意可序列化对象（如 CardToast）。</summary>
    public required object Body { get; init; }
}

/// <summary>Toast 提示响应体（对齐 Go 版 CustomToastBody）。</summary>
public sealed class CardToast
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("i18n")]
    public Dictionary<string, string>? I18n { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

/// <summary>卡片签名：hex(sha1(timestamp + nonce + verificationToken + body))（注意与事件的 SHA256 不同）。</summary>
public static class CardSignature
{
    public static string Signature(string timestamp, string nonce, string verificationToken, string body)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(timestamp + nonce + verificationToken + body));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>
/// 卡片回调处理器（对齐 Go 版 card.CardActionHandler）：
/// 解密（可选）→ challenge → SHA1 验签（用 verificationToken）→ 执行处理器 → 序列化返回值。
/// </summary>
public sealed class CardActionHandler : IWebhookHandler
{
    private readonly string? _verificationToken;
    private readonly string? _encryptKey;
    private readonly Func<CardAction, CancellationToken, Task<object?>> _handler;
    private readonly IFeishuSerializer _serializer;
    private readonly IFeishuLogger _logger;

    /// <summary>跳过验签（本地调试）。对齐 Go Config.SkipSignVerify。</summary>
    public bool SkipSignVerify { get; set; }

    public CardActionHandler(
        string? verificationToken,
        string? encryptKey,
        Func<CardAction, CancellationToken, Task<object?>> handler,
        IFeishuSerializer? serializer = null,
        IFeishuLogger? logger = null)
    {
        _verificationToken = verificationToken;
        _encryptKey = encryptKey;
        _handler = handler;
        _serializer = serializer ?? SystemTextJsonFeishuSerializer.Instance;
        _logger = logger ?? NullFeishuLogger.Instance;
    }

    public async Task<EventResponse> HandleAsync(EventRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var plain = DecryptIfNeeded(request.Body);

            var action = _serializer.Deserialize<CardAction>(plain);
            action.RawRequest = request;

            if (action.Type == EventDispatcher.ReqTypeChallenge)
            {
                if (_verificationToken != null && action.Token != _verificationToken)
                    return EventResponse.Json(500, @"{""msg"":""the result of auth by challenge failed""}");
                return EventResponse.Json(200, $@"{{""challenge"":""{action.Challenge}""}}");
            }

            if (!SkipSignVerify && !VerifySign(request))
                return EventResponse.Json(500, @"{""msg"":""the result of signature verification failed""}");

            var result = await _handler(action, cancellationToken);

            return result switch
            {
                null => EventResponse.Success,
                string raw => new EventResponse
                {
                    StatusCode = 200,
                    ContentType = "application/json; charset=utf-8",
                    Body = Encoding.UTF8.GetBytes(raw),
                },
                CardCustomResponse custom => new EventResponse
                {
                    StatusCode = custom.StatusCode == 0 ? 200 : custom.StatusCode,
                    ContentType = "application/json; charset=utf-8",
                    Body = _serializer.SerializeToUtf8Bytes(custom.Body),
                },
                _ => new EventResponse
                {
                    StatusCode = 200,
                    ContentType = "application/json; charset=utf-8",
                    Body = _serializer.SerializeToUtf8Bytes(result),
                },
            };
        }
        catch (Exception ex)
        {
            _logger.Error($"handle cardAction, path: {request.Path}, err: {ex.Message}", ex);
            return EventResponse.Json(500, $@"{{""msg"":""{EventDispatcher.EscapeJsonString(ex.Message)}""}}");
        }
    }

    private byte[] DecryptIfNeeded(byte[] body)
    {
        var fuzzy = _serializer.Deserialize<CardFuzzy>(body);
        if (fuzzy.Encrypt is not { Length: > 0 })
            return body;
        if (_encryptKey is not { Length: > 0 })
            throw new FeishuException("encrypt_key not found");
        return EventCrypto.Decrypt(fuzzy.Encrypt, _encryptKey);
    }

    /// <summary>SHA1(timestamp + nonce + verificationToken + body)；未配置 token 时不验签（对齐 Go）。</summary>
    private bool VerifySign(EventRequest request)
    {
        if (_verificationToken is not { Length: > 0 })
            return true;

        var timestamp = request.TryHeader("X-Lark-Request-Timestamp") ?? "";
        var nonce = request.TryHeader("X-Lark-Request-Nonce") ?? "";
        var source = request.TryHeader("X-Lark-Signature") ?? "";
        var target = CardSignature.Signature(timestamp, nonce, _verificationToken, Encoding.UTF8.GetString(request.Body));
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(target),
            Encoding.UTF8.GetBytes(source));
    }

    private sealed class CardFuzzy
    {
        [JsonPropertyName("encrypt")]
        public string? Encrypt { get; set; }
    }
}
