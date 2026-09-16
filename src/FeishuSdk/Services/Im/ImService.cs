namespace Feishu.Services.Im;

/// <summary>
/// IM 服务聚合（对应 Go 版 service/im）。手写实现，与生成服务共用同一请求管道；
/// 消息资源见 <see cref="ImMessageResource"/>，群组资源见 <see cref="ImChatResource"/>，
/// 事件模型见 <see cref="P2MessageReceiveV1"/> 一族。
/// </summary>
public sealed class ImService
{
    internal ImService(RequestPipeline pipeline)
    {
        V1 = new ImV1(pipeline);
    }

    /// <summary>v1 版本入口（Go 版 service/im/v1）。</summary>
    public ImV1 V1 { get; }

    /// <summary>消息资源快捷入口（等价 V1.Message）。</summary>
    public ImMessageResource Message => V1.Message;

    /// <summary>群组资源快捷入口（等价 V1.Chat）。</summary>
    public ImChatResource Chat => V1.Chat;
}

/// <summary>im/v1 版本命名空间（消息 + 群组两个资源）。</summary>
public sealed class ImV1
{
    internal ImV1(RequestPipeline pipeline)
    {
        Message = new ImMessageResource(pipeline);
        Chat = new ImChatResource(pipeline);
    }

    /// <summary>消息资源（发送/回复/查询/编辑/撤回）。</summary>
    public ImMessageResource Message { get; }

    /// <summary>群组资源（建群/查群/群列表）。</summary>
    public ImChatResource Chat { get; }
}

/// <summary>im/v1 端点路径常量（资源类内部使用）。</summary>
internal static class ImPaths
{
    public const string Messages = "/open-apis/im/v1/messages";
    public const string MessageItem = "/open-apis/im/v1/messages/:message_id";
    public const string MessageReply = "/open-apis/im/v1/messages/:message_id/reply";
    public const string Chats = "/open-apis/im/v1/chats";
    public const string ChatItem = "/open-apis/im/v1/chats/:chat_id";
}

// ==================== 事件 ====================

/// <summary>im 域订阅事件类型名常量（EventDispatcher.On&lt;T&gt; 注册用）。</summary>
public static class ImEventTypes
{
    /// <summary>接收消息。</summary>
    public const string MessageReceiveV1 = "im.message.receive_v1";

    /// <summary>消息已读。</summary>
    public const string MessageReadV1 = "im.message.message_read_v1";

    /// <summary>群创建。</summary>
    public const string ChatCreatedV1 = "im.chat.created_v1";
}
