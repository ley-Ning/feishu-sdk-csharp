using System.Collections.Concurrent;

namespace Feishu.Channel;

/// <summary>
/// 去重缓存：LRU + TTL（对齐 Go safety.DedupCache）。
/// 首次出现的 key 返回 false 并记录；窗口内再次出现返回 true。
/// </summary>
public sealed class DedupCache
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly TimeSpan _ttl;
    private readonly Dictionary<string, LinkedListNode<(string Key, long ExpiresAt)>> _items = new();
    private readonly LinkedList<(string Key, long ExpiresAt)> _lru = new();

    public DedupCache(int capacity, TimeSpan ttl)
    {
        _capacity = capacity;
        _ttl = ttl;
    }

    public bool IsDuplicate(string key)
    {
        if (key.Length == 0) return false;
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_items.TryGetValue(key, out var node))
            {
                if (node.Value.ExpiresAt > now)
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node); // LRU 提升
                    return true;
                }
                _lru.Remove(node);
                _items.Remove(key);
            }

            var entry = _lru.AddFirst((key, now + (long)_ttl.TotalMilliseconds));
            _items[key] = entry;
            if (_lru.Count > _capacity)
            {
                var oldest = _lru.Last!;
                _lru.RemoveLast();
                _items.Remove(oldest.Value.Key);
            }
            return false;
        }
    }
}

/// <summary>短 TTL 处理锁：同一事件并发到达只放行一个（对齐 Go safety.ProcessingLock）。</summary>
public sealed class ProcessingLock
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _locks = new(); // id -> expireAt(ms)
    private readonly long _ttlMs;

    public ProcessingLock(TimeSpan ttl) => _ttlMs = (long)ttl.TotalMilliseconds;

    public bool Acquire(string id)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_locks.TryGetValue(id, out var exp) && exp > now) return false;
            _locks[id] = now + _ttlMs;
            return true;
        }
    }

    public void Release(string id)
    {
        lock (_gate) _locks.Remove(id);
    }
}

/// <summary>过期消息检测（对齐 Go safety.IsStale：零/负时间戳不视为过期）。</summary>
public static class StaleDetector
{
    public static bool IsStale(long createTimeMs, TimeSpan window)
    {
        if (createTimeMs <= 0) return false;
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - createTimeMs > window.TotalMilliseconds;
    }
}

/// <summary>策略门控（对齐 Go safety.PolicyGate：群白名单→必须@→@all；私信 open/disabled/allowlist）。</summary>
public sealed class PolicyGate
{
    private readonly object _gate = new();
    private ChannelPolicyConfig _cfg;

    public PolicyGate(ChannelPolicyConfig cfg) => _cfg = cfg;

    public ChannelPolicyDecision Evaluate(NormalizedMessage msg)
    {
        lock (_gate)
        {
            return msg.ChatType == "group" ? EvaluateGroup(msg) : EvaluateDm(msg);
        }
    }

    private ChannelPolicyDecision EvaluateGroup(NormalizedMessage msg)
    {
        if (_cfg.GroupAllowlist is { Count: > 0 } && !_cfg.GroupAllowlist.Contains(msg.ChatId))
            return new(false, ChannelRejectReason.GroupNotAllowed);

        var requireMention = _cfg.RequireMention ?? true;
        if (requireMention && !msg.MentionedBot && !msg.MentionAll)
            return new(false, ChannelRejectReason.NoMention);

        var respondToMentionAll = _cfg.RespondToMentionAll ?? false;
        if (msg.MentionAll && !respondToMentionAll)
            return new(false, ChannelRejectReason.MentionAllBlocked);

        return new(true);
    }

    private ChannelPolicyDecision EvaluateDm(NormalizedMessage msg)
    {
        var mode = string.IsNullOrEmpty(_cfg.DmMode) ? "open" : _cfg.DmMode!;
        if (mode == "disabled")
            return new(false, ChannelRejectReason.DmDisabled);
        if (mode == "allowlist")
        {
            var allowed = _cfg.DmAllowlist?.Contains(msg.UserId) ?? false;
            if (!allowed)
                return new(false, ChannelRejectReason.SenderNotAllowed);
        }
        return new(true);
    }

    public void UpdateConfig(Action<ChannelPolicyConfig> mutate)
    {
        lock (_gate) mutate(_cfg);
    }

    public ChannelPolicyConfig GetConfig()
    {
        lock (_gate) return _cfg;
    }
}
