using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Feishu;

/// <summary>
/// JWT Client Assertion 提供方：由调用方产出 JWT，SDK 用它向 OAuth 端点换取 tenant_access_token。
/// 仅适用于自建应用；商店应用不支持。
/// </summary>
public interface IClientAssertionProvider
{
    Task<ClientAssertionToken> RetrieveTokenAsync(string audience, CancellationToken cancellationToken = default);
}

public sealed record ClientAssertionToken
{
    public required string Value { get; init; }

    /// <summary>可选：把 OAuth 请求转发到自建代理服务时使用。</summary>
    public string? TargetService { get; init; }

    public string? TargetPrefix { get; init; }
}

internal sealed class RequestPipeline
{
    internal const string AppAccessTokenInternalPath = "/open-apis/auth/v3/app_access_token/internal";
    internal const string AppAccessTokenPath = "/open-apis/auth/v3/app_access_token";
    internal const string TenantAccessTokenInternalPath = "/open-apis/auth/v3/tenant_access_token/internal";
    internal const string TenantAccessTokenPath = "/open-apis/auth/v3/tenant_access_token";
    internal const string OAuthTokenPath = "/oauth/v3/token";
    internal const string AppTicketResendPath = "/open-apis/auth/v3/app_ticket/resend";

    private const string UserAgentFormat = "feishu-sdk-csharp/0.1.0{0}";
    internal const string GrantTypeJwtBearer = "urn:ietf:params:oauth:grant-type:jwt-bearer";
    internal const string ClientAssertionTypeJwtBearer = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
    internal const string HeaderXTargetService = "X-Target-Service";

    private readonly FeishuOptions _options;
    private readonly HttpClient _httpClient;

    public TokenManager Tokens { get; }

    public AppTicketManager AppTickets { get; }

    internal FeishuOptions Options => _options;

    /// <summary>发送并把响应反序列化为强类型结果（业务 code/msg 挂在基类上，不抛业务异常）。</summary>
    public async Task<T> SendForAsync<T>(ApiRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) where T : FeishuResponse
    {
        var apiResponse = await SendAsync(request, options, cancellationToken);
        var typed = _options.Serializer.Deserialize<T>(apiResponse.RawBody);
        typed.Raw = apiResponse;
        return typed;
    }

    public RequestPipeline(FeishuOptions options, HttpClient httpClient)
    {
        _options = options;
        _httpClient = httpClient;
        Tokens = new TokenManager(options, this);
        AppTickets = new AppTicketManager(options, this);
    }

    internal static string UserAgent(string? source) =>
        string.Format(UserAgentFormat, source is { Length: > 0 } ? $" ({source})" : "");

    /// <summary>
    /// 请求总入口：token 类型裁决 → 校验 → 最多两轮（发送 → 预解码 → token/app_ticket 失效则驱逐缓存并重试）。
    /// 对齐 Go 版 doRequest：仅拨号失败会重试一轮；超时与其他传输错误立即抛出。
    /// </summary>
    public async Task<ApiResponse> SendAsync(ApiRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= RequestOptions.Default;
        ValidateTokenType(request, options);
        var tokenType = DetermineTokenType(request, options);
        Validate(request, options, tokenType);

        ApiResponse? response = null;
        Exception? transportError = null;
        var maxAttempts = 2;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            using var httpRequest = await BuildHttpRequestAsync(request, options, tokenType, cancellationToken);
            LogRequest(httpRequest);

            try
            {
                using var httpResponse = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (httpResponse.StatusCode == HttpStatusCode.GatewayTimeout)
                {
                    var requestId = Header(httpResponse, "X-Tt-Logid") ?? Header(httpResponse, "X-Request-Id");
                    _options.Logger.Info($"req path: {httpRequest.RequestUri?.PathAndQuery}, server time out, requestId: {requestId}");
                    throw new FeishuServerTimeoutException(requestId);
                }

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (k, v) in httpResponse.Headers)
                    headers[k] = string.Join(",", v);
                foreach (var (k, v) in httpResponse.Content.Headers)
                    headers[k] = string.Join(",", v);

                var body = httpResponse.Content is null
                    ? Array.Empty<byte>()
                    : await httpResponse.Content.ReadAsByteArrayAsync(cancellationToken);
                response = new ApiResponse
                {
                    StatusCode = (int)httpResponse.StatusCode,
                    Headers = headers,
                    RawBody = body,
                };
            }
            catch (FeishuServerTimeoutException)
            {
                throw;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // HttpClient 超时：立即失败，不重试（对齐 Go ClientTimeoutError）
                throw new FeishuClientTimeoutException();
            }
            catch (HttpRequestException ex) when (ex.InnerException is SocketException)
            {
                // 拨号失败：重试一轮（对齐 Go DialFailedError）
                transportError = new FeishuDialFailedException(ex.Message, ex);
                continue;
            }
            catch (HttpRequestException)
            {
                // 其余传输错误：Go 版直接返回，不重试
                throw;
            }

            LogResponse(response);

            // 文件下载成功 / 非 JSON 响应：不做业务预解码，直接透传
            if (options.FileDownload && response.StatusCode == 200 || !response.IsJson)
                return response;

            if (IsOAuthTokenPath(request.Path))
                return response;

            // 预解码：只看 code，判断 token / app_ticket 是否失效
            var codeError = TryDecodeCodeError(response);
            var code = codeError?.Code ?? 0;

            if (code == FeishuErrorCodes.AppTicketInvalid)
                await AppTickets.ResendAppTicketAsync(cancellationToken);

            if (tokenType == AccessTokenType.None || !_options.EnableTokenCache)
                return response;

            if (FeishuErrorCodes.IsTokenInvalid(code))
            {
                // 关键差异（优于 Go 版）：失效后先驱逐缓存再重试，
                // 否则重试仍会带上同一枚过期 token。
                Tokens.Invalidate(tokenType, options);
                continue;
            }

            return response;
        }

        if (transportError != null)
            throw transportError;
        if (response != null)
            return response;
        throw new InvalidOperationException("unreachable: pipeline exited without response");
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private CodeError? TryDecodeCodeError(ApiResponse response)
    {
        try
        {
            return _options.Serializer.Deserialize<CodeError>(response.RawBody);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsOAuthTokenPath(string path)
    {
        var pathOnly = path.Contains("://", StringComparison.Ordinal)
            ? new Uri(path).AbsolutePath
            : path;
        return pathOnly.EndsWith(OAuthTokenPath, StringComparison.Ordinal);
    }

    // ---- token 类型裁决（对齐 Go 版 determineTokenType/validateTokenType） ----

    internal AccessTokenType DetermineTokenType(ApiRequest request, RequestOptions options)
    {
        var types = request.SupportedTokenTypes;
        if (types.Count == 0) return AccessTokenType.None;

        // ClientAssertion 模式：只有 tenant（JWT 换来的）；User 可显式指定
        if (_options.ClientAssertionProvider != null)
        {
            var accessible = new HashSet<AccessTokenType>(types);
            if (options.UserAccessToken is { Length: > 0 } && accessible.Contains(AccessTokenType.User))
                return AccessTokenType.User;
            if (accessible.Contains(AccessTokenType.Tenant))
                return AccessTokenType.Tenant;
            if (accessible.Contains(AccessTokenType.App))
                throw new FeishuCodeException(FeishuErrorCodes.ClientAssertionModeNotSupported,
                    "AppAccessToken APIs are not available in ClientAssertion mode");
            return types[0];
        }

        if (options.UserAccessToken is { Length: > 0 } && types.Contains(AccessTokenType.User))
            return AccessTokenType.User;
        if (options.TenantAccessToken is { Length: > 0 } && types.Contains(AccessTokenType.Tenant))
            return AccessTokenType.Tenant;

        // 默认优先 tenant，其次 app
        if (types.Contains(AccessTokenType.Tenant)) return AccessTokenType.Tenant;
        if (types.Contains(AccessTokenType.App)) return AccessTokenType.App;
        return types[0];
    }

    /// <summary>
    /// 对齐 Go 版 validateTokenType：API 只声明一种 token 类型时，
    /// 手动传入的 token 类型不匹配要在发请求前拒绝。
    /// </summary>
    internal static void ValidateTokenType(ApiRequest request, RequestOptions options)
    {
        var types = request.SupportedTokenTypes;
        if (types.Count != 1) return;

        if (types[0] == AccessTokenType.Tenant && options.UserAccessToken is { Length: > 0 })
            throw new FeishuException("tenant token type not match user access token");
        if (types[0] == AccessTokenType.User && options.TenantAccessToken is { Length: > 0 })
            throw new FeishuException("user token type not match tenant access token");
    }

    private void Validate(ApiRequest request, RequestOptions options, AccessTokenType tokenType)
    {
        if (_options.AppId.Length == 0)
            throw new FeishuException("AppId is empty");

        var hasManualToken =
            (tokenType == AccessTokenType.User && options.UserAccessToken is { Length: > 0 }) ||
            (tokenType == AccessTokenType.Tenant && options.TenantAccessToken is { Length: > 0 }) ||
            (tokenType == AccessTokenType.App && options.AppAccessToken is { Length: > 0 });

        if (_options.ClientAssertionProvider != null && _options.AppType == FeishuAppType.Marketplace)
            throw new FeishuCodeException(FeishuErrorCodes.ClientAssertionProviderNotConfigured,
                "ClientAssertion mode is not supported for marketplace apps");

        if (_options.ClientAssertionProvider == null && _options.AppSecret.Length == 0 && !hasManualToken)
            throw new FeishuException("AppSecret is empty");

        // 禁用 token 缓存托管时，必须手动提供 token（对齐 Go validate）
        if (!_options.EnableTokenCache && tokenType != AccessTokenType.None &&
            options.UserAccessToken is not { Length: > 0 } &&
            options.TenantAccessToken is not { Length: > 0 } &&
            options.AppAccessToken is not { Length: > 0 })
        {
            throw new FeishuException("accessToken is empty (EnableTokenCache=false requires manual token)");
        }

        if (_options.AppType == FeishuAppType.Marketplace && tokenType == AccessTokenType.Tenant && options.TenantKey is not { Length: > 0 })
            throw new FeishuException("tenant key is empty (marketplace app requires RequestOptions.TenantKey)");

        if (tokenType == AccessTokenType.User && options.UserAccessToken is not { Length: > 0 })
            throw new FeishuException("user access token is empty");

        if (options.Headers != null)
        {
            if (options.Headers.ContainsKey("X-Request-Id") || options.Headers.ContainsKey("Request-Id"))
                throw new FeishuException("X-Request-Id / Request-Id are reserved header keys");
        }
    }

    // ---- HTTP 请求构建 ----

    private async Task<HttpRequestMessage> BuildHttpRequestAsync(ApiRequest request, RequestOptions options, AccessTokenType tokenType, CancellationToken cancellationToken)
    {
        var url = BuildUrl(request);
        var httpRequest = new HttpRequestMessage(request.Method, url);

        if (options.RequestId is { Length: > 0 })
            httpRequest.Headers.TryAddWithoutValidation("Oapi-Sdk-Request-Id", options.RequestId);
        if (options.Headers != null)
            foreach (var (k, v) in options.Headers)
                httpRequest.Headers.TryAddWithoutValidation(k, v);
        foreach (var (k, v) in _options.DefaultHeaders)
            httpRequest.Headers.TryAddWithoutValidation(k, v);
        httpRequest.Headers.TryAddWithoutValidation("User-Agent", UserAgent(_options.Source));

        switch (request.Body)
        {
            case MultipartRequestBody multipart:
            {
                var content = new MultipartFormDataContent();
                foreach (var (name, fileName, stream, value) in multipart.Parts)
                {
                    // 显式加引号，与 Go 版 multipart 输出一致（name="file"）
                    var quotedName = $"\"{name}\"";
                    if (value != null)
                        content.Add(new StringContent(value), quotedName);
                    else if (stream != null)
                        content.Add(new StreamContent(stream), quotedName, fileName ?? "unknown-file");
                }
                httpRequest.Content = content;
                break;
            }
            case byte[] raw:
                httpRequest.Content = new ByteArrayContent(raw);
                httpRequest.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json; charset=utf-8");
                break;
            case { } body:
            {
                var json = _options.Serializer.SerializeToUtf8Bytes(body);
                httpRequest.Content = new ByteArrayContent(json);
                httpRequest.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json; charset=utf-8");
                break;
            }
        }

        switch (tokenType)
        {
            case AccessTokenType.App:
            {
                var token = options.AppAccessToken;
                if (_options.EnableTokenCache && token is not { Length: > 0 })
                    token = await Tokens.GetAppAccessTokenAsync(options.AppTicket, cancellationToken);
                httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                break;
            }
            case AccessTokenType.Tenant:
            {
                var token = options.TenantAccessToken;
                if (token is not { Length: > 0 } && _options.EnableTokenCache)
                    token = await Tokens.GetTenantAccessTokenAsync(options.TenantKey, options.AppTicket, cancellationToken);
                httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                break;
            }
            case AccessTokenType.User:
                httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.UserAccessToken);
                break;
        }

        if (options.NeedHelpdeskAuth)
        {
            var token = _options.HelpdeskId is { Length: > 0 } && _options.HelpdeskToken is { Length: > 0 }
                ? Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{_options.HelpdeskId}:{_options.HelpdeskToken}"))
                : null;
            if (token == null)
                throw new FeishuException("help desk API: set FeishuOptions.HelpdeskId/HelpdeskToken first");
            httpRequest.Headers.TryAddWithoutValidation("X-Lark-Helpdesk-Authorization", token);
        }

        return httpRequest;
    }

    internal string BuildUrl(ApiRequest request)
    {
        var segments = request.Path.Split('/');
        var rebuilt = new StringBuilder();
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.StartsWith(':'))
            {
                var name = segment[1..];
                if (!request.PathParams.TryGetValue(name, out var value) || value.Length == 0)
                    throw new FeishuException($"path param `{name}` missing or empty for {request.Path}");
                rebuilt.Append(Uri.EscapeDataString(value));
            }
            else
            {
                rebuilt.Append(segment);
            }
            if (i < segments.Length - 1)
                rebuilt.Append('/');
        }
        var path = rebuilt.ToString();
        var full = path.StartsWith("http", StringComparison.Ordinal)
            ? path
            : _options.BaseUrl.TrimEnd('/') + path;

        if (request.QueryParams.Count > 0)
        {
            var query = string.Join("&", request.QueryParams
                .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
            full += "?" + query;
        }
        return full;
    }

    // ---- 日志 ----

    private void LogRequest(HttpRequestMessage request)
    {
        if (_options.Logger.IsEnabled(FeishuLogLevel.Debug))
        {
            if (_options.LogRequestAtDebug)
            {
                var safe = new Dictionary<string, string>();
                foreach (var (k, v) in request.Headers)
                    safe[k] = k.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || k.Equals("X-Lark-Helpdesk-Authorization", StringComparison.OrdinalIgnoreCase)
                        ? "<redacted>"
                        : string.Join(",", v);
                _options.Logger.Debug($"req: {request.Method} {request.RequestUri}, headers: {_options.Serializer.Prettify(safe)}");
            }
            else
            {
                _options.Logger.Debug($"req: {request.Method} {request.RequestUri?.GetLeftPart(UriPartial.Path)}");
            }
        }
    }

    private void LogResponse(ApiResponse response)
    {
        if (!_options.Logger.IsEnabled(FeishuLogLevel.Debug)) return;
        if (_options.LogRequestAtDebug && response.IsJson)
            _options.Logger.Debug($"resp: statusCode: {response.StatusCode}, requestId: {response.RequestId}, body: {System.Text.Encoding.UTF8.GetString(response.RawBody)}");
        else
            _options.Logger.Debug($"resp: statusCode: {response.StatusCode}, requestId: {response.RequestId}");
    }
}

/// <summary>SDK 基础设施异常（参数缺失、传输失败等）。</summary>
public sealed class FeishuException : Exception
{
    public FeishuException(string message) : base(message)
    {
    }

    public FeishuException(string message, Exception inner) : base(message, inner)
    {
    }
}
