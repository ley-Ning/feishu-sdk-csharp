using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Feishu.Scene;

/// <summary>二维码信息。</summary>
public sealed record RegistrationQrCodeInfo(string Url, int ExpireIn);

/// <summary>轮询状态变化（polling / slow_down / domain_switched）。</summary>
public sealed record RegistrationStatusChange(string Status, int? Interval = null);

public static class RegistrationStatus
{
    public const string Polling = "polling";
    public const string SlowDown = "slow_down";
    public const string DomainSwitched = "domain_switched";
}

/// <summary>应用创建页预填信息（头像 1-6 个候选、名称/描述支持 {user} 占位）。</summary>
public sealed class RegistrationAppPreset
{
    public List<string>? Avatar { get; set; }

    public string? Name { get; set; }

    public string? Desc { get; set; }
}

/// <summary>确认页增量配置（仅公开 scopes/事件/回调；三值 Preset 语义与 Go 一致）。</summary>
public sealed class RegistrationAppAddons
{
    /// <summary>null=平台默认模板（不携带该键）；false=最小模板（允许空增量）；true=显式默认模板。</summary>
    public bool? Preset { get; set; }

    public RegistrationAddonsScopes? Scopes { get; set; }

    public RegistrationAddonsEvents? Events { get; set; }

    public RegistrationAddonsCallbacks? Callbacks { get; set; }
}

public sealed class RegistrationAddonsScopes
{
    [JsonPropertyName("tenant")]
    public List<string>? Tenant { get; set; }

    [JsonPropertyName("user")]
    public List<string>? User { get; set; }
}

public sealed class RegistrationAddonsEvents
{
    [JsonPropertyName("items")]
    public RegistrationAddonsEventItems? Items { get; set; }
}

public sealed class RegistrationAddonsEventItems
{
    [JsonPropertyName("tenant")]
    public List<string>? Tenant { get; set; }

    [JsonPropertyName("user")]
    public List<string>? User { get; set; }
}

public sealed class RegistrationAddonsCallbacks
{
    [JsonPropertyName("items")]
    public List<string>? Items { get; set; }
}

public sealed record RegistrationUserInfo(string OpenId, string TenantBrand);

public sealed record RegisterAppResult(string ClientId, string ClientSecret, RegistrationUserInfo? UserInfo);

/// <summary>注册失败（access_denied / expired_token / invalid_response / 服务端 error）。</summary>
public sealed class RegisterAppException : Exception
{
    public string ErrorCode { get; }

    public RegisterAppException(string code, string description)
        : base($"registration: code={code}, description={description}")
        => ErrorCode = code;
}

/// <summary>一键建应用选项（对齐 Go registration.Options）。</summary>
public sealed class RegisterAppOptions
{
    public string? Source { get; set; }

    /// <summary>飞书账号域名（默认 https://accounts.feishu.cn）。</summary>
    public string? Domain { get; set; }

    /// <summary>国际版域名（默认 https://accounts.larksuite.com；tenant_brand==lark 时自动切换）。</summary>
    public string? LarkDomain { get; set; }

    public RegistrationAppPreset? AppPreset { get; set; }

    public RegistrationAppAddons? Addons { get; set; }

    public bool CreateOnly { get; set; }

    public string? AppId { get; set; }

    /// <summary>必填：二维码就绪回调。</summary>
    public required Action<RegistrationQrCodeInfo> OnQrCode { get; set; }

    public Action<RegistrationStatusChange>? OnStatusChange { get; set; }

    /// <summary>轮询间隔（测试注入）。</summary>
    internal Func<TimeSpan, CancellationToken, Task> WaitAsync { get; set; } = DefaultWait;

    /// <summary>HTTP 处理器工厂（测试注入）。</summary>
    internal Func<HttpMessageHandler>? HttpMessageHandlerFactory { get; set; }

    private static async Task DefaultWait(TimeSpan interval, CancellationToken ct) =>
        await Task.Delay(interval, ct);
}

/// <summary>
/// 一键建应用（对齐 Go scene/registration）：begin（form 表单）→ 组二维码 URL（含 addons
/// 三段编码：规范化 JSON → gzip → base64url）→ 轮询（slow_down 退避、tenant_brand==lark
/// 自动切国际版域名）→ 拿到 ClientId/ClientSecret。
/// </summary>
public static class AppRegistration
{
    private const string SdkName = "csharp-sdk";
    private const string DefaultFeishuDomain = "https://accounts.feishu.cn";
    private const string DefaultLarkDomain = "https://accounts.larksuite.com";
    private const string Endpoint = "/oauth/v1/app/registration";
    private const int AvatarMaxCount = 6;
    private const int DefaultPollIntervalSeconds = 5;
    private const int DefaultExpireInSeconds = 600;

    public static async Task<RegisterAppResult> RegisterAppAsync(RegisterAppOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.OnQrCode == null)
            throw new ArgumentException("registration: OnQrCode is required", nameof(options));

        var domain = string.IsNullOrEmpty(options.Domain) ? DefaultFeishuDomain : options.Domain!;
        var larkDomain = string.IsNullOrEmpty(options.LarkDomain) ? DefaultLarkDomain : options.LarkDomain!;

        var http = options.HttpMessageHandlerFactory != null
            ? new HttpClient(options.HttpMessageHandlerFactory(), disposeHandler: true)
            : new HttpClient();
        using var _ = http;

        var begin = await PostFormAsync<BeginResponse>(http, domain, new Dictionary<string, string>
        {
            ["action"] = "begin",
            ["archetype"] = "PersonalAgent",
            ["auth_method"] = "client_secret",
            ["request_user_info"] = "open_id",
        }, cancellationToken);
        if (string.IsNullOrEmpty(begin.DeviceCode))
            throw new RegisterAppException("invalid_response", "device_code is empty");
        if (string.IsNullOrEmpty(begin.VerificationUriComplete))
            throw new RegisterAppException("invalid_response", "verification_uri_complete is empty");

        var qrUrl = BuildQrCodeUrl(begin.VerificationUriComplete, options);
        var expireIn = NormalizeExpireIn(begin.ExpireIn);
        options.OnQrCode(new RegistrationQrCodeInfo(qrUrl, expireIn));

        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        pollCts.CancelAfter(TimeSpan.FromSeconds(expireIn));

        var currentDomain = domain;
        var interval = NormalizeInterval(begin.Interval);
        var switchedDomain = false;
        var waitBeforePoll = false;

        while (true)
        {
            if (waitBeforePoll)
            {
                try
                {
                    await options.WaitAsync(TimeSpan.FromSeconds(interval), pollCts.Token);
                }
                catch (OperationCanceledException) when (pollCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new RegisterAppException("expired_token", "registration expired");
                }
            }
            waitBeforePoll = true;

            var resp = await PostFormAsync<PollResponse>(http, currentDomain, new Dictionary<string, string>
            {
                ["action"] = "poll",
                ["device_code"] = begin.DeviceCode,
            }, pollCts.Token);

            if (resp.UserInfo is { TenantBrand: "lark" } && !switchedDomain)
            {
                currentDomain = larkDomain;
                switchedDomain = true;
                options.OnStatusChange?.Invoke(new RegistrationStatusChange(RegistrationStatus.DomainSwitched));
                waitBeforePoll = false;
                continue;
            }

            if (!string.IsNullOrEmpty(resp.ClientId) && !string.IsNullOrEmpty(resp.ClientSecret))
            {
                return new RegisterAppResult(resp.ClientId!, resp.ClientSecret!,
                    resp.UserInfo == null ? null : new RegistrationUserInfo(resp.UserInfo.OpenId ?? "", resp.UserInfo.TenantBrand ?? ""));
            }

            switch (resp.Error)
            {
                case "authorization_pending":
                    options.OnStatusChange?.Invoke(new RegistrationStatusChange(RegistrationStatus.Polling));
                    break;
                case "slow_down":
                    interval += 5;
                    options.OnStatusChange?.Invoke(new RegistrationStatusChange(RegistrationStatus.SlowDown, interval));
                    break;
                case "access_denied":
                case "expired_token":
                    throw new RegisterAppException(resp.Error, resp.ErrorDesc ?? "");
                case null or "":
                    // 服务端未给错误也未给凭证：继续轮询（对齐 Node SDK 行为）
                    break;
                default:
                    throw new RegisterAppException(resp.Error!, resp.ErrorDesc ?? "");
            }
        }
    }

    // ---- 二维码 URL 组装 ----

    internal static string BuildQrCodeUrl(string rawUrl, RegisterAppOptions options)
    {
        var parsed = new UriBuilder(rawUrl);

        // 先保留原 URL 携带的查询参数，再用 SDK 参数覆盖（与 Go query.Set 语义一致）
        var parameters = new List<KeyValuePair<string, string>>();
        var rawQuery = parsed.Query.TrimStart('?');
        if (rawQuery.Length > 0)
        {
            foreach (var pair in rawQuery.Split('&'))
            {
                var eq = pair.IndexOf('=');
                if (eq > 0)
                {
                    var key = Uri.UnescapeDataString(pair[..eq]);
                    var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
                    parameters.Add(new(key, value));
                }
            }
        }

        void Set(string key, string value)
        {
            parameters.RemoveAll(p => p.Key == key);
            parameters.Add(new(key, value));
        }
        Set("from", "sdk");
        Set("tp", "sdk");
        Set("source", string.IsNullOrEmpty(options.Source) ? SdkName : $"{SdkName}/{options.Source}");

        if (options.AppPreset != null)
        {
            if (options.AppPreset.Avatar is { Count: > AvatarMaxCount })
                throw new RegisterAppException("invalid_preset", $"AppPreset.Avatar supports at most {AvatarMaxCount} URLs");
            foreach (var (avatar, idx) in options.AppPreset.Avatar?.Select((a, i) => (a, i)) ?? [])
            {
                if (string.IsNullOrEmpty(avatar))
                    throw new RegisterAppException("invalid_preset", $"AppPreset.Avatar[{idx}] must be a non-empty string");
                parameters.Add(new("avatar", avatar));
            }
            if (!string.IsNullOrEmpty(options.AppPreset.Name))
                parameters.Add(new("name", options.AppPreset.Name));
            if (!string.IsNullOrEmpty(options.AppPreset.Desc))
                parameters.Add(new("desc", options.AppPreset.Desc));
        }

        if (options.Addons != null)
            parameters.Add(new("addons", EncodeAddons(options.Addons)));
        if (options.CreateOnly)
            parameters.Add(new("createOnly", "true"));
        if (!string.IsNullOrEmpty(options.AppId))
        {
            if (string.IsNullOrWhiteSpace(options.AppId))
                throw new RegisterAppException("invalid_preset", "Options.AppID must be a non-empty string");
            parameters.Add(new("clientID", options.AppId));
        }

        parsed.Query = string.Join("&", parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        return parsed.Uri.ToString();
    }

    // ---- Addons 三段编码（对齐 Go addons.go）----

    internal static string EncodeAddons(RegistrationAppAddons addons)
    {
        var payload = NormalizeAddons(addons);
        var body = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(body));
        }
        return Base64UrlEncode(output.ToArray());
    }

    private static Dictionary<string, object?> NormalizeAddons(RegistrationAppAddons addons)
    {
        var itemCount = 0;
        var payload = new Dictionary<string, object?>();

        if (addons.Preset.HasValue)
            payload["preset"] = addons.Preset.Value;

        var scopes = NormalizeStringGroups(new[]
        {
            ("tenant", addons.Scopes?.Tenant, "Addons.Scopes.Tenant"),
            ("user", addons.Scopes?.User, "Addons.Scopes.User"),
        }, ref itemCount);
        if (scopes != null)
            payload["scopes"] = scopes;

        var eventItems = NormalizeStringGroups(new[]
        {
            ("tenant", addons.Events?.Items?.Tenant, "Addons.Events.Items.Tenant"),
            ("user", addons.Events?.Items?.User, "Addons.Events.Items.User"),
        }, ref itemCount);
        if (eventItems != null)
            payload["events"] = new Dictionary<string, object?> { ["items"] = eventItems };

        if (addons.Callbacks?.Items != null)
        {
            var values = NormalizeStringList(addons.Callbacks.Items, "Addons.Callbacks.Items", ref itemCount);
            payload["callbacks"] = new Dictionary<string, object?> { ["items"] = values };
        }

        if (itemCount == 0 && addons.Preset != false)
            throw new RegisterAppException("invalid_addons", "Addons must contain at least one scope, event or callback, unless Preset is false");

        return payload;
    }

    private static Dictionary<string, object?>? NormalizeStringGroups(
        (string Key, List<string>? Values, string Path)[] groups, ref int itemCount)
    {
        Dictionary<string, object?>? result = null;
        foreach (var (key, values, path) in groups)
        {
            if (values == null) continue;
            result ??= new Dictionary<string, object?>();
            result[key] = NormalizeStringList(values, path, ref itemCount);
        }
        return result;
    }

    private static List<string> NormalizeStringList(List<string> values, string path, ref int itemCount)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (string.IsNullOrEmpty(values[i]))
                throw new RegisterAppException("invalid_addons", $"{path}[{i}] must be a non-empty string");
        }
        itemCount += values.Count;
        return values;
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ---- HTTP ----

    private static async Task<T> PostFormAsync<T>(HttpClient http, string domain, Dictionary<string, string> form, CancellationToken ct)
    {
        var content = new FormUrlEncodedContent(form);
        using var response = await http.PostAsync(domain.TrimEnd('/') + Endpoint, content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
            throw new RegisterAppException("invalid_response", "empty response body");
        try
        {
            return JsonSerializer.Deserialize<T>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        }
        catch (JsonException ex)
        {
            throw new RegisterAppException("invalid_response", $"decode response failed: {ex.Message}");
        }
    }

    private static int NormalizeInterval(int interval) => interval <= 0 ? DefaultPollIntervalSeconds : interval;

    private static int NormalizeExpireIn(int expireIn) => expireIn <= 0 ? DefaultExpireInSeconds : expireIn;

    private sealed class BeginResponse
    {
        [JsonPropertyName("device_code")]
        public string? DeviceCode { get; set; }

        [JsonPropertyName("verification_uri_complete")]
        public string? VerificationUriComplete { get; set; }

        [JsonPropertyName("interval")]
        public int Interval { get; set; }

        [JsonPropertyName("expire_in")]
        public int ExpireIn { get; set; }
    }

    private sealed class PollResponse
    {
        [JsonPropertyName("client_id")]
        public string? ClientId { get; set; }

        [JsonPropertyName("client_secret")]
        public string? ClientSecret { get; set; }

        [JsonPropertyName("user_info")]
        public PollUserInfo? UserInfo { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("error_description")]
        public string? ErrorDesc { get; set; }
    }

    private sealed class PollUserInfo
    {
        [JsonPropertyName("open_id")]
        public string? OpenId { get; set; }

        [JsonPropertyName("tenant_brand")]
        public string? TenantBrand { get; set; }
    }
}
