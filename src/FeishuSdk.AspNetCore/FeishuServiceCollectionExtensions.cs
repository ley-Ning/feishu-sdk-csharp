using Feishu;
using Feishu.AspNetCore;
using Feishu.Ws;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>FeishuSdk 的 ASP.NET Core 集成。</summary>
public static class FeishuServiceCollectionExtensions
{
    public const string FeishuHttpClientName = "Feishu";

    /// <summary>
    /// 注册 FeishuClient 单例：走 IHttpClientFactory、Microsoft.Extensions.Logging，
    /// 若容器里有 IDistributedCache 则自动用作 token 缓存（多实例部署推荐 Redis）。
    /// </summary>
    public static IServiceCollection AddFeishu(this IServiceCollection services, Action<FeishuOptions> configure)
    {
        services.AddHttpClient(FeishuHttpClientName, (sp, client) =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<FeishuOptions>>().Value;
            if (options.RequestTimeout > TimeSpan.Zero)
                client.Timeout = options.RequestTimeout;
        });
        services.AddOptions<FeishuOptions>()
            .Configure(configure)
            .Configure<IServiceProvider>((options, provider) =>
            {
                options.Logger = provider.GetService<ILoggerFactory>() is { } factory
                    ? new MelFeishuLoggerAdapter(factory.CreateLogger("Feishu"))
                    : NullFeishuLogger.Instance;
                if (provider.GetService<IDistributedCache>() is { } distributed)
                    options.TokenCache = new DistributedCacheFeishuAdapter(distributed);
            });

        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<FeishuOptions>>().Value;
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            return new FeishuClient(options, httpClientFactory.CreateClient(FeishuHttpClientName));
        });
        return services;
    }

    /// <summary>
    /// 注册 WebSocket 长连接托管服务（应用启动即连接，停止即断开）。
    /// FeishuWsClient 单例注册后可通过 Bind(EventDispatcher) 挂载处理器。
    /// </summary>
    public static IServiceCollection AddFeishuWebSocket(this IServiceCollection services, Action<FeishuWsOptions>? configureWs = null)
    {
        services.AddOptions<FeishuWsOptions>()
            .Configure(configureWs ?? (_ => { }))
            .Configure<IServiceProvider>((options, provider) =>
            {
                if (provider.GetService<ILoggerFactory>() is { } factory)
                    options.Logger = new MelFeishuLoggerAdapter(factory.CreateLogger("Feishu.Ws"));
            });
        services.TryAddSingleton(sp =>
        {
            var wsOptions = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<FeishuWsOptions>>().Value;
            var feishu = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<FeishuOptions>>().Value;
            return new FeishuWsClient(feishu.AppId, feishu.AppSecret, wsOptions);
        });
        services.AddHostedService<FeishuWsHostedService>();
        return services;
    }
}
