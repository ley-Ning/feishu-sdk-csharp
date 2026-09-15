using System.Text;
using Feishu;
using Feishu.Ws;

namespace FeishuSdk.Tests;

public class WsFrameTests
{
    [Fact]
    public void ToBytes_Should_Match_Canonical_Proto_Encoding()
    {
        // Frame{SeqID=1, LogID=2, service=3, method=0}：
        // field1 varint: 08 01 / field2: 10 02 / field3: 18 03 / field4: 20 00
        var frame = new WsFrame { SeqId = 1, LogId = 2, Service = 3, Method = 0 };

        Assert.Equal(new byte[] { 0x08, 0x01, 0x10, 0x02, 0x18, 0x03, 0x20, 0x00 }, frame.ToBytes());
    }

    [Fact]
    public void Frame_Should_Roundtrip_With_Headers_And_Payload()
    {
        var frame = new WsFrame
        {
            SeqId = 42,
            LogId = 7,
            Service = 110,
            Method = 1,
            Payload = "hello 飞书"u8.ToArray(),
            PayloadType = "json",
            PayloadEncoding = "raw",
            LogIdNew = "log-new-1",
        };
        frame.Headers.Add(("type", "event"));
        frame.Headers.Add(("message_id", "m-1"));

        var bytes = frame.ToBytes();
        var parsed = WsFrame.Parse(bytes);

        Assert.Equal(frame.SeqId, parsed.SeqId);
        Assert.Equal(frame.LogId, parsed.LogId);
        Assert.Equal(frame.Service, parsed.Service);
        Assert.Equal(frame.Method, parsed.Method);
        Assert.Equal("event", parsed.GetHeader("type"));
        Assert.Equal("m-1", parsed.GetHeader("message_id"));
        Assert.Equal("json", parsed.PayloadType);
        Assert.Equal("raw", parsed.PayloadEncoding);
        Assert.Equal("log-new-1", parsed.LogIdNew);
        Assert.Equal("hello 飞书", Encoding.UTF8.GetString(parsed.Payload!));
    }

    [Fact]
    public void Parse_Should_Skip_Unknown_Fields()
    {
        // 手工构造：合法字段 1 个 + 未知字段（field 99, varint）+ 尾随合法字段
        var buffer = new MemoryStream();
        // field 1 varint 5
        buffer.Write(new byte[] { 0x08, 0x05 });
        // field 99 wiretype 0 → tag = 99<<3|0 = 792 → varint 编码 0x98 0x06
        buffer.Write(new byte[] { 0x98, 0x06, 0x2A });
        // field 4 varint 1 → 0x20 0x01
        buffer.Write(new byte[] { 0x20, 0x01 });

        var frame = WsFrame.Parse(buffer.ToArray());

        Assert.Equal(5UL, frame.SeqId);
        Assert.Equal(1, frame.Method);
    }

    [Fact]
    public void SetHeader_Should_Update_Existing()
    {
        var frame = new WsFrame();
        frame.SetHeader("biz_rt", "1");
        frame.SetHeader("biz_rt", "2");

        Assert.Equal("2", frame.GetHeader("biz_rt"));
        Assert.Single(frame.Headers);
    }
}

public class FrameReassemblerTests
{
    [Fact]
    public void Combine_Should_Wait_For_All_Parts_In_Any_Order()
    {
        var reassembler = new FrameReassembler(TimeSpan.FromSeconds(5));

        Assert.Null(reassembler.Combine("m1", 2, 0, "a:"u8.ToArray()));
        var combined = reassembler.Combine("m1", 2, 1, "b"u8.ToArray());

        Assert.NotNull(combined);
        Assert.Equal("a:b", Encoding.UTF8.GetString(combined));
    }

    [Fact]
    public async Task Expired_Parts_Should_Not_Leak()
    {
        var reassembler = new FrameReassembler(TimeSpan.FromMilliseconds(30));
        reassembler.Combine("m-old", 2, 0, "x"u8.ToArray());

        await Task.Delay(80);
        // 新消息触发清扫；旧 key 到期后重新等第一片
        Assert.Null(reassembler.Combine("m-old", 2, 1, "y"u8.ToArray()));
    }
}

public class MemoryFeishuCacheTests
{
    [Fact]
    public async Task Set_With_Ttl_Should_Expire()
    {
        var cache = new MemoryFeishuCache();

        await cache.SetAsync("k", "v", TimeSpan.FromMilliseconds(60));
        Assert.Equal("v", await cache.GetAsync("k"));

        await Task.Delay(150);
        Assert.Null(await cache.GetAsync("k"));
    }

    [Fact]
    public async Task Remove_Should_Delete()
    {
        var cache = new MemoryFeishuCache();
        await cache.SetAsync("k", "v", TimeSpan.Zero);
        await cache.RemoveAsync("k");

        Assert.Null(await cache.GetAsync("k"));
    }
}
