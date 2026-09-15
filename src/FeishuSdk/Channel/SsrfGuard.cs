using System.Net;
using System.Net.Sockets;

namespace Feishu.Channel;

/// <summary>
/// SSRF 防护（对齐 Go channel/safety/ssrf_guard.go）：校验外链 URL 是否可安全拉取。
/// 拦截环回/私网/链路本地/CGNAT/组播/保留段 IPv4 与非公网 IPv6；支持主机名白名单直通。
/// </summary>
public static class SsrfGuard
{
    private static readonly (IPAddress Address, int PrefixLength)[] BlockedV4Blocks = BuildV4Blocks();

    private static (IPAddress, int)[] BuildV4Blocks()
    {
        string[] cidrs =
        [
            "0.0.0.0/8",       // 本网络
            "10.0.0.0/8",      // RFC1918
            "127.0.0.0/8",     // 环回
            "169.254.0.0/16",  // 链路本地
            "172.16.0.0/12",   // RFC1918
            "192.168.0.0/16",  // RFC1918
            "100.64.0.0/10",   // CGNAT
            "192.0.0.0/24",    // IETF 协议分配
            "192.0.2.0/24",    // TEST-NET-1
            "198.18.0.0/15",   // 基准测试
            "198.51.100.0/24", // TEST-NET-2
            "203.0.113.0/24",  // TEST-NET-3
            "224.0.0.0/4",     // 组播
            "240.0.0.0/4",     // 保留
        ];
        return cidrs
            .Select(c =>
            {
                var parts = c.Split('/');
                return (IPAddress.Parse(parts[0]), int.Parse(parts[1]));
            })
            .ToArray();
    }

    /// <summary>校验 URL 可公网访问，不安全时抛出 <see cref="FeishuChannelException"/>（SsrfBlocked）。</summary>
    public static async Task AssertPublicUrlAsync(string url, IReadOnlyCollection<string>? allowlist = null, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            throw Blocked("invalid url");

        if (parsed.Scheme != "http" && parsed.Scheme != "https")
            throw Blocked($"protocol {parsed.Scheme}");

        var host = parsed.Host;
        if (host.Length == 0) throw Blocked("empty host");
        if (allowlist != null && allowlist.Contains(host))
            return; // 白名单直通

        var ips = new List<IPAddress>();
        var literal = IPAddress.TryParse(host, out var ip) ? ip : null;
        if (literal != null)
        {
            ips.Add(literal);
        }
        else
        {
            try
            {
                var entry = await Dns.GetHostEntryAsync(host, cancellationToken);
                ips.AddRange(entry.AddressList);
            }
            catch (Exception ex)
            {
                throw new FeishuChannelException(ChannelErrorCode.SsrfBlocked, $"dns lookup failed: {ex.Message}", ex);
            }
        }

        foreach (var candidate in ips)
        {
            if (IsBlocked(candidate))
                throw Blocked(candidate.ToString());
        }
    }

    internal static bool IsBlocked(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        if (bytes.Length == 4)
            return V4Blocked(bytes);

        // IPv6（含 IPv4 映射地址 ::ffff:a.b.c.d）
        if (ip.IsIPv4MappedToIPv6)
            return V4Blocked(ip.MapToIPv4().GetAddressBytes());
        if (ip.Equals(IPAddress.IPv6Loopback) || ip.Equals(IPAddress.IPv6Any))
            return true;
        if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80) return true; // fe80:: 链路本地
        if ((bytes[0] & 0xfe) == 0xfc) return true;                     // fc00::/7 私网
        if (bytes[0] == 0xff) return true;                              // 组播
        return false;
    }

    private static bool V4Blocked(byte[] b)
    {
        foreach (var (baseAddr, prefix) in BlockedV4Blocks)
        {
            var baseBytes = baseAddr.GetAddressBytes();
            var fullBytes = prefix / 8;
            var remBits = prefix % 8;
            var match = true;
            for (var i = 0; i < fullBytes && match; i++)
                match = b[i] == baseBytes[i];
            if (match && remBits > 0)
                match = (b[fullBytes] & (0xff << (8 - remBits))) == (baseBytes[fullBytes] & (0xff << (8 - remBits)));
            if (match) return true;
        }
        return false;
    }

    private static FeishuChannelException Blocked(string reason) =>
        new(ChannelErrorCode.SsrfBlocked, reason);
}
