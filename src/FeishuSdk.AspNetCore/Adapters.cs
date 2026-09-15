using Feishu;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace Feishu.AspNetCore;

/// <summary>Microsoft.Extensions.Logging 适配器。</summary>
public sealed class MelFeishuLoggerAdapter(ILogger logger) : IFeishuLogger
{
    public bool IsEnabled(FeishuLogLevel level) => logger.IsEnabled(ToMel(level));

    public void Log(FeishuLogLevel level, string message, Exception? exception = null) =>
        logger.Log(ToMel(level), default, message, exception, static (s, _) => s);

    private static LogLevel ToMel(FeishuLogLevel level) => level switch
    {
        FeishuLogLevel.Debug => LogLevel.Debug,
        FeishuLogLevel.Info => LogLevel.Information,
        FeishuLogLevel.Warn => LogLevel.Warning,
        _ => LogLevel.Error,
    };
}

/// <summary>IDistributedCache 适配器（多实例共享 token 缓存）。</summary>
public sealed class DistributedCacheFeishuAdapter(IDistributedCache cache) : IFeishuCache
{
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var bytes = await cache.GetAsync(CacheKey(key), cancellationToken);
        return bytes == null ? null : System.Text.Encoding.UTF8.GetString(bytes);
    }

    public Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default) =>
        cache.SetAsync(CacheKey(key), System.Text.Encoding.UTF8.GetBytes(value), new DistributedCacheEntryOptions
        {
            // ttl<=0 永不过期；分布式缓存用一年近似
            AbsoluteExpirationRelativeToNow = ttl <= TimeSpan.Zero ? TimeSpan.FromDays(365) : ttl,
        }, cancellationToken);

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default) =>
        cache.RemoveAsync(CacheKey(key), cancellationToken);

    private static string CacheKey(string key) => $"feishu:{key}";
}
