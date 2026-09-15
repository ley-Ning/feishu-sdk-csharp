using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Feishu;

/// <summary>JSON 序列化抽象，默认基于 System.Text.Json（snake_case 属性映射）。</summary>
public interface IFeishuSerializer
{
    string Serialize<T>(T value);

    byte[] SerializeToUtf8Bytes<T>(T value);

    T Deserialize<T>(string json);

    T Deserialize<T>(ReadOnlySpan<byte> utf8Json);
}

public sealed class SystemTextJsonFeishuSerializer : IFeishuSerializer
{
    public static readonly SystemTextJsonFeishuSerializer Instance = new();

    public JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        // 飞书 API 一律 snake_case
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        // 与 Go 版 omitempty 语义对齐：null 不下发
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 中文原样输出，避免 \uXXXX 膨胀
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public byte[] SerializeToUtf8Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;

    public T Deserialize<T>(ReadOnlySpan<byte> utf8Json) => JsonSerializer.Deserialize<T>(utf8Json, Options)!;
}

public static class FeishuSerializerExtensions
{
    /// <summary>把对象序列化为适合打进日志的紧凑 JSON。</summary>
    public static string Prettify<T>(this IFeishuSerializer serializer, T value)
    {
        try
        {
            return serializer.Serialize(value);
        }
        catch (Exception)
        {
            return value?.ToString() ?? "<null>";
        }
    }

    public static byte[] ToUtf8Bytes(this string s) => Encoding.UTF8.GetBytes(s);
}
