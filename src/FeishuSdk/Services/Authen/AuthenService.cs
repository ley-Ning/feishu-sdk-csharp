using System.Text.Json.Serialization;

namespace Feishu.Services.Authen;

/// <summary>
/// 用户身份认证服务（authen/v1，对齐 Go service/authen/v1：五个端点、token 类型 App/User）。
/// v1 接口官方标记为历史版本，新集成建议使用 client.OAuth（/oauth/v3/token）。
/// </summary>
public sealed class AuthenService
{
    private readonly RequestPipeline _pipeline;

    internal AuthenService(RequestPipeline pipeline) => _pipeline = pipeline;

    /// <summary>获取 user_access_token（v1 历史版本，POST /open-apis/authen/v1/access_token）。</summary>
    public Task<CreateAccessTokenResponse> CreateAccessTokenAsync(string userAccessCode, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _pipeline.SendForAsync<CreateAccessTokenResponse>(new ApiRequest
        {
            Method = HttpMethod.Post,
            Path = "/open-apis/authen/v1/access_token",
            Body = new LegacyTokenBody
            {
                GrantType = "authorization_code",
                UserAccessCode = userAccessCode,
            },
            SupportedTokenTypes = [AccessTokenType.App],
        }, options, cancellationToken);

    /// <summary>获取 user_access_token（OIDC，POST /open-apis/authen/v1/oidc/access_token）。</summary>
    public Task<CreateOidcAccessTokenResponse> CreateOidcAccessTokenAsync(string code, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _pipeline.SendForAsync<CreateOidcAccessTokenResponse>(new ApiRequest
        {
            Method = HttpMethod.Post,
            Path = "/open-apis/authen/v1/oidc/access_token",
            Body = new OidcTokenBody
            {
                GrantType = "authorization_code",
                Code = code,
            },
            SupportedTokenTypes = [AccessTokenType.App],
        }, options, cancellationToken);

    /// <summary>刷新 user_access_token（OIDC，POST /open-apis/authen/v1/oidc/refresh_access_token）。</summary>
    public Task<CreateOidcRefreshAccessTokenResponse> CreateOidcRefreshAccessTokenAsync(string refreshToken, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _pipeline.SendForAsync<CreateOidcRefreshAccessTokenResponse>(new ApiRequest
        {
            Method = HttpMethod.Post,
            Path = "/open-apis/authen/v1/oidc/refresh_access_token",
            Body = new OidcRefreshTokenBody
            {
                GrantType = "refresh_token",
                RefreshToken = refreshToken,
            },
            SupportedTokenTypes = [AccessTokenType.App],
        }, options, cancellationToken);

    /// <summary>刷新 user_access_token（v1 历史版本，POST /open-apis/authen/v1/refresh_access_token）。</summary>
    public Task<CreateRefreshAccessTokenResponse> CreateRefreshAccessTokenAsync(string refreshToken, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _pipeline.SendForAsync<CreateRefreshAccessTokenResponse>(new ApiRequest
        {
            Method = HttpMethod.Post,
            Path = "/open-apis/authen/v1/refresh_access_token",
            Body = new OidcRefreshTokenBody
            {
                GrantType = "refresh_token",
                RefreshToken = refreshToken,
            },
            SupportedTokenTypes = [AccessTokenType.App],
        }, options, cancellationToken);

    /// <summary>获取用户信息（GET /open-apis/authen/v1/user_info，需 user_access_token）。</summary>
    public Task<GetUserInfoResponse> GetUserInfoAsync(RequestOptions options, CancellationToken cancellationToken = default) =>
        _pipeline.SendForAsync<GetUserInfoResponse>(new ApiRequest
        {
            Method = HttpMethod.Get,
            Path = "/open-apis/authen/v1/user_info",
            SupportedTokenTypes = [AccessTokenType.User],
        }, options, cancellationToken);
}

internal sealed class LegacyTokenBody
{
    [JsonPropertyName("grant_type")]
    public string? GrantType { get; set; }

    [JsonPropertyName("user_access_code")]
    public string? UserAccessCode { get; set; }
}

internal sealed class OidcTokenBody
{
    [JsonPropertyName("grant_type")]
    public string? GrantType { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }
}

internal sealed class OidcRefreshTokenBody
{
    [JsonPropertyName("grant_type")]
    public string? GrantType { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }
}

/// <summary>authen v1 响应为扁平结构（无 data 包裹）。</summary>
public abstract class AuthenV1Response : FeishuResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    [JsonPropertyName("expires_in")]
    public long? ExpiresIn { get; set; }

    [JsonPropertyName("refresh_token_expires_in")]
    public long? RefreshTokenExpiresIn { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }
}

public sealed class CreateAccessTokenResponse : AuthenV1Response;

public sealed class CreateOidcAccessTokenResponse : AuthenV1Response
{
    [JsonPropertyName("id_token")]
    public string? IdToken { get; set; }
}

public sealed class CreateOidcRefreshAccessTokenResponse : AuthenV1Response
{
    [JsonPropertyName("id_token")]
    public string? IdToken { get; set; }
}

public sealed class CreateRefreshAccessTokenResponse : AuthenV1Response
{
    [JsonPropertyName("user_id")]
    public string? UserId { get; set; }

    [JsonPropertyName("open_id")]
    public string? OpenId { get; set; }

    [JsonPropertyName("union_id")]
    public string? UnionId { get; set; }
}

public sealed class GetUserInfoResponse : FeishuResponse
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("en_name")]
    public string? EnName { get; set; }

    [JsonPropertyName("open_id")]
    public string? OpenId { get; set; }

    [JsonPropertyName("union_id")]
    public string? UnionId { get; set; }

    [JsonPropertyName("user_id")]
    public string? UserId { get; set; }

    [JsonPropertyName("avatar_url")]
    public string? AvatarUrl { get; set; }

    [JsonPropertyName("avatar_thumb")]
    public string? AvatarThumb { get; set; }

    [JsonPropertyName("avatar_middle")]
    public string? AvatarMiddle { get; set; }

    [JsonPropertyName("avatar_big")]
    public string? AvatarBig { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("mobile")]
    public string? Mobile { get; set; }

    [JsonPropertyName("tenant_key")]
    public string? TenantKey { get; set; }
}
