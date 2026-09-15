using System.Text.Json.Serialization;

namespace Feishu.Channel;

/// <summary>拒绝原因（对齐 Go RejectReason）。</summary>
public enum ChannelRejectReason
{
    GroupNotAllowed,
    NoMention,
    MentionAllBlocked,
    DmDisabled,
    SenderNotAllowed,
}

public sealed record ChannelPolicyDecision(bool Allowed, ChannelRejectReason? Reason = null);

/// <summary>策略配置（对齐 Go PolicyConfig：群白名单 / 必须@ / @所有 / 私信模式）。</summary>
public sealed class ChannelPolicyConfig
{
    public List<string>? GroupAllowlist { get; set; }

    /// <summary>群聊中是否必须 @ 机器人（默认 true）。</summary>
    public bool? RequireMention { get; set; }

    /// <summary>@ 所有人是否响应（默认 false）。</summary>
    public bool? RespondToMentionAll { get; set; }

    /// <summary>私信模式：open / disabled / allowlist（默认 open）。</summary>
    public string? DmMode { get; set; }

    public List<string>? DmAllowlist { get; set; }
}

/// <summary>机器人身份（open_id/name/activate_status，来自 /open-apis/bot/v3/info）。</summary>
public sealed record BotIdentity(string OpenId, string? UserId, string Name, int ActivateStatus);

/// <summary>提及。</summary>
public sealed class ChannelMention
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("user_id")]
    public string UserId { get; set; } = "";

    [JsonPropertyName("open_id")]
    public string OpenId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("is_bot")]
    public bool IsBot { get; set; }
}

/// <summary>消息附带资源（图片/文件/音视频/表情）。</summary>
public sealed class ChannelResource
{
    public string Type { get; set; } = "";

    public string FileKey { get; set; } = "";

    public string? FileName { get; set; }

    public int? DurationMs { get; set; }

    public string? CoverImageKey { get; set; }
}

/// <summary>归一化消息（对齐 Go NormalizedMessage）。</summary>
public sealed class NormalizedMessage
{
    public string EventId { get; set; } = "";
    public string MessageId { get; set; } = "";
    public string ChatId { get; set; } = "";
    /// <summary>group / p2p。</summary>
    public string ChatType { get; set; } = "";
    public string UserId { get; set; } = "";
    /// <summary>标准文本内容（markdown 化）。</summary>
    public string Content { get; set; } = "";
    /// <summary>原始消息类型（text/post/image…）。</summary>
    public string RawContentType { get; set; } = "";
    public List<ChannelMention> Mentions { get; set; } = new();
    public bool MentionAll { get; set; }
    public bool MentionedBot { get; set; }
    public List<ChannelResource> Resources { get; set; } = new();
    public long CreateTimeMs { get; set; }
}

/// <summary>发送入参（对齐 Go SendInput，可空字段按优先级自动组装）。</summary>
public sealed class ChannelSendInput
{
    public string? ReceiveId { get; set; }
    public string? ChatId { get; set; }
    public string? UserId { get; set; }
    public string? MsgType { get; set; }
    public string? ReplyMessageId { get; set; }

    public string? Text { get; set; }
    public string? Markdown { get; set; }
    public string? Title { get; set; }
    public string? ImageKey { get; set; }
    public string? FileKey { get; set; }
    public string? AudioKey { get; set; }
    public string? VideoKey { get; set; }
    /// <summary>卡片 JSON 字符串。</summary>
    public string? Card { get; set; }
    public string? Post { get; set; }
    public string? ShareChatId { get; set; }
    public string? ShareUserId { get; set; }
    public string? StickerFileKey { get; set; }

    public string? ImagePath { get; set; }
    public string? FilePath { get; set; }

    public List<ChannelMention>? Mentions { get; set; }
}

public sealed record ChannelSendResult(string MessageId, string? ChatId = null, List<string>? ChunkIds = null);

/// <summary>被策略拒绝的事件。</summary>
public sealed record ChannelRejectEvent(string MessageId, string ChatId, string SenderId, string Reason);

/// <summary>表情回复事件（创建/删除归一）。</summary>
public sealed record ChannelReactionEvent(
    string EventId = "",
    string MessageId = "",
    string ReactionType = "",
    string UserId = "",
    string Action = "", // added / removed
    long CreateTimeMs = 0);

/// <summary>评论事件（drive.notice.comment_add_v1 归一）。</summary>
public sealed record ChannelCommentEvent(
    string EventId = "",
    string CommentId = "",
    string FileToken = "",
    string FileType = "",
    string ReplyId = "",
    string OperatorOpenId = "",
    string OperatorUserId = "",
    string OperatorUnionId = "",
    bool MentionedBot = false,
    long Timestamp = 0);

/// <summary>机器人进群事件。</summary>
public sealed record ChannelBotAddedEvent(string EventId = "", string ChatId = "", string ChatName = "", string UserId = "", long CreateTimeMs = 0);

/// <summary>卡片行为事件（归一）。</summary>
public sealed record ChannelCardActionEvent(
    string EventId = "",
    string MessageId = "",
    string ChatId = "",
    string Token = "",
    string? OperatorOpenId = null,
    string? OperatorUserId = null,
    string? ActionTag = null,
    Dictionary<string, object?>? ActionValue = null);

/// <summary>批量分发：合并后的消息 + 来源消息 ID。</summary>
public sealed record BatchedDispatch(NormalizedMessage Message, IReadOnlyList<string> SourceIds);

/// <summary>批量聚合配置（对齐 Go BatchConfig 默认值）。</summary>
public sealed record ChannelBatchConfig
{
    public TimeSpan Delay { get; init; } = TimeSpan.FromMilliseconds(600);

    public int LongThresholdChars { get; init; } = 1000;

    public TimeSpan LongDelay { get; init; } = TimeSpan.FromMilliseconds(2000);

    public int MaxMessages { get; init; } = 8;

    public int MaxChars { get; init; } = 4000;
}

/// <summary>Channel 总配置（对齐 Go DefaultChannelConfig）。</summary>
public sealed class ChannelConfig
{
    public int DedupMaxEntries { get; set; } = 10000;

    public TimeSpan DedupTtl { get; set; } = TimeSpan.FromHours(1);

    public ChannelBatchConfig Batch { get; set; } = new();

    /// <summary>过期消息窗口（默认 30 分钟）。</summary>
    public TimeSpan StaleMessageWindow { get; set; } = TimeSpan.FromMinutes(30);

    public ChannelPolicyConfig Policy { get; set; } = new();

    /// <summary>出站文本分片上限。</summary>
    public int TextChunkLimit { get; set; } = 3500;

    public TimeSpan StreamThrottle { get; set; } = TimeSpan.FromMilliseconds(100);

    public int RetryMaxAttempts { get; set; } = 3;

    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan BotIdentityTtl { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan BotIdentityMinRefreshInterval { get; set; } = TimeSpan.FromMinutes(1);
}

// ---- 错误分类（对齐 Go channel/types/errors.go）----

public enum ChannelErrorCode
{
    TargetRevoked,
    PermissionDenied,
    FormatError,
    RateLimited,
    SsrfBlocked,
    SendTimeout,
    Unknown,
}

public sealed class FeishuChannelException : Exception
{
    public ChannelErrorCode Code { get; }

    public FeishuChannelException(ChannelErrorCode code, string message, Exception? cause = null)
        : base($"FeishuChannelError(code={code}): {message}" + (cause != null ? $" | cause: {cause.Message}" : ""), cause)
        => Code = code;

    public static ChannelErrorCode Classify(Exception error)
    {
        if (error is FeishuChannelException fce) return fce.Code;
        if (error is FeishuCodeException apiErr)
        {
            return apiErr.Code switch
            {
                // 230011 消息被撤回 / 230017、230020、230040 目标消息不可达
                230011 or 230017 or 230020 or 230040 => ChannelErrorCode.TargetRevoked,
                99991400 or 99991401 or 230002 => ChannelErrorCode.PermissionDenied,
                230001 => ChannelErrorCode.FormatError,
                _ => ChannelErrorCode.Unknown,
            };
        }

        var msg = error.Message.ToLowerInvariant();
        if (msg.Contains("status 429")) return ChannelErrorCode.RateLimited;
        if (msg.Contains("status 401") || msg.Contains("status 403")) return ChannelErrorCode.PermissionDenied;
        if (msg.Contains("status 400")) return ChannelErrorCode.FormatError;
        if (msg.Contains("status 404")) return ChannelErrorCode.TargetRevoked;
        if (msg.Contains("timeout") || msg.Contains("etimedout") || msg.Contains("econnaborted")) return ChannelErrorCode.SendTimeout;
        return ChannelErrorCode.Unknown;
    }

    public bool Retryable => Code is ChannelErrorCode.RateLimited or ChannelErrorCode.Unknown;
}

/// <summary>媒体上传入参（对齐 Go channel UploadInput：三选一来源 + 类型）。</summary>
public sealed record ChannelUploadInput(
    string Kind, // image / file / audio / video
    string? SourceUrl = null,
    string? SourcePath = null,
    byte[]? SourceBytes = null,
    string? FileName = null);
