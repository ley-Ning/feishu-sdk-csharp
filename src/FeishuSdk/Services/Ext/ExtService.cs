using System.Text.Json.Serialization;

namespace Feishu.Services.Ext;

/// <summary>
/// 扩展服务（对齐 Go service/ext）：DriveExplorer 建文件 + authen 便捷封装。
/// </summary>
public sealed class ExtService
{
    private readonly RequestPipeline _pipeline;

    internal ExtService(RequestPipeline pipeline)
    {
        _pipeline = pipeline;
        DriveExplorer = new ExtDriveExplorerResource(pipeline);
        Authen = new ExtAuthenResource(pipeline);
    }

    public ExtDriveExplorerResource DriveExplorer { get; }

    public ExtAuthenResource Authen { get; }
}

public sealed class ExtDriveExplorerResource
{
    private readonly RequestPipeline _pipeline;

    internal ExtDriveExplorerResource(RequestPipeline pipeline) => _pipeline = pipeline;

    /// <summary>创建云空间文件（POST /open-apis/drive/explorers/v2/file/:folderToken）。</summary>
    public Task<ExtCreateFileResponse> CreateFileAsync(string folderToken, object? body, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _pipeline.SendForAsync<ExtCreateFileResponse>(new ApiRequest
        {
            Method = HttpMethod.Post,
            Path = "/open-apis/drive/explorer/v2/file/:folderToken",
            PathParams = { ["folderToken"] = folderToken },
            Body = body,
            SupportedTokenTypes = [AccessTokenType.Tenant, AccessTokenType.User],
        }, options, cancellationToken);
}

public sealed class ExtAuthenResource
{
    private readonly RequestPipeline _pipeline;

    internal ExtAuthenResource(RequestPipeline pipeline) => _pipeline = pipeline;

    /// <summary>获取 user_access_token（app token；POST /open-apis/authen/v1/access_token）。</summary>
    public Task<ExtAuthenTokenResponse> AuthenAccessTokenAsync(string userAccessCode, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _pipeline.SendForAsync<ExtAuthenTokenResponse>(new ApiRequest
        {
            Method = HttpMethod.Post,
            Path = "/open-apis/authen/v1/access_token",
            Body = new { grant_type = "authorization_code", user_access_code = userAccessCode },
            SupportedTokenTypes = [AccessTokenType.App],
        }, options, cancellationToken);

    /// <summary>刷新 user_access_token（POST /open-apis/authen/v1/refresh_access_token）。</summary>
    public Task<ExtAuthenTokenResponse> RefreshAuthenAccessTokenAsync(string refreshToken, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        _pipeline.SendForAsync<ExtAuthenTokenResponse>(new ApiRequest
        {
            Method = HttpMethod.Post,
            Path = "/open-apis/authen/v1/refresh_access_token",
            Body = new { grant_type = "refresh_token", refresh_token = refreshToken },
            SupportedTokenTypes = [AccessTokenType.App],
        }, options, cancellationToken);

    /// <summary>获取用户信息（GET /open-apis/authen/v1/user_info，user token）。</summary>
    public Task<ExtUserInfoResponse> AuthenUserInfoAsync(RequestOptions options, CancellationToken cancellationToken = default) =>
        _pipeline.SendForAsync<ExtUserInfoResponse>(new ApiRequest
        {
            Method = HttpMethod.Get,
            Path = "/open-apis/authen/v1/user_info",
            SupportedTokenTypes = [AccessTokenType.User],
        }, options, cancellationToken);
}

public sealed class ExtCreateFileResponse : FeishuResponse
{
    [JsonPropertyName("data")]
    public ExtCreateFileData? Data { get; set; }
}

public sealed class ExtCreateFileData
{
    [JsonPropertyName("file_token")]
    public string? FileToken { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

public sealed class ExtAuthenTokenResponse : FeishuResponse
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
}

public sealed class ExtUserInfoResponse : FeishuResponse
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("open_id")]
    public string? OpenId { get; set; }
}
