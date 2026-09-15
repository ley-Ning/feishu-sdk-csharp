namespace Feishu;

/// <summary>
/// token / app_ticket 的缓存抽象。
/// ttl &lt;= 0 表示永不过期。多实例部署时应替换为分布式实现（如 Redis），
/// 避免每个实例各自向飞书刷新 token（有频率限制）。
/// </summary>
public interface IFeishuCache
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default);

    /// <summary>删除缓存项（token 失效时主动驱逐，等待下次请求重新获取）。</summary>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>进程内 TTL 缓存，惰性过期 + 定期清扫。</summary>
public sealed class MemoryFeishuCache : IFeishuCache
{
    private sealed record Entry(string Value, long ExpireAt); // ExpireAt == 0 表示永不过期

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new();
    private readonly TimeSpan _sweepInterval;
    private long _nextSweepAt;

    public MemoryFeishuCache(TimeSpan? sweepInterval = null)
    {
        _sweepInterval = sweepInterval ?? TimeSpan.FromSeconds(30);
        _nextSweepAt = Environment.TickCount64 + (long)_sweepInterval.TotalMilliseconds;
    }

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry) && !Expired(entry))
                return Task.FromResult<string?>(entry.Value);
            _entries.Remove(key);
            return Task.FromResult<string?>(null);
        }
    }

    public Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        var expireAt = ttl <= TimeSpan.Zero ? 0 : Environment.TickCount64 + (long)ttl.TotalMilliseconds;
        lock (_gate)
        {
            _entries[key] = new Entry(value, expireAt);
            MaybeSweep();
        }
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        lock (_gate) _entries.Remove(key);
        return Task.CompletedTask;
    }

    private bool Expired(Entry entry) =>
        entry.ExpireAt != 0 && entry.ExpireAt <= Environment.TickCount64;

    private void MaybeSweep()
    {
        var now = Environment.TickCount64;
        if (now < _nextSweepAt) return;
        _nextSweepAt = now + (long)_sweepInterval.TotalMilliseconds;
        List<string>? dead = null;
        foreach (var (key, entry) in _entries)
        {
            if (Expired(entry)) (dead ??= []).Add(key);
        }
        if (dead != null)
            foreach (var key in dead)
                _entries.Remove(key);
    }
}

/// <summary>
/// 按 key 的单飞（single-flight）：同一 key 的并发执行只放行一个工厂调用，其余等待其结果。
/// 用于 token 获取，避免缓存失效瞬间的惊群（Go 版并发 miss 会重复打 token 接口）。
/// </summary>
public sealed class SingleFlight : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SemaphoreSlim> _inFlight = new();
    private bool _disposed;

    public async Task<T> RunAsync<T>(string key, Func<Task<T>> factory, CancellationToken cancellationToken = default)
    {
        SemaphoreSlim sem;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_inFlight.TryGetValue(key, out sem!))
            {
                sem = new SemaphoreSlim(1, 1);
                _inFlight[key] = sem;
            }
        }

        await sem.WaitAsync(cancellationToken);
        try
        {
            return await factory();
        }
        finally
        {
            sem.Release();
            lock (_gate)
            {
                // 无人排队时移除信号量，避免字典随 key 数量无限增长
                if (_inFlight.TryGetValue(key, out var current) && current == sem && sem.CurrentCount == 1)
                    _inFlight.Remove(key);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var sem in _inFlight.Values) sem.Dispose();
            _inFlight.Clear();
        }
    }
}
