using System.Text.Json.Serialization;

namespace Feishu.Auth;

/// <summary>
/// OAuth 用户访问令牌服务（对齐 Go 版 core/accesstoken 包）：
/// 授权码换取 / 刷新 user_access_token，走 OAuth 端点（/oauth/v3/token），
/// 凭证用 client_secret 或 ClientAssertion（JWT）。
/// </summary>
public sealed class OAuthTokenService
{
    private readonly RequestPipeline _pipeline;

    internal OAuthTokenService(RequestPipeline pipeline) => _pipeline = pipeline;

    private const string GrantTypeAuthorizationCode = "authorization_code";
    private const string GrantTypeRefreshToken = "refresh_token";

    private FeishuOptions Options => _pipeline.Options;

    /// <summary>用网页授权码换取用户访问令牌。</summary>
    public Task<OAuthTokenResult> ExchangeByAuthorizationCodeAsync(
        string code, string? redirectUri = null, string? codeVerifier = null, string? scope = null,
        CancellationToken cancellationToken = default) =>
        DoTokenRequestAsync(new OAuthTokenRequestBody
        {
            GrantType = GrantTypeAuthorizationCode,
            Code = code,
            RedirectUri = redirectUri,
            CodeVerifier = codeVerifier,
            Scope = scope,
        }, null, cancellationToken);

    /// <summary>用刷新令牌续期用户访问令牌。</summary>
    public Task<OAuthTokenResult> RefreshAsync(
        string refreshToken, string? scope = null,
        CancellationToken cancellationToken = default) =>
        DoTokenRequestAsync(new OAuthTokenRequestBody
        {
            GrantType = GrantTypeRefreshToken,
            RefreshToken = refreshToken,
            Scope = scope,
        }, null, cancellationToken);

    private async Task<OAuthTokenResult> DoTokenRequestAsync(OAuthTokenRequestBody body, RequestOptions? options, CancellationToken ct)
    {
        var oauthBaseUrl = Options.ResolveOAuthBaseUrl();
        var requestUrl = oauthBaseUrl.TrimEnd('/') + RequestPipeline.OAuthTokenPath;
        body.ClientId = Options.AppId;

        if (Options.ClientAssertionProvider != null)
        {
            var aud = ExtractHost(oauthBaseUrl)
                ?? throw new FeishuException($"invalid oauth base url: {oauthBaseUrl}");
            ClientAssertionToken assertion;
            try
            {
                assertion = await Options.ClientAssertionProvider.RetrieveTokenAsync(aud, ct);
            }
            catch (Exception ex)
            {
                throw new FeishuCodeException(FeishuErrorCodes.ClientAssertionRetrieveFailed, ex.Message);
            }
            if (assertion.Value is not { Length: > 0 })
                throw new FeishuCodeException(FeishuErrorCodes.ClientAssertionTokenEmpty, "client assertion token is empty");

            body.ClientAssertionType = RequestPipeline.ClientAssertionTypeJwtBearer;
            body.ClientAssertion = assertion.Value;
            var headers = new Dictionary<string, string>();
            if (assertion.TargetService is { Length: > 0 })
            {
                requestUrl = BuildProxyUrl(assertion.TargetService, assertion.TargetPrefix, RequestPipeline.OAuthTokenPath);
                headers[RequestPipeline.HeaderXTargetService] = aud;
            }
            options = (options ?? RequestOptions.Default).WithHeaders(headers);
        }
        else if (Options.AppSecret is { Length: > 0 })
        {
            body.ClientSecret = Options.AppSecret;
        }
        else
        {
            throw new FeishuCodeException(FeishuErrorCodes.AppSecretAndClientAssertionEmpty,
                "AppSecret and ClientAssertionProvider cannot both be empty for AccessToken APIs");
        }

        var response = await _pipeline.SendAsync(new ApiRequest
        {
            Method = HttpMethod.Post,
            Path = requestUrl,
            Body = body,
            SupportedTokenTypes = [AccessTokenType.None],
        }, options, ct);

        OAuthTokenResponseBody respBody;
        try
        {
            respBody = Options.Serializer.Deserialize<OAuthTokenResponseBody>(response.RawBody);
        }
        catch (Exception ex)
        {
            throw new OAuthTokenException(response.StatusCode, "", $"response unmarshal failed: {ex.Message}");
        }

        if (response.StatusCode != 200 || respBody.Code != 0 || respBody.Error is { Length: > 0 } || respBody.AccessToken is not { Length: > 0 })
        {
            var description = respBody.ErrorDescription is { Length: > 0 } ? respBody.ErrorDescription
                : respBody.Error is { Length: > 0 } ? respBody.Error
                : respBody.AccessToken is { Length: > 0 } ? "" : "access_token is empty";
            throw new OAuthTokenException(response.StatusCode, respBody.Error ?? "", description);
        }

        return new OAuthTokenResult
        {
            Raw = response,
            AccessToken = respBody.AccessToken!,
            TokenType = respBody.TokenType,
            ExpiresIn = respBody.ExpiresIn,
            RefreshToken = respBody.RefreshToken,
            RefreshTokenExpiresIn = respBody.RefreshTokenExpiresIn,
            Scope = respBody.Scope,
        };
    }

    private static string? ExtractHost(string url)
    {
        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;
    }

    private static string BuildProxyUrl(string targetService, string? targetPrefix, string apiPath)
    {
        if (!targetService.Contains("://", StringComparison.Ordinal)) targetService = "https://" + targetService;
        return targetService.TrimEnd('/') + (targetPrefix ?? "").TrimEnd('/') + apiPath;
    }
}

/// <summary>OAuth token 端点错误（对齐 Go AccessTokenError）。</summary>
public sealed class OAuthTokenException : Exception
{
    public int StatusCode { get; }

    public string ErrorType { get; }

    public OAuthTokenException(int statusCode, string errorType, string description)
        : base($"status: {statusCode}, error: {errorType}, description: {description}")
    {
        StatusCode = statusCode;
        ErrorType = errorType;
    }
}

public sealed class OAuthTokenResult
{
    public required string AccessToken { get; init; }

    public string? TokenType { get; init; }

    public int? ExpiresIn { get; init; }

    public string? RefreshToken { get; init; }

    public int? RefreshTokenExpiresIn { get; init; }

    public string? Scope { get; init; }

    [JsonIgnore]
    public ApiResponse? Raw { get; internal init; }
}

internal sealed class OAuthTokenRequestBody
{
    [JsonPropertyName("grant_type")]
    public required string GrantType { get; init; }

    [JsonPropertyName("client_assertion_type")]
    public string? ClientAssertionType { get; set; }

    [JsonPropertyName("client_assertion")]
    public string? ClientAssertion { get; set; }

    [JsonPropertyName("client_id")]
    public string? ClientId { get; set; }

    [JsonPropertyName("client_secret")]
    public string? ClientSecret { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; init; }

    [JsonPropertyName("redirect_uri")]
    public string? RedirectUri { get; init; }

    [JsonPropertyName("code_verifier")]
    public string? CodeVerifier { get; init; }

    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }
}

internal sealed class OAuthTokenResponseBody
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; set; }

    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    [JsonPropertyName("expires_in")]
    public int? ExpiresIn { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("refresh_token_expires_in")]
    public int? RefreshTokenExpiresIn { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }
}
