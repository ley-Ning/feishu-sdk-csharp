using Feishu;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace FeishuSdk.Tests;

using Feishu.Channel;
using Feishu.Scene;
using Feishu.Services.Ext;

/// <summary>SSRF 防护（对齐 Go ssrf_guard.go 的 CIDR 表与 IPv6 规则）。</summary>
public class SsrfGuardTests
{
    [Theory]
    [InlineData("http://127.0.0.1/x")]           // 环回
    [InlineData("http://10.1.2.3/x")]            // RFC1918
    [InlineData("http://192.168.1.1/x")]         // RFC1918
    [InlineData("http://172.16.0.9/x")]          // RFC1918
    [InlineData("http://169.254.169.254/x")]     // 云元数据
    [InlineData("http://100.64.0.1/x")]          // CGNAT
    [InlineData("http://0.0.0.0/x")]             // 本网络
    [InlineData("http://224.0.0.1/x")]           // 组播
    [InlineData("http://240.0.0.1/x")]           // 保留
    [InlineData("http://[::1]/x")]               // IPv6 环回
    [InlineData("http://[::ffff:127.0.0.1]/x")]  // IPv4 映射环回
    [InlineData("http://[fe80::1]/x")]           // 链路本地
    [InlineData("http://[fc00::1]/x")]           // fc00::/7
    [InlineData("http://[fd12::1]/x")]           // fd00::/8
    [InlineData("http://[ff02::1]/x")]           // IPv6 组播
    public async Task Private_Or_Reserved_Targets_Should_Be_Blocked(string url)
    {
        var ex = await Assert.ThrowsAsync<FeishuChannelException>(() => SsrfGuard.AssertPublicUrlAsync(url));
        Assert.Equal(ChannelErrorCode.SsrfBlocked, ex.Code);
    }

    [Theory]
    [InlineData("https://1.1.1.1/x")]
    [InlineData("https://8.8.8.8/x")]
    [InlineData("http://[2606:4700::1111]/x")] // 公网 IPv6
    public async Task Public_Ip_Literals_Should_Pass(string url) =>
        await SsrfGuard.AssertPublicUrlAsync(url);

    [Fact]
    public async Task Allowlist_Should_Bypass_Host()
    {
        await SsrfGuard.AssertPublicUrlAsync("http://127.0.0.1/x", allowlist: ["127.0.0.1"]);
    }

    [Theory]
    [InlineData("ftp://1.1.1.1/x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not-a-url")]
    public async Task Non_Http_Scheme_Or_Invalid_Should_Be_Blocked(string url)
    {
        var ex = await Assert.ThrowsAsync<FeishuChannelException>(() => SsrfGuard.AssertPublicUrlAsync(url));
        Assert.Equal(ChannelErrorCode.SsrfBlocked, ex.Code);
    }
}

/// <summary>音视频时长解析（OGG granule / MP4 mvhd，对齐 Go duration_*.go）。</summary>
public class MediaDurationTests
{
    [Fact]
    public void Opus_Duration_Should_Be_Granule_Over_48()
    {
        // 构造 27 字节 OggS 页：4B 魔数 + 2B 版本保留 + 8B granule(LE)
        var page = new byte[27];
        "OggS"u8.CopyTo(page);
        var granule = 48_000L * 62; // 62 秒
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6, 8), granule);

        using var ogg = new MemoryStream(page);
        Assert.Equal(62_000, MediaDuration.ParseOpusDuration(ogg));
    }

    [Fact]
    public void Opus_Tail_Scan_Should_Find_Last_Page()
    {
        var first = MakePage(48_000L * 10);
        var second = MakePage(48_000L * 30);
        var data = first.Concat(new byte[64]).Concat(second).ToArray(); // 尾部页必须命中

        using var ms = new MemoryStream(data);
        Assert.Equal(30_000, MediaDuration.ParseOpusDuration(ms));

        static byte[] MakePage(long granule)
        {
            var page = new byte[27];
            "OggS"u8.CopyTo(page);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6, 8), granule);
            return page;
        }
    }

    [Fact]
    public void Mp4_Duration_v0_Should_Divide_Timescale()
    {
        // ftyp + moov{ mvhd(v0) }：timescale=1000, duration=2500 → 2500ms
        var mp4 = BuildMp4(version: 0, timescale: 1000, durationUnits: 2500);
        using var ms = new MemoryStream(mp4);
        Assert.Equal(2500, MediaDuration.ParseMp4Duration(ms));
    }

    [Fact]
    public void Mp4_Duration_v1_Should_Read_64Bit()
    {
        var mp4 = BuildMp4(version: 1, timescale: 48000, durationUnits: 48000 * 5);
        using var ms = new MemoryStream(mp4);
        Assert.Equal(5000, MediaDuration.ParseMp4Duration(ms));
    }

    [Fact]
    public void Mp4_Missing_Moov_Should_Throw()
    {
        var ftyp = Box("ftyp", "isom"u8.ToArray());
        using var ms = new MemoryStream(ftyp);
        Assert.Throws<FeishuChannelException>(() => MediaDuration.ParseMp4Duration(ms));
    }

    private static byte[] BuildMp4(int version, uint timescale, ulong durationUnits)
    {
        var mvhd = new List<byte> { (byte)version, 0, 0, 0 }; // version + flags
        if (version == 1)
        {
            mvhd.AddRange(new byte[16]); // creation + modification
            mvhd.AddRange(Be32(timescale));
            mvhd.AddRange(Be64(durationUnits));
        }
        else
        {
            mvhd.AddRange(new byte[8]); // creation + modification
            mvhd.AddRange(Be32(timescale));
            mvhd.AddRange(Be32((uint)durationUnits));
        }
        var ftyp = Box("ftyp", "isom"u8.ToArray());
        var moov = Box("moov", Box("mvhd", mvhd.ToArray()));
        return ftyp.Concat(moov).ToArray();
    }

    private static byte[] Box(string type, byte[] payload)
    {
        var box = new byte[8 + payload.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(0, 4), (uint)(8 + payload.Length));
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        payload.CopyTo(box, 8);
        return box;
    }

    private static byte[] Be32(uint v)
    {
        var b = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }

    private static byte[] Be64(ulong v)
    {
        var b = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(b, v);
        return b;
    }
}

/// <summary>ext 扩展服务。</summary>
public class ExtServiceTests
{
    [Fact]
    public async Task DriveExplorer_CreateFile_Should_Post()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"tenant_access_token":"t"}"""));
            return Task.FromResult(FakeHandler.Json(200, """{"code":0,"data":{"file_token":"ft_1","url":"https://x","type":"doc"}}"""));
        });

        var resp = await client.Ext.DriveExplorer.CreateFileAsync("fld_9", new { title = "新建", type = "doc" });

        Assert.Equal("ft_1", resp.Data?.FileToken);
        var req = handler.Requests[^1];
        Assert.Equal("POST", req.Method.Method);
        Assert.Equal("/open-apis/drive/explorer/v2/file/fld_9", req.PathAndQuery);
        Assert.Contains("新建", req.BodyText);
    }

    [Fact]
    public async Task Ext_Authen_Endpoints_Should_Match_Go_Paths()
    {
        var (client, handler) = FeishuTestHarness.CreateClient(req =>
        {
            if (req.PathAndQuery.StartsWith("/open-apis/auth/v3/app_access_token"))
                return Task.FromResult(FakeHandler.Json(200, """{"code":0,"expire":7200,"app_access_token":"t-app"}"""));
            return Task.FromResult(FakeHandler.Json(200, """{"code":0,"access_token":"u","name":"N"}"""));
        });

        await client.Ext.Authen.AuthenAccessTokenAsync("uac");
        await client.Ext.Authen.RefreshAuthenAccessTokenAsync("rt");
        await client.Ext.Authen.AuthenUserInfoAsync(new RequestOptions().WithUserAccessToken("u-t"));

        Assert.Equal("/open-apis/authen/v1/access_token", handler.Requests[^3].PathAndQuery);
        Assert.Contains("user_access_code", handler.Requests[^3].BodyText);
        Assert.Equal("/open-apis/authen/v1/refresh_access_token", handler.Requests[^2].PathAndQuery);
        Assert.Equal("/open-apis/authen/v1/user_info", handler.Requests[^1].PathAndQuery);
        Assert.Equal("Bearer u-t", handler.Requests[^1].Headers["Authorization"]);
    }
}

/// <summary>一键建应用（scene/registration：QR URL 组装 / addons 编码 / 轮询状态机）。</summary>
public class AppRegistrationTests
{
    [Fact]
    public void QrUrl_Should_Carry_Sdk_And_Preset_Params()
    {
        var url = AppRegistration.BuildQrCodeUrl(
            "https://accounts.feishu.cn/oauth/device?code=abc&from=web",
            new RegisterAppOptions
            {
                OnQrCode = _ => { },
                Source = "demo",
                AppPreset = new RegistrationAppPreset { Name = "我的{user}机器人", Avatar = ["https://img/1.png"] },
                CreateOnly = true,
                AppId = "cli_pre",
            });

        Assert.StartsWith("https://accounts.feishu.cn/oauth/device?", url);
        Assert.Contains("code=abc", url);
        Assert.Contains("from=sdk", url);          // 覆盖原 from=web
        Assert.Contains("tp=sdk", url);
        Assert.Contains("source=csharp-sdk%2Fdemo", url);
        Assert.Contains("avatar=https%3A%2F%2Fimg%2F1.png", url);
        Assert.Contains("name=", url);
        Assert.Contains("createOnly=true", url);
        Assert.Contains("clientID=cli_pre", url);
    }

    [Fact]
    public void Addons_Encoding_Should_Be_Json_Gzip_Base64Url()
    {
        var addons = new RegistrationAppAddons
        {
            Preset = false,
            Scopes = new RegistrationAddonsScopes { Tenant = ["im:message:send_as_bot"] },
            Events = new RegistrationAddonsEvents { Items = new RegistrationAddonsEventItems { User = ["calendar.calendar.event.changed_v4"] } },
            Callbacks = new RegistrationAddonsCallbacks { Items = ["card.action.trigger"] },
        };

        var encoded = AppRegistration.EncodeAddons(addons);

        // base64url 字符集
        Assert.Matches("^[A-Za-z0-9_-]+$", encoded);

        // 解码：base64url → gunzip → JSON
        var base64 = encoded.Replace('-', '+').Replace('_', '/').PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
        using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(base64)), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var json = reader.ReadToEnd();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("preset").GetBoolean()); // false 序列化为 false（最小模板）
        Assert.Equal("im:message:send_as_bot", root.GetProperty("scopes").GetProperty("tenant")[0].GetString());
        Assert.Equal("calendar.calendar.event.changed_v4", root.GetProperty("events").GetProperty("items").GetProperty("user")[0].GetString());
        Assert.Equal("card.action.trigger", root.GetProperty("callbacks").GetProperty("items")[0].GetString());
    }

    [Fact]
    public void Addons_Empty_Without_Minimal_Preset_Should_Be_Rejected()
    {
        var ex = Assert.Throws<RegisterAppException>(() =>
            AppRegistration.EncodeAddons(new RegistrationAppAddons()));
        Assert.Contains("unless Preset is false", ex.Message);
    }

    [Fact]
    public void Addons_Empty_String_Item_Should_Be_Rejected()
    {
        var ex = Assert.Throws<RegisterAppException>(() =>
            AppRegistration.EncodeAddons(new RegistrationAppAddons
            {
                Scopes = new RegistrationAddonsScopes { Tenant = ["ok", ""] },
            }));
        Assert.Contains("must be a non-empty string", ex.Message);
    }

    [Fact]
    public async Task RegisterApp_Happy_Path_Should_Poll_Then_Return_Credentials()
    {
        var poll = 0;
        RegistrationQrCodeInfo? qr = null;
        var statuses = new List<RegistrationStatusChange>();
        var handler = new FakeHandler(req =>
        {
            var body = req.BodyText;
            if (body.Contains("action=begin"))
                return Task.FromResult(FakeHandler.Json(200,
                    """{"device_code":"dev1","verification_uri_complete":"https://accounts.feishu.cn/oauth/device?code=c1&from=web","interval":1,"expire_in":600}"""));
            poll++;
            return Task.FromResult(poll switch
            {
                1 => FakeHandler.Json(200, """{"error":"authorization_pending"}"""),
                2 => FakeHandler.Json(200, """{"error":"slow_down"}"""),
                _ => FakeHandler.Json(200, """{"client_id":"cli_new","client_secret":"sec_new","user_info":{"open_id":"ou_1","tenant_brand":"feishu"}}"""),
            });
        });

        var result = await AppRegistration.RegisterAppAsync(new RegisterAppOptions
        {
            OnQrCode = info => qr = info,
            OnStatusChange = s => statuses.Add(s),
            WaitAsync = (_, _) => Task.CompletedTask,
            HttpMessageHandlerFactory = () => handler,
        });

        Assert.Equal("cli_new", result.ClientId);
        Assert.Equal("sec_new", result.ClientSecret);
        Assert.Equal("ou_1", result.UserInfo?.OpenId);
        Assert.NotNull(qr);
        Assert.Contains("from=sdk", qr!.Url);
        Assert.Contains(RegistrationStatus.Polling, statuses.Select(s => s.Status));
        Assert.Contains(RegistrationStatus.SlowDown, statuses.Select(s => s.Status));
        // begin 表单形态
        var begin = handler.Requests.First(r => r.BodyText.Contains("action=begin"));
        Assert.Contains("archetype=PersonalAgent", begin.BodyText);
        Assert.Contains("request_user_info=open_id", begin.BodyText);
    }

    [Fact]
    public async Task RegisterApp_Lark_Brand_Should_Switch_Domain()
    {
        var poll = 0;
        var statuses = new List<RegistrationStatusChange>();
        var handler = new FakeHandler(req =>
        {
            if (req.BodyText.Contains("action=begin"))
                return Task.FromResult(FakeHandler.Json(200,
                    """{"device_code":"d","verification_uri_complete":"https://accounts.feishu.cn/oauth/device?code=c","interval":1,"expire_in":600}"""));
            poll++;
            if (poll == 1)
                return Task.FromResult(FakeHandler.Json(200, """{"user_info":{"open_id":"ou","tenant_brand":"lark"}}"""));
            return Task.FromResult(FakeHandler.Json(200, """{"client_id":"c","client_secret":"s"}"""));
        });

        var result = await AppRegistration.RegisterAppAsync(new RegisterAppOptions
        {
            OnQrCode = _ => { },
            OnStatusChange = s => statuses.Add(s),
            WaitAsync = (_, _) => Task.CompletedTask,
            HttpMessageHandlerFactory = () => handler,
        });

        Assert.Equal("c", result.ClientId);
        Assert.Contains(RegistrationStatus.DomainSwitched, statuses.Select(s => s.Status));
        // 第二次 poll 打到了 lark 域名
        Assert.Equal(3, handler.Requests.Count);
        Assert.StartsWith("https://accounts.feishu.cn", handler.Requests[0].Url);
        Assert.StartsWith("https://accounts.feishu.cn", handler.Requests[1].Url);
        Assert.StartsWith("https://accounts.larksuite.com", handler.Requests[2].Url);
    }

    [Fact]
    public async Task RegisterApp_Access_Denied_Should_Throw_Typed()
    {
        var handler = new FakeHandler(req =>
        {
            if (req.BodyText.Contains("action=begin"))
                return Task.FromResult(FakeHandler.Json(200,
                    """{"device_code":"d","verification_uri_complete":"https://accounts.feishu.cn/oauth/device?code=c","interval":1,"expire_in":600}"""));
            return Task.FromResult(FakeHandler.Json(200, """{"error":"access_denied","error_description":"user denied"}"""));
        });

        var ex = await Assert.ThrowsAsync<RegisterAppException>(() => AppRegistration.RegisterAppAsync(new RegisterAppOptions
        {
            OnQrCode = _ => { },
            WaitAsync = (_, _) => Task.CompletedTask,
            HttpMessageHandlerFactory = () => handler,
        }));

        Assert.Equal("access_denied", ex.ErrorCode);
    }

    [Fact]
    public async Task RegisterApp_Missing_OnQrCode_Should_Throw()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            AppRegistration.RegisterAppAsync(new RegisterAppOptions { OnQrCode = null! }));
    }
}
