using System.Text.Json.Serialization;

namespace Feishu;

/// <summary>token 类型（决定 Authorization 头怎么来）。</summary>
public enum AccessTokenType
{
    /// <summary>不需要 token（如获取 token 的接口本身）。</summary>
    None,

    /// <summary>app_access_token，应用维度。</summary>
    App,

    /// <summary>tenant_access_token，租户维度（绝大多数服务端 API）。</summary>
    Tenant,

    /// <summary>user_access_token，用户维度，必须调用方显式提供。</summary>
    User,
}

/// <summary>
/// 一次 API 调用的协议级描述。路径中的 <c>:param</c> 占位符由 <see cref="PathParams"/> 填充。
/// </summary>
public sealed class ApiRequest
{
    public required HttpMethod Method { get; init; }

    /// <summary>API 路径，如 <c>/open-apis/im/v1/messages/:message_id</c>；以 http 开头则视为完整 URL。</summary>
    public required string Path { get; init; }

    public Dictionary<string, string> PathParams { get; init; } = new();

    /// <summary>查询参数，支持同名多值。</summary>
    public QueryParams QueryParams { get; init; } = new();

    /// <summary>请求体（POCO / MultipartRequestBody / byte[] / null）。</summary>
    public object? Body { get; init; }

    /// <summary>该 API 声明支持的 token 类型，按顺序降级选择。</summary>
    public IReadOnlyList<AccessTokenType> SupportedTokenTypes { get; init; } = [AccessTokenType.None];

    public ApiRequest Clone()
    {
        var query = new QueryParams();
        foreach (var kv in QueryParams) query.Add(kv.Key, kv.Value);
        return new ApiRequest
        {
            Method = Method,
            Path = Path,
            PathParams = new Dictionary<string, string>(PathParams),
            QueryParams = query,
            Body = Body,
            SupportedTokenTypes = SupportedTokenTypes,
        };
    }
}

/// <summary>查询参数集合，支持同名多值（Add(key, value)）。</summary>
public sealed class QueryParams : IEnumerable<KeyValuePair<string, string>>
{
    private readonly List<KeyValuePair<string, string>> _items = new();

    public void Add(string key, string value) => _items.Add(new(key, value));

    public int Count => _items.Count;

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _items.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>multipart 表单体（文件上传接口使用）。</summary>
public sealed class MultipartRequestBody
{
    private readonly List<(string Name, string? FileName, Stream? Stream, string? Value)> _parts = new();

    public MultipartRequestBody AddField(string name, string value)
    {
        _parts.Add((name, null, null, value));
        return this;
    }

    public MultipartRequestBody AddFile(string name, string fileName, Stream stream)
    {
        _parts.Add((name, fileName, stream, null));
        return this;
    }

    public MultipartRequestBody AddFileFromPath(string name, string path) =>
        AddFile(name, Path.GetFileName(path), File.OpenRead(path));

    internal IReadOnlyList<(string Name, string? FileName, Stream? Stream, string? Value)> Parts => _parts;
}

/// <summary>单次请求的可选项（对应 Go 版 RequestOption 的 WithXxx 系列），record 支持不可变派生。</summary>
public sealed record RequestOptions
{
    public string? TenantKey { get; init; }
    public string? UserAccessToken { get; init; }
    public string? AppAccessToken { get; init; }
    public string? TenantAccessToken { get; init; }
    public string? AppTicket { get; init; }
    public string? RequestId { get; init; }
    public bool NeedHelpdeskAuth { get; init; }
    public bool FileDownload { get; init; }

    /// <summary>单次请求附加 header（X-Request-Id / Request-Id 为保留字，不允许设置）。</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    public static RequestOptions Default { get; } = new();

    public RequestOptions WithTenantKey(string tenantKey) => this with { TenantKey = tenantKey };
    public RequestOptions WithUserAccessToken(string token) => this with { UserAccessToken = token };
    public RequestOptions WithAppAccessToken(string token) => this with { AppAccessToken = token };
    public RequestOptions WithTenantAccessToken(string token) => this with { TenantAccessToken = token };
    public RequestOptions WithAppTicket(string appTicket) => this with { AppTicket = appTicket };
    public RequestOptions WithRequestId(string requestId) => this with { RequestId = requestId };
    public RequestOptions WithHelpdeskAuth() => this with { NeedHelpdeskAuth = true };
    public RequestOptions WithFileDownload() => this with { FileDownload = true };
    public RequestOptions WithHeaders(IReadOnlyDictionary<string, string> headers) => this with { Headers = headers };
}

/// <summary>
/// 原始 HTTP 响应：状态码 / header / 未解码 body。挂在每个强类型响应对象上，
/// 便于取 RequestId、原始字节（文件下载）等。
/// </summary>
public sealed class ApiResponse
{
    public required int StatusCode { get; init; }

    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    public required byte[] RawBody { get; init; }

    public string RequestId =>
        Headers.TryGetValue("X-Tt-Logid", out var logId) && logId.Length > 0 ? logId
            : Headers.TryGetValue("X-Request-Id", out var reqId) ? reqId
            : "";

    public bool IsJson => Headers.TryGetValue("Content-Type", out var ct) && ct.Contains("application/json");

    public T Deserialize<T>(IFeishuSerializer serializer) => serializer.Deserialize<T>(RawBody);
}

/// <summary>
/// 所有强类型响应的基类：业务 code/msg + 传输层信息。
/// 与 Go 版一致：调用不抛业务异常，用 <see cref="Success"/> 判断。
/// </summary>
public abstract class FeishuResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("msg")]
    public string? Msg { get; set; }

    [JsonIgnore]
    public ApiResponse? Raw { get; internal set; }

    [JsonIgnore]
    public int StatusCode => Raw?.StatusCode ?? 0;

    [JsonIgnore]
    public string RequestId => Raw?.RequestId ?? "";

    [JsonIgnore]
    public byte[]? RawBody => Raw?.RawBody;

    [JsonIgnore]
    public bool Success => Code == 0;

    /// <summary>业务失败时抛出 <see cref="FeishuCodeException"/>（含 code/msg/log_id）。</summary>
    public FeishuResponse EnsureSuccess()
    {
        if (!Success) throw new FeishuCodeException(new CodeError { Code = Code, Msg = Msg });
        return this;
    }
}
