using System.Text.Json;

namespace Feishu.Channel;

/// <summary>
/// FeishuChannel 分部：媒体上传与下载。
/// 图片走 im/v1/images（image_type=message），文件/音视频走 im/v1/files（file_type=stream）；
/// URL 来源先过 <see cref="SsrfGuard"/> 校验，音频(OGG)/视频(MP4)自动探测时长。
/// </summary>
public sealed partial class FeishuChannel
{
    /// <summary>上传本地图片，返回 image_key（SendAsync 里 ImagePath 也会自动走到这里）。</summary>
    private async Task<string> UploadImageAsync(string path, CancellationToken ct)
    {
        var multipart = new MultipartRequestBody()
            .AddField("image_type", "message")
            .AddFileFromPath("image", path);
        var resp = await _client.DoAsync(new ApiRequest
        {
            Method = HttpMethod.Post,
            Path = "/open-apis/im/v1/images",
            Body = multipart,
            SupportedTokenTypes = [AccessTokenType.Tenant],
        }, null, ct);
        var data = JsonSerializer.Deserialize<UploadResp>(resp.RawBody)!;
        if (data.Code != 0 || data.Data?.ImageKey == null)
            throw new FeishuCodeException(data.Code, data.Msg ?? "");
        return data.Data.ImageKey;
    }

    /// <summary>
    /// 从 URL / 本地路径 / 字节上传媒体（对齐 Go uploader.UploadMedia 的入口形态）：
    /// URL 来源先过 <see cref="SsrfGuard"/>；音频(ogg)/视频(mp4)自动探测时长。
    /// 返回 file/image key（image 走 im/v1/images，其余走 im/v1/files）。
    /// </summary>
    /// <returns>(file_key 或 image_key, 探测到的时长毫秒；无法探测时为 null)。</returns>
    public async Task<(string FileKey, int? DurationMs)> UploadMediaAsync(ChannelUploadInput input, CancellationToken ct = default)
    {
        byte[] data;
        string fileName;
        if (input.SourceUrl != null)
        {
            await SsrfGuard.AssertPublicUrlAsync(input.SourceUrl, allowlist: null, ct);
            using var http = new HttpClient();
            data = await http.GetByteArrayAsync(input.SourceUrl, ct);
            fileName = input.FileName ?? new Uri(input.SourceUrl).GetComponents(UriComponents.Path, UriFormat.UriEscaped).Split('/')[^1];
        }
        else if (input.SourcePath != null)
        {
            data = await File.ReadAllBytesAsync(input.SourcePath, ct);
            fileName = input.FileName ?? Path.GetFileName(input.SourcePath);
        }
        else if (input.SourceBytes != null)
        {
            data = input.SourceBytes;
            fileName = input.FileName ?? "upload.bin";
        }
        else
        {
            throw new FeishuException("UploadMedia requires SourceUrl / SourcePath / SourceBytes");
        }

        // 时长探测：OGG 头尾页 / MP4 mvhd
        int? durationMs = null;
        if (input.Kind is "audio" or "video" or "media")
        {
            try
            {
                using var probe = new MemoryStream(data);
                durationMs = LooksLikeOgg(data) ? MediaDuration.ParseOpusDuration(probe) : MediaDuration.ParseMp4Duration(probe);
            }
            catch (FeishuChannelException)
            {
                durationMs = null; // 无法解析时不上报时长（与 Go 探测失败兜底一致）
            }
        }

        var isImage = input.Kind == "image";
        var key = await UploadBytesAsync(isImage, fileName, data, ct);
        return (key, durationMs);
    }

    /// <summary>"OggS" 魔数嗅探（区分 OGG 音频与 MP4 视频以选择时长探测路径）。</summary>
    private static bool LooksLikeOgg(byte[] data) =>
        data.Length >= 4 && data[0] == 0x4f && data[1] == 0x67 && data[2] == 0x67 && data[3] == 0x53;

    /// <summary>字节级上传：image 走 im/v1/images，其余走 im/v1/files。</summary>
    private async Task<string> UploadBytesAsync(bool isImage, string fileName, byte[] data, CancellationToken ct)
    {
        var multipart = new MultipartRequestBody()
            .AddField(isImage ? "image_type" : "file_type", isImage ? "message" : "stream")
            .AddFile(isImage ? "image" : "file", fileName, new MemoryStream(data));
        var resp = await _client.DoAsync(new ApiRequest
        {
            Method = HttpMethod.Post,
            Path = isImage ? "/open-apis/im/v1/images" : "/open-apis/im/v1/files",
            Body = multipart,
            SupportedTokenTypes = [AccessTokenType.Tenant],
        }, null, ct);
        var parsed = JsonSerializer.Deserialize<UploadResp>(resp.RawBody)!;
        if (parsed.Code != 0)
            throw new FeishuCodeException(parsed.Code, parsed.Msg ?? "");
        var key = isImage ? parsed.Data?.ImageKey : parsed.Data?.FileKey;
        if (key == null) throw new FeishuCodeException(parsed.Code, "upload returned empty key");
        return key;
    }

    /// <summary>上传本地文件，返回 file_key（SendAsync 里 FilePath 也会自动走到这里）。</summary>
    private async Task<string> UploadFileAsync(string path, CancellationToken ct)
    {
        var multipart = new MultipartRequestBody()
            .AddField("file_type", "stream")
            .AddFileFromPath("file", path);
        var resp = await _client.DoAsync(new ApiRequest
        {
            Method = HttpMethod.Post,
            Path = "/open-apis/im/v1/files",
            Body = multipart,
            SupportedTokenTypes = [AccessTokenType.Tenant],
        }, null, ct);
        var data = JsonSerializer.Deserialize<UploadResp>(resp.RawBody)!;
        if (data.Code != 0 || data.Data?.FileKey == null)
            throw new FeishuCodeException(data.Code, data.Msg ?? "");
        return data.Data.FileKey;
    }

    /// <summary>im/v1/images 与 im/v1/files 上传响应壳。</summary>
    private sealed class UploadResp
    {
        [System.Text.Json.Serialization.JsonPropertyName("code")]
        public int Code { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("msg")]
        public string? Msg { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("data")]
        public UploadData? Data { get; set; }
    }

    /// <summary>上传响应 data 载荷（图片与文件端点共用，字段按需存在）。</summary>
    private sealed class UploadData
    {
        [System.Text.Json.Serialization.JsonPropertyName("image_key")]
        public string? ImageKey { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("file_key")]
        public string? FileKey { get; set; }
    }

    // ==================== 媒体下载 ====================

    /// <summary>下载媒体（mediaType: image / file / audio / video / media）。</summary>
    /// <returns>媒体原始字节（走文件下载通道，不做 JSON 解析）。</returns>
    public async Task<byte[]> DownloadFileAsync(string fileKey, string mediaType, CancellationToken ct = default)
    {
        if (fileKey.Length == 0) throw new FeishuException("fileKey cannot be empty");

        string path;
        if (mediaType == "image") path = "/open-apis/im/v1/images/" + Uri.EscapeDataString(fileKey);
        else if (mediaType is "file" or "audio" or "video" or "media") path = "/open-apis/im/v1/files/" + Uri.EscapeDataString(fileKey);
        else throw new FeishuException($"unsupported mediaType: {mediaType}");

        var resp = await _client.GetAsync(path, null, AccessTokenType.Tenant,
            new RequestOptions().WithFileDownload(), ct);
        if (resp.StatusCode != 200)
            throw new FeishuException($"download {mediaType} failed: status {resp.StatusCode}");
        return resp.RawBody;
    }
}
