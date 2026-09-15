using System.Text.Json;
using System.Text.Json.Serialization;

namespace Feishu;

/// <summary>
/// 通用响应：data 以原始 JSON 暴露（生成器批量扩面用；
/// 需要强类型时按 PARITY 模板手写或扩展生成器模型）。
/// </summary>
public sealed class FeishuDataResponse : FeishuResponse
{
    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }

    /// <summary>把 data 反序列化为指定模型。</summary>
    public T? DataAs<T>() => Data is { ValueKind: JsonValueKind.Object or JsonValueKind.Array }
        ? JsonSerializer.Deserialize<T>(Data.Value.GetRawText(), SystemTextJsonFeishuSerializer.Instance.Options)
        : default;

    /// <summary>分页字段：data.page_token。</summary>
    public string? PageToken => Data is { ValueKind: JsonValueKind.Object } && Data.Value.TryGetProperty("page_token", out var t) && t.ValueKind == JsonValueKind.String
        ? t.GetString()
        : null;

    /// <summary>分页字段：data.has_more。</summary>
    public bool HasMore => Data is { ValueKind: JsonValueKind.Object } && Data.Value.TryGetProperty("has_more", out var h) && h.ValueKind == JsonValueKind.True;
}
