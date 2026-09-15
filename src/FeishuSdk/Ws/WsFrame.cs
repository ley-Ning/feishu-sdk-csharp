using System.Buffers.Binary;
using System.Text;

namespace Feishu.Ws;

/// <summary>
/// 飞书长连接二进制帧（pbbp2.proto 的手写编解码，无第三方依赖）：
/// <code>
/// message Header { required string key = 1; required string value = 2; }
/// message Frame {
///   required uint64 SeqID          = 1;  // varint
///   required uint64 LogID          = 2;  // varint
///   required int32  service        = 3;  // varint
///   required int32  method         = 4;  // varint（0=控制帧 1=数据帧）
///   repeated Header headers        = 5;
///   optional string payload_encoding = 6;
///   optional string payload_type     = 7;
///   optional bytes  payload          = 8;
///   optional string LogIDNew         = 9;
/// }
/// </code>
/// </summary>
public sealed class WsFrame
{
    public ulong SeqId { get; set; }

    public ulong LogId { get; set; }

    public int Service { get; set; }

    /// <summary>0 = 控制帧（ping/pong），1 = 数据帧（事件/卡片）。</summary>
    public int Method { get; set; }

    public List<(string Key, string Value)> Headers { get; set; } = new();

    public string? PayloadEncoding { get; set; }

    public string? PayloadType { get; set; }

    public byte[]? Payload { get; set; }

    public string? LogIdNew { get; set; }

    public string? GetHeader(string key)
    {
        foreach (var (k, v) in Headers)
            if (k == key) return v;
        return null;
    }

    public void SetHeader(string key, string value)
    {
        for (var i = 0; i < Headers.Count; i++)
        {
            if (Headers[i].Key == key)
            {
                Headers[i] = (key, value);
                return;
            }
        }
        Headers.Add((key, value));
    }

    public int GetHeaderInt(string key) =>
        int.TryParse(GetHeader(key), out var v) ? v : 0;

    public static WsFrame Parse(ReadOnlySpan<byte> data)
    {
        var frame = new WsFrame();
        var i = 0;
        while (i < data.Length)
        {
            var (tag, wireType) = ReadTag(data, ref i);
            var field = tag >> 3;
            switch (field)
            {
                case 1:
                    RequireWireType(wireType, 0, field);
                    frame.SeqId = ReadVarint(data, ref i);
                    break;
                case 2:
                    RequireWireType(wireType, 0, field);
                    frame.LogId = ReadVarint(data, ref i);
                    break;
                case 3:
                    RequireWireType(wireType, 0, field);
                    frame.Service = (int)ReadVarint(data, ref i);
                    break;
                case 4:
                    RequireWireType(wireType, 0, field);
                    frame.Method = (int)ReadVarint(data, ref i);
                    break;
                case 5:
                {
                    RequireWireType(wireType, 2, field);
                    var headerBytes = ReadLengthDelimited(data, ref i);
                    var (key, value) = ParseHeader(headerBytes);
                    frame.Headers.Add((key, value));
                    break;
                }
                case 6:
                    frame.PayloadEncoding = Encoding.UTF8.GetString(ReadLengthDelimited(data, ref i));
                    break;
                case 7:
                    frame.PayloadType = Encoding.UTF8.GetString(ReadLengthDelimited(data, ref i));
                    break;
                case 8:
                    frame.Payload = ReadLengthDelimited(data, ref i).ToArray();
                    break;
                case 9:
                    frame.LogIdNew = Encoding.UTF8.GetString(ReadLengthDelimited(data, ref i));
                    break;
                default:
                    SkipField(data, ref i, wireType, field);
                    break;
            }
        }
        return frame;
    }

    public byte[] ToBytes()
    {
        var buffer = new MemoryStream(64 + (Payload?.Length ?? 0));
        WriteVarintField(buffer, 1, SeqId);
        WriteVarintField(buffer, 2, LogId);
        WriteVarintField(buffer, 3, (ulong)Service);
        WriteVarintField(buffer, 4, (ulong)Method);
        foreach (var (key, value) in Headers)
        {
            var headerBytes = BuildHeader(key, value);
            WriteTag(buffer, 5, 2);
            WriteVarint(buffer, (ulong)headerBytes.Length);
            buffer.Write(headerBytes);
        }
        if (PayloadEncoding != null) WriteStringField(buffer, 6, PayloadEncoding);
        if (PayloadType != null) WriteStringField(buffer, 7, PayloadType);
        if (Payload != null)
        {
            WriteTag(buffer, 8, 2);
            WriteVarint(buffer, (ulong)Payload.Length);
            buffer.Write(Payload);
        }
        if (LogIdNew != null) WriteStringField(buffer, 9, LogIdNew);
        return buffer.ToArray();
    }

    // ---- 解码原语 ----

    private static (int Tag, int WireType) ReadTag(ReadOnlySpan<byte> data, ref int i)
    {
        var tag = (int)ReadVarint(data, ref i);
        return (tag, tag & 0x7);
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int i)
    {
        ulong value = 0;
        var shift = 0;
        while (true)
        {
            if (i >= data.Length) throw new FeishuWsProtocolException("varint truncated");
            var b = data[i++];
            value |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80) return value;
            shift += 7;
            if (shift >= 64) throw new FeishuWsProtocolException("varint overflow");
        }
    }

    private static ReadOnlySpan<byte> ReadLengthDelimited(ReadOnlySpan<byte> data, ref int i)
    {
        var len = (int)ReadVarint(data, ref i);
        if (len < 0 || i + len > data.Length) throw new FeishuWsProtocolException("length-delimited field out of range");
        var slice = data.Slice(i, len);
        i += len;
        return slice;
    }

    private static (string Key, string Value) ParseHeader(ReadOnlySpan<byte> data)
    {
        string? key = null, value = null;
        var i = 0;
        while (i < data.Length)
        {
            var (tag, wireType) = ReadTag(data, ref i);
            switch (tag >> 3)
            {
                case 1:
                    key = Encoding.UTF8.GetString(ReadLengthDelimited(data, ref i));
                    break;
                case 2:
                    value = Encoding.UTF8.GetString(ReadLengthDelimited(data, ref i));
                    break;
                default:
                    SkipField(data, ref i, wireType, tag >> 3);
                    break;
            }
        }
        return (key ?? "", value ?? "");
    }

    private static void SkipField(ReadOnlySpan<byte> data, ref int i, int wireType, int field)
    {
        switch (wireType)
        {
            case 0:
                ReadVarint(data, ref i);
                break;
            case 1:
                i += 8;
                break;
            case 2:
                var len = (int)ReadVarint(data, ref i);
                i += len;
                break;
            case 5:
                i += 4;
                break;
            default:
                throw new FeishuWsProtocolException($"illegal wire type {wireType} for field {field}");
        }
        if (i > data.Length) throw new FeishuWsProtocolException("field out of range");
    }

    private static void RequireWireType(int actual, int expected, int field)
    {
        if (actual != expected)
            throw new FeishuWsProtocolException($"wrong wire type {actual} for field {field}");
    }

    // ---- 编码原语 ----

    private static void WriteTag(Stream stream, int field, int wireType)
    {
        WriteVarint(stream, (ulong)((field << 3) | wireType));
    }

    private static void WriteVarint(Stream stream, ulong value)
    {
        Span<byte> buf = stackalloc byte[10];
        var n = 0;
        while (value >= 0x80)
        {
            buf[n++] = (byte)(value | 0x80);
            value >>= 7;
        }
        buf[n++] = (byte)value;
        stream.Write(buf[..n]);
    }

    private static void WriteVarintField(Stream stream, int field, ulong value)
    {
        WriteTag(stream, field, 0);
        WriteVarint(stream, value);
    }

    private static void WriteStringField(Stream stream, int field, string value)
    {
        WriteTag(stream, field, 2);
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarint(stream, (ulong)bytes.Length);
        stream.Write(bytes);
    }

    private static byte[] BuildHeader(string key, string value)
    {
        var buffer = new MemoryStream();
        WriteStringField(buffer, 1, key);
        WriteStringField(buffer, 2, value);
        return buffer.ToArray();
    }
}

public sealed class FeishuWsProtocolException : Exception
{
    public FeishuWsProtocolException(string message) : base(message)
    {
    }
}
