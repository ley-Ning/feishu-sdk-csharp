using System.Text.Json.Serialization;

namespace Feishu;

/// <summary>
/// 飞书业务错误（响应体里的 code/msg）。token 获取失败等基础设施路径以异常抛出；
/// 普通 API 调用不抛出，结果对象上通过 <see cref="FeishuResponse.Success"/> 判断。
/// </summary>
public sealed class FeishuCodeException : Exception
{
    public int Code { get; }

    public CodeError Error { get; }

    public FeishuCodeException(CodeError error)
        : base($"code: {error.Code}, msg: {error.Msg}" + (error.ErrorDetails != null ? $", error: {error.ErrorDetails}" : ""))
    {
        Code = error.Code;
        Error = error;
    }

    public FeishuCodeException(int code, string msg) : this(new CodeError { Code = code, Msg = msg })
    {
    }

    public override string ToString() => Message;
}

/// <summary>服务端 504 网关超时（Go 版 ServerTimeoutError）。</summary>
public sealed class FeishuServerTimeoutException : Exception
{
    public string? RequestId { get; init; }

    public FeishuServerTimeoutException(string? requestId = null)
        : base("server time out" + (requestId is { Length: > 0 } ? $", requestId: {requestId}" : ""))
        => RequestId = requestId;
}

/// <summary>客户端请求超时（Go 版 ClientTimeoutError，不重试）。</summary>
public sealed class FeishuClientTimeoutException : Exception
{
    public FeishuClientTimeoutException() : base("client request time out")
    {
    }
}

/// <summary>连接建立失败（Go 版 DialFailedError，会重试一轮）。</summary>
public sealed class FeishuDialFailedException : Exception
{
    public FeishuDialFailedException(string message, Exception? inner = null)
        : base($"dial failed: {message}", inner)
    {
    }
}

/// <summary>常见业务错误码（与 Go 版 core/constants.go 对齐）。</summary>
public static class FeishuErrorCodes
{
    public const int AppTicketInvalid = 10012;
    public const int AccessTokenInvalid = 99991671;
    public const int AppAccessTokenInvalid = 99991664;
    public const int TenantAccessTokenInvalid = 99991663;

    public const int ClientAssertionProviderNotConfigured = 7100;
    public const int ClientAssertionTokenEmpty = 7101;
    public const int ClientAssertionRetrieveFailed = 7102;
    public const int ClientAssertionModeNotSupported = 7103;
    public const int AppSecretAndClientAssertionEmpty = 7104;

    public static bool IsTokenInvalid(int code) =>
        code is AccessTokenInvalid or AppAccessTokenInvalid or TenantAccessTokenInvalid;
}

/// <summary>响应体中的错误结构，用于预解码和结果对象反序列化。</summary>
public sealed class CodeError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("msg")]
    public string? Msg { get; set; }

    [JsonPropertyName("error")]
    public CodeErrorBody? ErrorDetails { get; set; }

    public bool IsSuccess => Code == 0;

    public override string ToString() => $"code: {Code}, msg: {Msg}";
}

public sealed class CodeErrorBody
{
    [JsonPropertyName("log_id")]
    public string? LogId { get; set; }

    [JsonPropertyName("troubleshooter")]
    public string? Troubleshooter { get; set; }

    [JsonPropertyName("details")]
    public List<CodeErrorDetail>? Details { get; set; }

    [JsonPropertyName("permission_violations")]
    public List<CodeErrorPermissionViolation>? PermissionViolations { get; set; }

    [JsonPropertyName("field_violations")]
    public List<CodeErrorFieldViolation>? FieldViolations { get; set; }
}

public sealed class CodeErrorDetail
{
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }
}

public sealed class CodeErrorPermissionViolation
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("subject")]
    public string? Subject { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

public sealed class CodeErrorFieldViolation
{
    [JsonPropertyName("field")]
    public string? Field { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}
