using System.Text.Json.Serialization;

namespace Feishu.Ws;

// ---- bootstrap / 配置模型 ----

/// <summary>bootstrap 请求体（POST /callback/ws/endpoint；字段为 PascalCase，对齐飞书网关）。</summary>
internal sealed class WsBootstrapRequest
{
    /// <summary>应用 id。</summary>
    [JsonPropertyName("AppID")]
    public string? AppId { get; set; }

    /// <summary>应用密钥（ClientAssertion 模式下传空）。</summary>
    [JsonPropertyName("AppSecret")]
    public string? AppSecret { get; set; }

    /// <summary>ClientAssertion JWT（代持凭证模式）。</summary>
    [JsonPropertyName("ClientAssertion")]
    public string? ClientAssertion { get; set; }
}

/// <summary>bootstrap 响应壳。</summary>
internal sealed class WsEndpointResp
{
    /// <summary>业务码：0 成功；1 服务繁忙；403/514 客户端致命；1000040350 连接数超限等。</summary>
    [JsonPropertyName("code")]
    public int Code { get; set; }

    /// <summary>错误信息。</summary>
    [JsonPropertyName("msg")]
    public string? Msg { get; set; }

    /// <summary>连接信息。</summary>
    [JsonPropertyName("data")]
    public WsEndpoint? Data { get; set; }
}

/// <summary>bootstrap 响应 data 载荷。</summary>
internal sealed class WsEndpoint
{
    /// <summary>带凭证的 WebSocket 连接 URL（含 device_id/service_id query）。</summary>
    [JsonPropertyName("URL")]
    public string? Url { get; set; }

    /// <summary>服务端下发的客户端运行配置。</summary>
    [JsonPropertyName("ClientConfig")]
    public WsClientConfig? ClientConfig { get; set; }
}

/// <summary>服务端下发的连接配置（pong 载荷 / bootstrap 附带）。</summary>
public sealed class WsClientConfig
{
    /// <summary>重连次数上限（-1 无限）。</summary>
    [JsonPropertyName("ReconnectCount")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int? ReconnectCount { get; set; }

    /// <summary>重连间隔（秒）。</summary>
    [JsonPropertyName("ReconnectInterval")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int? ReconnectInterval { get; set; }

    /// <summary>首次重连随机抖动上限（秒）。</summary>
    [JsonPropertyName("ReconnectNonce")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int? ReconnectNonce { get; set; }

    /// <summary>心跳间隔（秒）。</summary>
    [JsonPropertyName("PingInterval")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int? PingInterval { get; set; }
}

/// <summary>上行回执帧载荷：{"code":200} / {"code":500}。</summary>
internal sealed class WsUpstreamResponse
{
    /// <summary>处理结果码：200 成功，500 处理器报错。</summary>
    [JsonPropertyName("code")]
    public int StatusCode { get; set; }
}
