using System.Security.Cryptography;
using System.Text;

namespace Feishu.Events;

/// <summary>飞书事件回调的加解密与验签（协议对齐 Go 版 event/event.go）。</summary>
public static class EventCrypto
{
    /// <summary>
    /// 解密事件体：base64 → AES-256-CBC（key = SHA256(encryptKey)），
    /// IV 取密文前 16 字节，解密后裁剪到首个 <c>{</c> 与最后一个 <c>}</c> 之间。
    /// </summary>
    public static byte[] Decrypt(string encryptBase64, string encryptKey)
    {
        var cipher = Convert.FromBase64String(encryptBase64);
        if (cipher.Length < 16)
            throw new FeishuException("event cipher too short");

        var key = SHA256.HashData(Encoding.UTF8.GetBytes(encryptKey));
        var iv = cipher[..16];
        var data = cipher[16..];
        if (data.Length % 16 != 0)
            throw new FeishuException("event ciphertext is not a multiple of the block size");

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None; // Go 版不剥 padding，靠 { } 裁剪
        using var decryptor = aes.CreateDecryptor();
        var plain = decryptor.TransformFinalBlock(data, 0, data.Length);

        var start = Array.IndexOf(plain, (byte)'{');
        if (start < 0) start = 0;
        var end = Array.LastIndexOf(plain, (byte)'}');
        if (end < 0) end = plain.Length - 1;
        return plain[start..(end + 1)];
    }

    /// <summary>计算签名：hex(sha256(timestamp + nonce + encryptKey + body))。</summary>
    public static string Signature(string timestamp, string nonce, string encryptKey, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(timestamp + nonce + encryptKey + body);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>构造一个已加密的事件体（本地自测 / 回环验证用）。</summary>
    public static string Encrypt(string plainJson, string encryptKey)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(encryptKey));
        var iv = RandomNumberGenerator.GetBytes(16);
        var pad = 16 - Encoding.UTF8.GetByteCount(plainJson) % 16;
        var padded = Encoding.UTF8.GetBytes(plainJson + new string((char)pad, pad));

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();
        var cipher = encryptor.TransformFinalBlock(padded, 0, padded.Length);
        return Convert.ToBase64String([.. iv, .. cipher]);
    }
}
