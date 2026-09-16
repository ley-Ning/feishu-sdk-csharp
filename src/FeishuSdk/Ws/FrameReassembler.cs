namespace Feishu.Ws;

/// <summary>分包重组器：大消息按 (message_id, sum, seq) 拆包传输，等齐后拼接。</summary>
/// <remarks>
/// 对齐 Go 版 ws 的分包处理：数据帧 headers 带 sum（总片数）/seq（当前片序），
/// 单片消息 sum=1 直接透传；多片消息等全部到齐才拼接返回，超时（TTL）丢弃防泄漏。
/// </remarks>
internal sealed class FrameReassembler(TimeSpan ttl)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (byte[][] Parts, long Deadline)> _pending = new();

    /// <summary>投递一个分包；全部到齐返回拼接后的完整载荷，否则返回 null 继续等。</summary>
    public byte[]? Combine(string messageId, int sum, int seq, byte[] part)
    {
        lock (_gate)
        {
            Cleanup();
            if (!_pending.TryGetValue(messageId, out var entry))
            {
                var parts = new byte[sum][];
                parts[seq] = part;
                _pending[messageId] = (parts, Environment.TickCount64 + (long)ttl.TotalMilliseconds);
                return null;
            }

            entry.Parts[seq] = part;
            var capacity = 0;
            foreach (var p in entry.Parts)
            {
                if (p == null)
                {
                    _pending[messageId] = entry;
                    return null;
                }
                capacity += p.Length;
            }

            _pending.Remove(messageId);
            var combined = new byte[capacity];
            var offset = 0;
            foreach (var p in entry.Parts)
            {
                Buffer.BlockCopy(p, 0, combined, offset, p.Length);
                offset += p.Length;
            }
            return combined;
        }
    }

    /// <summary>清扫过期未齐的分包组（对端丢片时防内存泄漏）。</summary>
    private void Cleanup()
    {
        var now = Environment.TickCount64;
        List<string>? dead = null;
        foreach (var (key, (_, deadline)) in _pending)
            if (deadline <= now) (dead ??= []).Add(key);
        if (dead != null)
            foreach (var key in dead)
                _pending.Remove(key);
    }
}
