using System.Buffers.Binary;

namespace Feishu.Channel;

/// <summary>
/// 音视频时长解析（对齐 Go channel/normalize/duration_mp4.go 与 duration_ogg.go）。
/// </summary>
public static class MediaDuration
{
    /// <summary>
    /// 解析 Opus/OGG 时长（毫秒）：从尾部 64KB 反向扫描最后一个 "OggS" 页头，
    /// 取 granule position（页内偏移 6..14，小端 uint64），除以 48kHz 得毫秒。
    /// </summary>
    public static int ParseOpusDuration(Stream stream)
    {
        var size = stream.Seek(0, SeekOrigin.End);
        if (size < 27)
            throw new FeishuChannelException(ChannelErrorCode.FormatError, "file too small to be ogg");

        var readSize = Math.Min(65536L, size);
        stream.Seek(-readSize, SeekOrigin.End);
        var buf = new byte[readSize];
        ReadExactly(stream, buf);

        for (var i = buf.Length - 27; i >= 0; i--)
        {
            if (buf[i] == 0x4f && buf[i + 1] == 0x67 && buf[i + 2] == 0x67 && buf[i + 3] == 0x53) // "OggS"
            {
                var granule = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(i + 6, 8));
                if (granule < 0)
                    throw new FeishuChannelException(ChannelErrorCode.FormatError, "invalid granule position");
                var ms = granule / 48.0;
                if (double.IsNaN(ms) || double.IsInfinity(ms))
                    throw new FeishuChannelException(ChannelErrorCode.FormatError, "invalid duration");
                return (int)Math.Round(ms);
            }
        }

        throw new FeishuChannelException(ChannelErrorCode.FormatError, "OggS not found");
    }

    /// <summary>
    /// 解析 MP4 时长（毫秒）：遍历 ISO BMFF box 找 moov → mvhd，
    /// 按 version 0/1 读取 timescale 与 duration 换算毫秒。
    /// </summary>
    public static int ParseMp4Duration(Stream stream)
    {
        stream.Seek(0, SeekOrigin.Begin);
        var size = stream.Seek(0, SeekOrigin.End);

        var (moovStart, moovEnd) = FindBoxPayload(stream, 0, size, "moov");
        var (mvhdStart, _) = FindBoxPayload(stream, moovStart, moovEnd, "mvhd");

        stream.Seek(mvhdStart, SeekOrigin.Begin);
        var header = new byte[4];
        ReadExactly(stream, header);
        var version = header[0]; // flags 3 字节跳过

        uint timescale;
        double duration;
        if (version == 1)
        {
            // creation(8) + modification(8) + timescale(4) + duration(8)
            var data = new byte[28];
            ReadExactly(stream, data);
            timescale = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16, 4));
            duration = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(20, 8));
        }
        else
        {
            // creation(4) + modification(4) + timescale(4) + duration(4)
            var data = new byte[16];
            ReadExactly(stream, data);
            timescale = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8, 4));
            duration = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12, 4));
        }

        if (timescale == 0)
            throw new FeishuChannelException(ChannelErrorCode.FormatError, "invalid timescale");

        return (int)((duration / timescale) * 1000);
    }

    private static (long PayloadStart, long BoxEnd) FindBoxPayload(Stream s, long begin, long end, string name)
    {
        var p = begin;
        var header = new byte[8];
        while (p + 8 <= end)
        {
            s.Seek(p, SeekOrigin.Begin);
            ReadExactly(s, header);

            var boxSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            var boxType = System.Text.Encoding.ASCII.GetString(header, 4, 4);

            long boxEnd;
            long payloadStart;
            if (boxSize == 1)
            {
                // 64 位大尺寸
                var large = new byte[8];
                ReadExactly(s, large);
                boxEnd = p + (long)BinaryPrimitives.ReadUInt64BigEndian(large);
                payloadStart = p + 16;
            }
            else if (boxSize == 0)
            {
                boxEnd = end; // 到结尾
                payloadStart = p + 8;
            }
            else
            {
                boxEnd = p + boxSize;
                payloadStart = p + 8;
            }

            if (boxEnd <= p || boxEnd > end)
                throw new FeishuChannelException(ChannelErrorCode.FormatError, "invalid box size");

            if (boxType == name)
                return (payloadStart, boxEnd);

            p = boxEnd;
        }

        throw new FeishuChannelException(ChannelErrorCode.FormatError, $"box {name} not found");
    }

    private static void ReadExactly(Stream s, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = s.Read(buffer, offset, buffer.Length - offset);
            if (read <= 0) throw new EndOfStreamException();
            offset += read;
        }
    }
}
