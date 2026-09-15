using System.Text.Json.Serialization;

namespace Feishu;

internal sealed class TokenManager
{
    /// <summary>提前过期缓冲：token 在标称过期前 3 分钟即视为过期，避免边界失效。</summary>
    private static readonly TimeSpan ExpiryDelta = TimeSpan.FromMinutes(3);

    private const string AppTokenKeyPrefix = "app_access_token";
    private const string TenantTokenKeyPrefix = "tenant_access_token";
    private const string AppTicketKeyPrefix = "app_ticket";

    private readonly FeishuOptions _options;
    private readonly RequestPipeline _pipeline;
    private readonly SingleFlight _singleFlight = new();

    public TokenManager(FeishuOptions options, RequestPipeline pipeline)
    {
        _options = options;
        _pipeline = pipeline;
    }

    // ---- 对外入口 ----

    public Task<string> GetAppAccessTokenAsync(string? appTicket = null, CancellationToken cancellationToken = default) =>
        _singleFlight.RunAsync(AppTokenKey(), async () =>
        {
            var cached = await _options.TokenCache.GetAsync(AppTokenKey(), cancellationToken);
            if (cached is { Length: > 0 }) return cached;

            var (token, ttl) = _options.AppType == FeishuAppType.SelfBuilt
                ? await FetchSelfBuiltAppTokenAsync(cancellationToken)
                : await FetchMarketplaceAppTokenAsync(appTicket, cancellationToken);
            await CacheAsync(AppTokenKey(), token, ttl, cancellationToken);
            return token;
        }, cancellationToken);

    public Task<string> GetTenantAccessTokenAsync(string? tenantKey = null, string? appTicket = null, CancellationToken cancellationToken = default)
    {
        if (_options.ClientAssertionProvider != null)
            return GetTenantTokenByClientAssertionAsync(tenantKey, cancellationToken);

        return _singleFlight.RunAsync(TenantTokenKey(tenantKey), async () =>
        {
            var cached = await _options.TokenCache.GetAsync(TenantTokenKey(tenantKey), cancellationToken);
            if (cached is { Length: > 0 }) return cached;

            var (token, ttl) = _options.AppType == FeishuAppType.SelfBuilt
                ? await FetchSelfBuiltTenantTokenAsync(cancellationToken)
                : await FetchMarketplaceTenantTokenAsync(tenantKey, appTicket, cancellationToken);
            await CacheAsync(TenantTokenKey(tenantKey), token, ttl, cancellationToken);
            return token;
        }, cancellationToken);
    }

    /// <summary>token 失效后驱逐缓存（市场应用的 tenant 依赖 app token，一并驱逐）。</summary>
    public void Invalidate(AccessTokenType tokenType, RequestOptions options)
    {
        switch (tokenType)
        {
            case AccessTokenType.App:
                _options.TokenCache.RemoveAsync(AppTokenKey());
                break;
            case AccessTokenType.Tenant:
                _options.TokenCache.RemoveAsync(TenantTokenKey(options.TenantKey));
                if (_options.AppType == FeishuAppType.Marketplace)
                    _options.TokenCache.RemoveAsync(AppTokenKey());
                break;
        }
        _options.Logger.Info($"token cache invalidated: type={tokenType}");
    }

    // ---- 自建应用 ----

    private async Task<(string Token, TimeSpan Ttl)> FetchSelfBuiltAppTokenAsync(CancellationToken ct)
    {
        var resp = await PostTokenEndpointAsync(RequestPipeline.AppAccessTokenInternalPath, new AppTokenRequest
        {
            AppId = _options.AppId,
            AppSecret = _options.AppSecret,
        }, ct);
        if (resp.AppAccessToken is not { Length: > 0 })
            throw new FeishuCodeException(new CodeError { Code = resp.Code, Msg = resp.Msg });
        return (resp.AppAccessToken, TtlOf(resp.Expire));
    }

    private async Task<(string Token, TimeSpan Ttl)> FetchSelfBuiltTenantTokenAsync(CancellationToken ct)
    {
        var resp = await PostTokenEndpointAsync(RequestPipeline.TenantAccessTokenInternalPath, new AppTokenRequest
        {
            AppId = _options.AppId,
            AppSecret = _options.AppSecret,
        }, ct);
        if (resp.TenantAccessToken is not { Length: > 0 })
            throw new FeishuCodeException(new CodeError { Code = resp.Code, Msg = resp.Msg });
        return (resp.TenantAccessToken, TtlOf(resp.Expire));
    }

    // ---- 商店（ISV）应用 ----

    private async Task<(string Token, TimeSpan Ttl)> FetchMarketplaceAppTokenAsync(string? appTicket, CancellationToken ct)
    {
        if (appTicket is not { Length: > 0 })
        {
            appTicket = await AppTickets.GetAsync(ct);
            if (appTicket is not { Length: > 0 })
                throw new FeishuException("app ticket is empty: 飞书会通过事件推送 app_ticket，请先接收 app_ticket 事件（EventDispatcher 内置支持）");
        }

        var resp = await PostTokenEndpointAsync(RequestPipeline.AppAccessTokenPath, new MarketplaceAppTokenRequest
        {
            AppId = _options.AppId,
            AppSecret = _options.AppSecret,
            AppTicket = appTicket,
        }, ct);
        if (resp.AppAccessToken is not { Length: > 0 })
            throw new FeishuCodeException(new CodeError { Code = resp.Code, Msg = resp.Msg });
        return (resp.AppAccessToken, TtlOf(resp.Expire));
    }

    private async Task<(string Token, TimeSpan Ttl)> FetchMarketplaceTenantTokenAsync(string? tenantKey, string? appTicket, CancellationToken ct)
    {
        if (tenantKey is not { Length: > 0 })
            throw new FeishuException("marketplace app requires RequestOptions.TenantKey");

        var appToken = await GetAppAccessTokenAsync(appTicket, ct);
        var resp = await PostTokenEndpointAsync(RequestPipeline.TenantAccessTokenPath, new MarketplaceTenantTokenRequest
        {
            AppAccessToken = appToken,
            TenantKey = tenantKey,
        }, ct);
        if (resp.TenantAccessToken is not { Length: > 0 })
            throw new FeishuCodeException(new CodeError { Code = resp.Code, Msg = resp.Msg });
        return (resp.TenantAccessToken, TtlOf(resp.Expire));
    }

    // ---- ClientAssertion（JWT）模式 ----

    private async Task<string> GetTenantTokenByClientAssertionAsync(string? tenantKey, CancellationToken ct)
    {
        var oauthBaseUrl = _options.ResolveOAuthBaseUrl();
        var aud = ExtractHost(oauthBaseUrl)
            ?? throw new FeishuException($"invalid oauth base url: {oauthBaseUrl}");
        var tokenKey = $"{TenantTokenKeyPrefix}:client_assertion:{_options.AppId}:{tenantKey}:{aud}";

        return await _singleFlight.RunAsync(tokenKey, async () =>
        {
            var cached = await _options.TokenCache.GetAsync(tokenKey, ct);
            if (cached is { Length: > 0 }) return cached;

            ClientAssertionToken assertion;
            try
            {
                assertion = await _options.ClientAssertionProvider!.RetrieveTokenAsync(aud, ct);
            }
            catch (Exception ex)
            {
                _options.Logger.Warn($"retrieve client assertion failed, aud: {aud}, err: {ex.Message}");
                throw new FeishuCodeException(FeishuErrorCodes.ClientAssertionRetrieveFailed, ex.Message);
            }
            if (assertion.Value is not { Length: > 0 })
                throw new FeishuCodeException(FeishuErrorCodes.ClientAssertionTokenEmpty, "client assertion token is empty");

            // 可选代理：assertion 指定 TargetService 时，把 OAuth 请求转发到代理端点
            var path = assertion.TargetService is { Length: > 0 }
                ? BuildProxyUrl(assertion.TargetService, assertion.TargetPrefix, RequestPipeline.OAuthTokenPath)
                : oauthBaseUrl.TrimEnd('/') + RequestPipeline.OAuthTokenPath;
            var request = new ApiRequest
            {
                Method = HttpMethod.Post,
                Path = path,
                Body = new OAuthTokenRequest
                {
                    GrantType = RequestPipeline.GrantTypeJwtBearer,
                    ClientAssertionType = RequestPipeline.ClientAssertionTypeJwtBearer,
                    ClientAssertion = assertion.Value,
                    ClientId = _options.AppId,
                },
                SupportedTokenTypes = [AccessTokenType.None],
            };
            var headers = assertion.TargetService is { Length: > 0 }
                ? new Dictionary<string, string> { [RequestPipeline.HeaderXTargetService] = aud }
                : null;

            var response = await _pipeline.SendAsync(request, headers == null ? null : new RequestOptions().WithHeaders(headers), ct);
            return await HandleOAuthResponseAsync(response, tokenKey, ct);
        }, ct);
    }

    private async Task<string> HandleOAuthResponseAsync(ApiResponse response, string tokenKey, CancellationToken ct)
    {
        OAuthTokenResponse oauth;
        try
        {
            oauth = _options.Serializer.Deserialize<OAuthTokenResponse>(response.RawBody);
        }
        catch (Exception ex)
        {
            throw new FeishuException($"oauth token response unmarshal failed: {ex.Message}", ex);
        }
        if (oauth.AccessToken is not { Length: > 0 })
        {
            var msg = oauth.ErrorDescription is { Length: > 0 } ? oauth.ErrorDescription
                : oauth.Error is { Length: > 0 } ? oauth.Error
                : "oauth token response missing access token";
            throw new FeishuCodeException(oauth.Code, msg);
        }
        await CacheAsync(tokenKey, oauth.AccessToken, TtlOf(oauth.ExpiresIn), ct);
        return oauth.AccessToken;
    }

    // ---- 基础设施 ----

    private AppTicketManager AppTickets => _pipeline.AppTickets;

    private async Task<AppTokenResponse> PostTokenEndpointAsync(string path, object body, CancellationToken ct)
    {
        var response = await _pipeline.SendAsync(new ApiRequest
        {
            Method = HttpMethod.Post,
            Path = path,
            Body = body,
            SupportedTokenTypes = [AccessTokenType.None],
        }, null, ct);
        return _options.Serializer.Deserialize<AppTokenResponse>(response.RawBody);
    }

    private async Task CacheAsync(string key, string token, TimeSpan ttl, CancellationToken ct)
    {
        try
        {
            await _options.TokenCache.SetAsync(key, token, ttl, ct);
        }
        catch (Exception ex)
        {
            _options.Logger.Warn($"save token cache failed: {ex.Message}");
        }
    }

    private static TimeSpan TtlOf(int expireSeconds)
    {
        var ttl = TimeSpan.FromSeconds(expireSeconds) - ExpiryDelta;
        return ttl < TimeSpan.Zero ? TimeSpan.Zero : ttl;
    }

    private string AppTokenKey() => $"{AppTokenKeyPrefix}-{_options.AppId}";

    private string TenantTokenKey(string? tenantKey) => $"{TenantTokenKeyPrefix}:app_secret:{_options.AppId}:{tenantKey}";

    internal static string AppTicketKey(string appId) => $"{AppTicketKeyPrefix}-{appId}";

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

/// <summary>app_ticket 管理：事件推送写入缓存，token 获取时消费。</summary>
public sealed class AppTicketManager
{
    private readonly FeishuOptions _options;
    private readonly RequestPipeline _pipeline;

    internal AppTicketManager(FeishuOptions options, RequestPipeline pipeline)
    {
        _options = options;
        _pipeline = pipeline;
    }

    /// <summary>读取缓存的 app_ticket；为空时触发一次重推请求（新 ticket 仍需等事件送达）。</summary>
    public async Task<string?> GetAsync(CancellationToken cancellationToken = default)
    {
        var ticket = await _options.TokenCache.GetAsync(TokenManager.AppTicketKey(_options.AppId), cancellationToken);
        if (ticket is not { Length: > 0 })
            await ResendAppTicketAsync(cancellationToken);
        return ticket;
    }

    public Task SetAsync(string appTicket, CancellationToken cancellationToken = default) =>
        _options.TokenCache.SetAsync(TokenManager.AppTicketKey(_options.AppId), appTicket, TimeSpan.Zero, cancellationToken);

    /// <summary>请求飞书重推 app_ticket（POST /open-apis/auth/v3/app_ticket/resend）。</summary>
    public async Task<bool> ResendAppTicketAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _pipeline.SendAsync(new ApiRequest
            {
                Method = HttpMethod.Post,
                Path = RequestPipeline.AppTicketResendPath,
                Body = new AppTokenRequest { AppId = _options.AppId, AppSecret = _options.AppSecret },
                SupportedTokenTypes = [AccessTokenType.None],
            }, null, cancellationToken);
            if (!response.IsJson)
            {
                _options.Logger.Error("resend app_ticket: response content-type not json");
                return false;
            }
            var codeError = _options.Serializer.Deserialize<CodeError>(response.RawBody);
            if (codeError.Code != 0)
            {
                _options.Logger.Error($"resend app_ticket failed: {codeError}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _options.Logger.Error("resend app_ticket error", ex);
            return false;
        }
    }
}

// ---- token 端点请求/响应模型 ----

internal class AppTokenRequest
{
    [JsonPropertyName("app_id")]
    public string? AppId { get; set; }

    [JsonPropertyName("app_secret")]
    public string? AppSecret { get; set; }
}

internal sealed class MarketplaceAppTokenRequest : AppTokenRequest
{
    [JsonPropertyName("app_ticket")]
    public string? AppTicket { get; set; }
}

internal sealed class MarketplaceTenantTokenRequest
{
    [JsonPropertyName("app_access_token")]
    public string? AppAccessToken { get; set; }

    [JsonPropertyName("tenant_key")]
    public string? TenantKey { get; set; }
}

internal sealed class AppTokenResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("msg")]
    public string? Msg { get; set; }

    [JsonPropertyName("expire")]
    public int Expire { get; set; }

    [JsonPropertyName("app_access_token")]
    public string? AppAccessToken { get; set; }

    [JsonPropertyName("tenant_access_token")]
    public string? TenantAccessToken { get; set; }
}

internal sealed class OAuthTokenRequest
{
    [JsonPropertyName("grant_type")]
    public string? GrantType { get; set; }

    [JsonPropertyName("client_assertion_type")]
    public string? ClientAssertionType { get; set; }

    [JsonPropertyName("client_assertion")]
    public string? ClientAssertion { get; set; }

    [JsonPropertyName("client_id")]
    public string? ClientId { get; set; }
}

internal sealed class OAuthTokenResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; set; }

    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }
}
