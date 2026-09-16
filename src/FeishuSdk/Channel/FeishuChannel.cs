using Feishu.Events;
using Feishu.Ws;

namespace Feishu.Channel;

/// <summary>
/// 高层机器人编排（对齐 Go channel.Channel）：
/// 消息归一化 → 过期/去重/处理锁 → 策略门控 → 按会话批量合并分发；
/// 发送侧统一 SendInput（自动识别 receive_id_type、Markdown→Post、长文分片、
/// 上传本地文件、回复目标失效/格式错误自动降级）；Stream 提供节流式流式回复。
/// </summary>
/// <remarks>
/// 本类按职责拆分为四个分部文件：
/// <list type="bullet">
/// <item><see cref="FeishuChannel"/>（本文件）：字段/构造/流式入口/生命周期；</item>
/// <item>FeishuChannel.Events.cs：事件订阅注册、入站处理管道、机器人身份缓存；</item>
/// <item>FeishuChannel.Send.cs：结构化发送、降级与重试；</item>
/// <item>FeishuChannel.Media.cs：图片/文件/媒体上传与下载。</item>
/// </list>
/// </remarks>
public sealed partial class FeishuChannel : IDisposable
{
    private readonly FeishuClient _client;
    private readonly FeishuWsClient? _ws;
    private readonly ChannelConfig _config;
    private readonly DedupCache _dedup;
    private readonly ChatPipelineManager _pipelines;
    private readonly PolicyGate _policyGate;
    private readonly ProcessingLock _processLock;

    /// <summary>暴露给流式控制器使用的底层客户端（internal，外部请直接用 SendAsync）。</summary>
    internal FeishuClient Client => _client;

    /// <summary>Channel 运行配置快照（构造后只读）。</summary>
    internal ChannelConfig Config => _config;

    /// <summary>
    /// 创建 Channel。ws 传 null 时仅可用发送侧能力（事件订阅在绑定 WS 后才生效）；
    /// configure 可覆盖分片长度/节流间隔/重试策略等（见 <see cref="ChannelConfig"/>）。
    /// </summary>
    public FeishuChannel(FeishuClient client, FeishuWsClient? ws = null, Action<ChannelConfig>? configure = null)
    {
        _client = client;
        _ws = ws;
        _config = new ChannelConfig();
        configure?.Invoke(_config);
        _dedup = new DedupCache(_config.DedupMaxEntries, _config.DedupTtl);
        _pipelines = new ChatPipelineManager(_config.Batch);
        _policyGate = new PolicyGate(_config.Policy);
        _processLock = new ProcessingLock(TimeSpan.FromMinutes(5));
    }

    // ==================== 流式回复 ====================

    /// <summary>开启流式消息会话：Card 输入直接首发并支持换卡；Markdown/Text 输入走节流追加。</summary>
    /// <param name="input">发送入参；Markdown 与 Text 均为空时以 "..." 占位先发一条。</param>
    /// <returns>流式控制器（<see cref="IStreamController"/>），用完调用 CloseAsync 收尾。</returns>
    public async Task<IStreamController> StreamAsync(ChannelSendInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!string.IsNullOrEmpty(input.Card))
        {
            var res = await SendAsync(input, ct);
            return new CardStreamController(this, res.MessageId, _config);
        }

        if (string.IsNullOrEmpty(input.Markdown) && string.IsNullOrEmpty(input.Text))
            input.Markdown = "...";
        var initial = await SendAsync(input, ct);
        return new MarkdownStreamController(this, initial.MessageId, input.Markdown ?? "", input.Title, _config);
    }

    /// <summary>流式控制器：Append 追加 / UpdateCard 换卡 / Flush 立即发 / Close 收尾。</summary>
    public interface IStreamController
    {
        /// <summary>追加一段文本（仅 Markdown 流支持；Card 流调用会抛 FormatError）。</summary>
        Task AppendAsync(string text, CancellationToken ct = default);

        /// <summary>整体替换卡片 JSON（仅 Card 流支持；Markdown 流调用会抛 FormatError）。</summary>
        Task UpdateCardAsync(string cardJson, CancellationToken ct = default);

        /// <summary>跳过节流间隔，立即把当前内容发出去。</summary>
        Task FlushAsync(CancellationToken ct = default);

        /// <summary>结束流式会话（发完缓冲内容并停止节流器）。</summary>
        Task CloseAsync(CancellationToken ct = default);
    }

    // ==================== 生命周期 ====================

    /// <summary>启动 WS 长连接（未绑定 ws 时为空操作）。等价于直接调 FeishuWsClient.StartAsync。</summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        if (_ws == null) return Task.CompletedTask;
        return _ws.StartAsync(ct);
    }

    /// <summary>停机：先断开 WS，再把各会话批量队列中未分发的事件冲刷完毕。</summary>
    public async Task StopAsync()
    {
        if (_ws != null) await _ws.ShutdownAsync();
        await _pipelines.FlushAllAsync();
    }

    /// <summary>运行期修改策略门控配置（群白名单/@要求等），对新消息立即生效。</summary>
    public void UpdatePolicy(Action<ChannelPolicyConfig> mutate) => _policyGate.UpdateConfig(mutate);

    /// <summary>读取当前策略门控配置快照。</summary>
    public ChannelPolicyConfig GetPolicy() => _policyGate.GetConfig();

    /// <summary>测试钩子：直接访问去重缓存。</summary>
    internal DedupCache DedupForTest => _dedup;

    /// <summary>测试钩子：直接访问会话批量管道。</summary>
    internal ChatPipelineManager PipelinesForTest => _pipelines;

    /// <summary>释放资源（冲刷并销毁批量管道）。</summary>
    public void Dispose() => _pipelines.Dispose();
}
