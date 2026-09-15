namespace Feishu;

/// <summary>应用类型：自建应用 或 商店（ISV）应用。</summary>
public enum FeishuAppType
{
    SelfBuilt,
    Marketplace,
}

/// <summary>
/// SDK 全局配置。等价于 Go 版 <c>larkcore.Config</c>，
/// 但以可直接绑定配置源（IConfiguration/环境变量）的普通属性形式暴露。
/// </summary>
public sealed class FeishuOptions
{
    public const string FeishuBaseUrl = "https://open.feishu.cn";
    public const string LarkBaseUrl = "https://open.larksuite.com";
    public const string FeishuOAuthBaseUrl = "https://accounts.feishu.cn";
    public const string LarkOAuthBaseUrl = "https://accounts.larksuite.com";

    /// <summary>是否使用国际版（Lark）域名。默认 false（飞书）。</summary>
    public bool UseLarkDomain
    {
        get => BaseUrl == LarkBaseUrl;
        set => BaseUrl = value ? LarkBaseUrl : FeishuBaseUrl;
    }

    public string AppId { get; set; } = "";

    public string AppSecret { get; set; } = "";

    public FeishuAppType AppType { get; set; } = FeishuAppType.SelfBuilt;

    public string BaseUrl { get; set; } = FeishuBaseUrl;

    /// <summary>OAuth 端点域名（ClientAssertion 模式使用）。留空时按 BaseUrl 推导。</summary>
    public string? OAuthBaseUrl { get; set; }

    /// <summary>是否启用 token 缓存与自动管理。默认 true。</summary>
    public bool EnableTokenCache { get; set; } = true;

    /// <summary>单请求超时。零表示使用 HttpClient 默认值（100s）。</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.Zero;

    public IFeishuLogger Logger { get; set; } = ConsoleFeishuLogger.InfoOnly;

    /// <summary>token / app_ticket 缓存。默认进程内实现，可替换为分布式缓存。</summary>
    public IFeishuCache TokenCache { get; set; } = new MemoryFeishuCache();

    public IFeishuSerializer Serializer { get; set; } = SystemTextJsonFeishuSerializer.Instance;

    /// <summary>JWT Client Assertion 提供方（企业自建 IAM 场景）。</summary>
    public IClientAssertionProvider? ClientAssertionProvider { get; set; }

    public string? HelpdeskId { get; set; }

    public string? HelpdeskToken { get; set; }

    /// <summary>随每个请求携带的默认 header。</summary>
    public IDictionary<string, string> DefaultHeaders { get; } = new Dictionary<string, string>();

    /// <summary>Debug 级别是否打印完整请求/响应（Authorization 已脱敏）。</summary>
    public bool LogRequestAtDebug { get; set; }

    /// <summary>User-Agent 中附加的来源标识，例如 "my-company-crmm"。</summary>
    public string? Source { get; set; }

    /// <summary>事件回调是否跳过签名校验（仅限本地调试）。</summary>
    public bool SkipSignVerify { get; set; }

    /// <summary>用户自定义的 HTTP 消息处理器工厂（测试注入 / 代理配置）。</summary>
    public Func<HttpMessageHandler>? HttpMessageHandlerFactory { get; set; }

    internal string ResolveOAuthBaseUrl() =>
        OAuthBaseUrl is { Length: > 0 } oauth ? oauth : FeishuBaseUrl;
}
