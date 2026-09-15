using System.Text.Json.Serialization;

namespace Feishu.Card;

/// <summary>
/// 卡片搭建 DSL（对齐 Go card/model.go 的 MessageCard 家族）。
/// C# 形态：可空属性 + 对象初始化器（替代 Go Builder），元素以只读 Tag 属性自带 "tag" 字段，
/// 序列化输出与 Go 版一致（null 字段省略、snake_case）。
/// </summary>
public sealed class MessageCard
{
    [JsonPropertyName("config")]
    public CardConfig? Config { get; set; }

    [JsonPropertyName("header")]
    public CardHeader? Header { get; set; }

    /// <summary>元素列表（各 *Element 类型，可混排）。</summary>
    [JsonPropertyName("elements")]
    public List<object>? Elements { get; set; }

    [JsonPropertyName("i18n_elements")]
    public Dictionary<string, List<object>>? I18nElements { get; set; }

    [JsonPropertyName("card_link")]
    public CardUrl? CardLink { get; set; }

    public string ToJsonString(IFeishuSerializer? serializer = null) =>
        (serializer ?? SystemTextJsonFeishuSerializer.Instance).Serialize(this);
}

/// <summary>卡片全局配置。</summary>
public sealed class CardConfig
{
    [JsonPropertyName("enable_forward")]
    public bool? EnableForward { get; set; }

    [JsonPropertyName("update_multi")]
    public bool? UpdateMulti { get; set; }

    [JsonPropertyName("wide_screen_mode")]
    public bool? WideScreenMode { get; set; }
}

/// <summary>卡片头（Template 取 <see cref="CardTemplates"/> 常量）。</summary>
public sealed class CardHeader
{
    [JsonPropertyName("template")]
    public string? Template { get; set; }

    [JsonPropertyName("title")]
    public CardPlainText? Title { get; set; }
}

public static class CardTemplates
{
    public const string Blue = "blue";
    public const string Wathet = "wathet";
    public const string Turquoise = "turquoise";
    public const string Green = "green";
    public const string Yellow = "yellow";
    public const string Orange = "orange";
    public const string Red = "red";
    public const string Carmine = "carmine";
    public const string Violet = "violet";
    public const string Purple = "purple";
    public const string Indigo = "indigo";
    public const string Grey = "grey";
}

/// <summary>纯文本（tag=plain_text）。</summary>
public sealed class CardPlainText
{
    [JsonPropertyName("tag")]
    public string Tag => "plain_text";

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("lines")]
    public int? Lines { get; set; }
}

/// <summary>Lark Markdown（tag=lark_md）。</summary>
public sealed class CardLarkMd
{
    [JsonPropertyName("tag")]
    public string Tag => "lark_md";

    [JsonPropertyName("content")]
    public string? Content { get; set; }
}

/// <summary>分割线（tag=hr）。</summary>
public sealed class CardHr
{
    [JsonPropertyName("tag")]
    public string Tag => "hr";
}

/// <summary>Markdown 块（tag=markdown）。</summary>
public sealed class CardMarkdown
{
    [JsonPropertyName("tag")]
    public string Tag => "markdown";

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("href")]
    public Dictionary<string, CardUrl>? Href { get; set; }
}

/// <summary>多列布局（tag=div）：Text 用 CardPlainText/CardLarkMd。</summary>
public sealed class CardDiv
{
    [JsonPropertyName("tag")]
    public string Tag => "div";

    [JsonPropertyName("text")]
    public object? Text { get; set; }

    [JsonPropertyName("fields")]
    public List<CardField>? Fields { get; set; }

    [JsonPropertyName("extra")]
    public object? Extra { get; set; }
}

public sealed class CardField
{
    [JsonPropertyName("is_short")]
    public bool? IsShort { get; set; }

    [JsonPropertyName("text")]
    public object? Text { get; set; }
}

/// <summary>备注块（tag=note）。</summary>
public sealed class CardNote
{
    [JsonPropertyName("tag")]
    public string Tag => "note";

    [JsonPropertyName("elements")]
    public List<object>? Elements { get; set; }
}

/// <summary>跳转链接。</summary>
public sealed class CardUrl
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("android_url")]
    public string? AndroidUrl { get; set; }

    [JsonPropertyName("ios_url")]
    public string? IosUrl { get; set; }

    [JsonPropertyName("pc_url")]
    public string? PcUrl { get; set; }
}

public static class CardButtonTypes
{
    public const string Default = "default";
    public const string Primary = "primary";
    public const string Danger = "danger";
}

/// <summary>按钮（tag=button）。</summary>
public sealed class CardButton
{
    [JsonPropertyName("tag")]
    public string Tag => "button";

    [JsonPropertyName("text")]
    public object? Text { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("multi_url")]
    public CardUrl? MultiUrl { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>回传值：卡片回调时出现在 action.value。</summary>
    [JsonPropertyName("value")]
    public Dictionary<string, object?>? Value { get; set; }

    [JsonPropertyName("confirm")]
    public CardConfirm? Confirm { get; set; }
}

public sealed class CardConfirm
{
    [JsonPropertyName("title")]
    public object? Title { get; set; }

    [JsonPropertyName("text")]
    public object? Text { get; set; }
}

public static class CardImageModes
{
    public const string FitHorizontal = "fit_horizontal";
    public const string CropCenter = "crop_center";
}

/// <summary>图片（tag=img）。</summary>
public sealed class CardImage
{
    [JsonPropertyName("tag")]
    public string Tag => "img";

    [JsonPropertyName("alt")]
    public CardPlainText? Alt { get; set; }

    [JsonPropertyName("img_key")]
    public string? ImgKey { get; set; }

    [JsonPropertyName("custom_width")]
    public int? CustomWidth { get; set; }

    [JsonPropertyName("compact_width")]
    public bool? CompactWidth { get; set; }

    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    [JsonPropertyName("preview")]
    public bool? Preview { get; set; }
}

public sealed class CardSelectOption
{
    [JsonPropertyName("text")]
    public CardPlainText? Text { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("multi_url")]
    public CardUrl? MultiUrl { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

/// <summary>更多操作（tag=overflow）。</summary>
public sealed class CardOverflow
{
    [JsonPropertyName("tag")]
    public string Tag => "overflow";

    [JsonPropertyName("options")]
    public List<CardSelectOption>? Options { get; set; }

    [JsonPropertyName("value")]
    public Dictionary<string, object?>? Value { get; set; }

    [JsonPropertyName("confirm")]
    public CardConfirm? Confirm { get; set; }
}

/// <summary>静态下拉选框（tag=select_static）。</summary>
public sealed class CardSelectStatic
{
    [JsonPropertyName("tag")]
    public string Tag => "select_static";

    [JsonPropertyName("placeholder")]
    public CardPlainText? Placeholder { get; set; }

    [JsonPropertyName("initial_option")]
    public string? InitialOption { get; set; }

    [JsonPropertyName("options")]
    public List<CardSelectOption>? Options { get; set; }

    [JsonPropertyName("value")]
    public Dictionary<string, object?>? Value { get; set; }

    [JsonPropertyName("confirm")]
    public CardConfirm? Confirm { get; set; }
}

/// <summary>人员选框（tag=select_person）。</summary>
public sealed class CardSelectPerson
{
    [JsonPropertyName("tag")]
    public string Tag => "select_person";

    [JsonPropertyName("placeholder")]
    public CardPlainText? Placeholder { get; set; }

    [JsonPropertyName("initial_option")]
    public string? InitialOption { get; set; }

    [JsonPropertyName("options")]
    public List<CardSelectOption>? Options { get; set; }

    [JsonPropertyName("value")]
    public Dictionary<string, object?>? Value { get; set; }

    [JsonPropertyName("confirm")]
    public CardConfirm? Confirm { get; set; }
}

public static class CardPickerKinds
{
    public const string Date = "date_picker";
    public const string Time = "picker_time";
    public const string Datetime = "picker_datetime";
}

/// <summary>日期/时间选择器（tag=date_picker / picker_time / picker_datetime，由 Kind 决定）。</summary>
public sealed class CardPicker
{
    // 序列化时按 Kind 输出对应 tag
    [JsonPropertyName("tag")]
    public string Tag => Kind ?? CardPickerKinds.Date;

    [JsonIgnore]
    public string? Kind { get; set; }

    [JsonPropertyName("initial_date")]
    public string? InitialDate { get; set; }

    [JsonPropertyName("initial_time")]
    public string? InitialTime { get; set; }

    [JsonPropertyName("initial_datetime")]
    public string? InitialDatetime { get; set; }

    [JsonPropertyName("placeholder")]
    public CardPlainText? Placeholder { get; set; }

    [JsonPropertyName("value")]
    public Dictionary<string, object?>? Value { get; set; }

    [JsonPropertyName("confirm")]
    public CardConfirm? Confirm { get; set; }
}

public static class CardActionLayouts
{
    public const string Bisected = "bisected";
    public const string Trisection = "trisection";
    public const string Flow = "flow";
}

/// <summary>交互元素布局块（tag=action）。</summary>
public sealed class CardActionBlock
{
    [JsonPropertyName("tag")]
    public string Tag => "action";

    [JsonPropertyName("actions")]
    public List<object>? Actions { get; set; }

    [JsonPropertyName("layout")]
    public string? Layout { get; set; }
}
