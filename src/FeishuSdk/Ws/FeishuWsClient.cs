using System.Text;
using System.Net.WebSockets;
using Feishu.Events;

namespace Feishu.Ws;

/// <summary>长连接配置（对应 Go 版 ws.Client 的 WithXxx 选项）。</summary>
public sealed class FeishuWsOptions
{
    public string Domain { get; set; } = FeishuOptions.FeishuBaseUrl;

    /// <summary>断线后是否自动重连（默认 true）。</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>重连间隔；可被服务端下发的 ClientConfig 覆盖。</summary>
    public TimeSpan ReconnectInterval { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>心跳间隔；可被服务端下发的 ClientConfig 覆盖。</summary>
    public TimeSpan PingInterval { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>重连次数上限，-1 表示无限；可被服务端覆盖。</summary>
    public int ReconnectCount { get; set; } = -1;

    /// <summary>首次重连随机抖动上限（秒），避免服务端故障恢复瞬间的重连风暴。</summary>
    public int ReconnectNonceSeconds { get; set; } = 30;

    /// <summary>附加到 bootstrap 请求与 WS 握手的自定义头（如代理网关鉴权）。</summary>
    public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>();

    public string? Source { get; set; }

    /// <summary>ClientAssertion JWT 提供方（代持凭证模式；设置后 AppSecret 不再发送）。</summary>
    public IClientAssertionProvider? ClientAssertionProvider { get; set; }

    /// <summary>日志器（默认 Info 级别控制台输出）。</summary>
    public IFeishuLogger Logger { get; set; } = ConsoleFeishuLogger.InfoOnly;

    /// <summary>JSON 序列化器（默认 SDK 统一实例，中文不转义）。</summary>
    public IFeishuSerializer Serializer { get; set; } = SystemTextJsonFeishuSerializer.Instance;

    /// <summary>bootstrap HTTP 处理器工厂（测试注入用；默认新建 HttpClient）。</summary>
    public Func<HttpMessageHandler>? BootstrapHttpMessageHandlerFactory { get; set; }
}

/// <summary>
/// 飞书 WebSocket 长连接客户端（免公网端点接收事件）。
/// 流程：HTTP bootstrap 换连接 URL → 建立 WebSocket → 心跳（控制帧）→
/// 数据帧（事件，支持分包重组）→ 回执帧 → 断线自动重连。
/// </summary>
public sealed class FeishuWsClient : IAsyncDisposable
{
    private const string GenEndpointUri = "/callback/ws/endpoint";
    private const string HeaderType = "type";
    private const string HeaderMessageId = "message_id";
    private const string HeaderSum = "sum";
    private const string HeaderSeq = "seq";
    private const string HeaderTraceId = "trace_id";
    private const string HeaderBizRt = "biz_rt";

    private const int FrameTypeControl = 0;
    private const int FrameTypeData = 1;
    private const string MessageTypeEvent = "event";
    private const string MessageTypePing = "ping";
    private const string MessageTypePong = "pong";

    private const int BootstrapOk = 0;
    private const int BootstrapSystemBusy = 1;
    private const int BootstrapForbidden = 403;
    private const int BootstrapAuthFailed = 514;
    private const int BootstrapExceedConnLimit = 1000040350;
    private const int BootstrapInternalError = 1000040343;

    private readonly string _appId;
    private readonly string _appSecret;
    private readonly FeishuWsOptions _options;
    private readonly SemaphoreSlim _stateGate = new(1, 1);

    private ClientWebSocket? _ws;
    private string? _connUrl;
    private string _connId = "";
    private string _serviceId = "";
    private CancellationTokenSource _cts = new();
    private Task? _runLoop;
    private readonly FrameReassembler _reassembler = new(TimeSpan.FromSeconds(5));

    private EventBus.IEventHub? _eventHub;

    /// <summary>帧发送钩子（测试注入，拦截实际网络发送）。</summary>
    internal Func<WsFrame, CancellationToken, Task>? FrameSendOverride;

    // StartAsync 进行中标记：防止并发双重启动产生两条运行循环
    private volatile bool _starting;

    // 服务端可下发的运行时配置（pong 载荷）
    private volatile int _reconnectCount;
    private long _reconnectIntervalTicks;
    private long _pingIntervalTicks;
    private volatile int _reconnectNonceSeconds;

    /// <summary>连接就绪（首次建连成功后触发一次）。</summary>
    public event Action? OnReady;

    /// <summary>连接或处理过程出错（不中断重连循环）。</summary>
    public event Action<Exception>? OnError;

    /// <summary>开始重连时触发。</summary>
    public event Action? OnReconnecting;

    /// <summary>重连成功时触发。</summary>
    public event Action? OnReconnected;

    /// <summary>连接断开时触发。</summary>
    public event Action? OnDisconnected;

    /// <summary>当前连接 id（来自连接 URL 的 device_id；未连接为空）。</summary>
    public string? ConnId => _connId;

    /// <summary>用应用凭证创建 WS 客户端（配合 <see cref="Bind(EventBus.IEventHub)"/> 与 <see cref="StartAsync"/> 使用）。</summary>
    public FeishuWsClient(string appId, string appSecret, FeishuWsOptions? options = null)
    {
        _appId = appId;
        _appSecret = appSecret;
        _options = options ?? new FeishuWsOptions();
        _reconnectCount = _options.ReconnectCount;
        _reconnectIntervalTicks = (long)_options.ReconnectInterval.TotalMilliseconds * TimeSpan.TicksPerMillisecond;
        _pingIntervalTicks = (long)_options.PingInterval.TotalMilliseconds * TimeSpan.TicksPerMillisecond;
        _reconnectNonceSeconds = _options.ReconnectNonceSeconds;
    }

    /// <summary>绑定事件枢纽（收到数据帧后回调；FeishuEventBus 与 EventDispatcher 均可）。</summary>
    public FeishuWsClient Bind(EventBus.IEventHub hub)
    {
        _eventHub = hub;
        return this;
    }

    /// <summary>绑定事件分发器（webhook 复用场景；对齐 Go ws.Client.EventHandler()）。</summary>
    public FeishuWsClient Bind(EventDispatcher dispatcher) => Bind((EventBus.IEventHub)dispatcher);

    /// <summary>已绑定的事件枢纽（对齐 Go ws.Client.EventHandler()；FeishuChannel 由此注册原始订阅）。</summary>
    public EventBus.IEventHub? EventHandler() => _eventHub;

    /// <summary>
    /// 启动长连接：完成首次建连后返回，心跳/收包/重连在后台任务中持续运行。
    /// 用 <see cref="ShutdownAsync"/> 停止。需阻塞等待时可用 <see cref="WaitUntilShutdownAsync"/>。
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            if (_runLoop != null && !_runLoop.IsCompleted) return;
            if (_starting) return;
            _starting = true;
            _cts = new CancellationTokenSource();
        }
        finally
        {
            // 不能跨 ConnectAsync 持有门闩：ConnectAsync 拨号成功后自身要再进门换 _ws，
            // 跨 await 持有会形成循环等待（异步信号量自死锁），StartAsync 永不返回。
            _stateGate.Release();
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        try
        {
            var connectOk = await ConnectAsync(linked.Token);
            if (!connectOk.ok) throw connectOk.error!;

            await _stateGate.WaitAsync(CancellationToken.None);
            try { _runLoop = Task.Run(() => RunLoopsAsync(linked.Token)); }
            finally { _stateGate.Release(); }

            OnReady?.Invoke();
        }
        finally
        {
            _starting = false;
        }
    }

    /// <summary>等待后台循环退出（正常情况永不返回，直到 Shutdown）。</summary>
    public Task WaitUntilShutdownAsync() => _runLoop ?? Task.CompletedTask;

    /// <summary>停止长连接并释放资源（不再自动重连）。</summary>
    public async Task ShutdownAsync()
    {
        await _stateGate.WaitAsync(CancellationToken.None);
        try
        {
            _options.AutoReconnect = false;
            _cts.Cancel();
        }
        finally
        {
            _stateGate.Release();
        }
        await DisconnectAsync(CancellationToken.None, raiseEvent: false);
        if (_runLoop != null)
        {
            try { await _runLoop; } catch (OperationCanceledException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync();
        _cts.Dispose();
        _stateGate.Dispose();
    }

    // ---- 后台循环 ----

    private async Task RunLoopsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pingTask = PingLoopAsync(loopCts.Token);
            var receiveTask = ReceiveLoopAsync(ct);

            // 任一循环退出即视为断线
            var finished = await Task.WhenAny(receiveTask, pingTask).ConfigureAwait(false);
            loopCts.Cancel();
            try { await Task.WhenAll(receiveTask, pingTask); } catch (OperationCanceledException) { }

            if (!_options.AutoReconnect || ct.IsCancellationRequested)
            {
                OnDisconnected?.Invoke();
                return;
            }

            await ReconnectAsync(ct); // 成功后 _ws 已是新连接；致命错误会抛出终止
            OnReconnected?.Invoke();
        }
    }

    private async Task ReconnectAsync(CancellationToken ct)
    {
        OnReconnecting?.Invoke();

        // 首次重连随机抖动
        var nonce = _reconnectNonceSeconds;
        if (nonce > 0)
            await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(nonce * 1000) / 1000.0), ct);

        var count = _reconnectCount;
        for (var i = 0; count < 0 || i < count; i++)
        {
            if (ct.IsCancellationRequested) return;
            _options.Logger.Info($"trying to reconnect: {i + 1}");
            var (ok, error) = await ConnectAsync(ct);
            if (ok) return;
            if (error is FeishuWsFatalException fatal)
            {
                OnError?.Invoke(fatal);
                throw fatal;
            }
            OnError?.Invoke(error!);
            await Task.Delay(new TimeSpan(Interlocked.Read(ref _reconnectIntervalTicks)), ct);
        }
        throw new FeishuWsFatalException($"unable to connect to server after {count} retries");
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SendPingAsync(ct);
                _options.Logger.Debug("ping success");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _options.Logger.Warn($"ping failed: {ex.Message}");
            }
            await Task.Delay(new TimeSpan(Interlocked.Read(ref _pingIntervalTicks)), ct);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (!ct.IsCancellationRequested)
        {
            WebSocket? ws;
            await _stateGate.WaitAsync(ct);
            try { ws = _ws; } finally { _stateGate.Release(); }
            if (ws is null || ws.State != WebSocketState.Open)
            {
                _options.Logger.Error("connection is closed, receive loop exit");
                return;
            }

            // 组装一条完整 WebSocket 消息（可能分片）
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _options.Logger.Warn("server sent close frame");
                    return;
                }
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Binary)
            {
                _options.Logger.Warn($"unknown websocket message type: {result.MessageType}");
                continue;
            }

            // 数据帧处理放后台，避免慢业务阻塞收包
            _ = Task.Run(() => HandleMessageSafeAsync(message.ToArray(), ct), ct);
        }
    }

    private async Task HandleMessageSafeAsync(byte[] data, CancellationToken ct)
    {
        try
        {
            var frame = WsFrame.Parse(data);
            switch (frame.Method)
            {
                case FrameTypeControl:
                    HandleControlFrame(frame);
                    break;
                case FrameTypeData:
                    await HandleDataFrameAsync(frame, ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            _options.Logger.Error($"handle ws message failed: {ex.Message}", ex);
        }
    }

    internal void HandleControlFrame(WsFrame frame)
    {
        var type = frame.GetHeader(HeaderType);
        if (type != MessageTypePong) return;
        _options.Logger.Debug("receive pong");
        if (frame.Payload is not { Length: > 0 } payload) return;

        try
        {
            var config = _options.Serializer.Deserialize<WsClientConfig>(payload);
            ApplyServerConfig(config);
        }
        catch (Exception ex)
        {
            _options.Logger.Warn($"unmarshal client config failed: {ex.Message}");
        }
    }

    internal async Task HandleDataFrameAsync(WsFrame frame, CancellationToken ct)
    {
        var sum = frame.GetHeaderInt(HeaderSum);
        var messageId = frame.GetHeader(HeaderMessageId) ?? "";
        var traceId = frame.GetHeader(HeaderTraceId) ?? "";
        var type = frame.GetHeader(HeaderType) ?? "";

        byte[]? payload = frame.Payload;
        if (sum > 1)
        {
            payload = _reassembler.Combine(messageId, sum, frame.GetHeaderInt(HeaderSeq), payload ?? Array.Empty<byte>());
            if (payload == null) return; // 等待其余分包
        }

        _options.Logger.Debug($"receive message, type: {type}, message_id: {messageId}, trace_id: {traceId}");

        // 对齐 Go：卡片帧当前直接忽略（不回执），仅事件帧进入分发
        if (type != MessageTypeEvent)
            return;

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var success = false;
        if (_eventHub != null)
        {
            try
            {
                // 处理器返回错误/抛异常 → 回执 500（对齐 Go handleDataFrame 的 err 分支）
                success = await _eventHub.DispatchAsync(payload!, ct);
            }
            catch (Exception ex)
            {
                _options.Logger.Error($"handle ws event failed, message_id: {messageId}, trace_id: {traceId}, err: {ex.Message}", ex);
            }
        }
        watch.Stop();
        frame.SetHeader(HeaderBizRt, watch.ElapsedMilliseconds.ToString());

        var response = new WsUpstreamResponse { StatusCode = success ? 200 : 500 };
        frame.Payload = _options.Serializer.SerializeToUtf8Bytes(response);
        if (FrameSendOverride != null)
            await FrameSendOverride(frame, ct);
        else
            await SendFrameAsync(frame, ct);
    }

    // ---- 连接管理 ----

    private async Task<(bool ok, Exception? error)> ConnectAsync(CancellationToken ct)
    {
        string url;
        try
        {
            url = await GetConnUrlAsync(ct);
        }
        catch (FeishuWsFatalException fatal)
        {
            _options.Logger.Error($"connect failed, err: {fatal.Message}");
            return (false, fatal);
        }
        catch (Exception ex)
        {
            _options.Logger.Warn($"get conn url failed, err: {ex.Message}");
            return (false, ex);
        }

        var uri = new Uri(url);
        var connId = uri.Query.Contains("device_id=") ? ParseQueryParam(uri, "device_id") : "";
        var serviceId = ParseQueryParam(uri, "service_id");

        var ws = new ClientWebSocket();
        try
        {
            await ws.ConnectAsync(uri, ct);
        }
        catch (Exception ex)
        {
            ws.Dispose();
            _options.Logger.Warn($"websocket dial failed: {ex.Message}");
            return (false, ex);
        }

        await _stateGate.WaitAsync(ct);
        try
        {
            _ws?.Dispose();
            _ws = ws;
            _connUrl = url;
            _connId = connId;
            _serviceId = serviceId;
        }
        finally
        {
            _stateGate.Release();
        }
        _options.Logger.Info($"connected to {uri.Host} [conn_id={connId}]");
        return (true, null);
    }

    private async Task DisconnectAsync(CancellationToken ct, bool raiseEvent = true)
    {
        await _stateGate.WaitAsync(ct);
        try
        {
            if (_ws == null) return;
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "client shutdown", ct);
            }
            catch (Exception ex)
            {
                _options.Logger.Warn($"close websocket failed: {ex.Message}");
            }
            _ws.Dispose();
            _ws = null;
            _connUrl = null;
            _connId = "";
            _serviceId = "";
            _options.Logger.Info($"disconnected [conn_id={_connId}]");
        }
        finally
        {
            _stateGate.Release();
        }
        if (raiseEvent) OnDisconnected?.Invoke();
    }

    /// <summary>bootstrap：POST {domain}/callback/ws/endpoint 换取带凭证的连接 URL。</summary>
    internal async Task<string> GetConnUrlAsync(CancellationToken ct)
    {
        if (_options.ClientAssertionProvider == null && _appSecret.Length == 0)
            throw new FeishuWsFatalException(FeishuErrorCodes.AppSecretAndClientAssertionEmpty,
                "appSecret and clientAssertionProvider cannot both be empty");

        var requestUrl = _options.Domain.TrimEnd('/') + GenEndpointUri;
        var body = new WsBootstrapRequest { AppId = _appId, AppSecret = _appSecret };
        if (_options.ClientAssertionProvider != null)
        {
            var aud = ExtractHost(_options.Domain) ?? throw new FeishuWsFatalException("invalid domain");
            var assertion = await _options.ClientAssertionProvider.RetrieveTokenAsync(aud, ct);
            if (assertion.Value is not { Length: > 0 })
                throw new FeishuWsFatalException(FeishuErrorCodes.ClientAssertionTokenEmpty, "client assertion token is empty");
            body.AppSecret = "";
            body.ClientAssertion = assertion.Value;
            if (assertion.TargetService is { Length: > 0 })
                requestUrl = BuildProxyUrl(assertion.TargetService, assertion.TargetPrefix, GenEndpointUri);
        }

        using var http = _options.BootstrapHttpMessageHandlerFactory != null
            ? new HttpClient(_options.BootstrapHttpMessageHandlerFactory(), disposeHandler: true)
            : new HttpClient();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUrl)
        {
            Content = new StringContent(_options.Serializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.TryAddWithoutValidation("locale", "zh");
        foreach (var (k, v) in _options.Headers)
            httpRequest.Headers.TryAddWithoutValidation(k, v);
        httpRequest.Headers.TryAddWithoutValidation("User-Agent", RequestPipeline.UserAgent(_options.Source));

        using var httpResponse = await http.SendAsync(httpRequest, ct);
        var respBody = await httpResponse.Content.ReadAsStringAsync(ct);
        if (!httpResponse.IsSuccessStatusCode)
        {
            var serverMsg = TryGetMsg(respBody) ?? "system busy";
            throw new FeishuWsFatalException((int)httpResponse.StatusCode, serverMsg);
        }

        var endpoint = _options.Serializer.Deserialize<WsEndpointResp>(respBody);
        switch (endpoint.Code)
        {
            case BootstrapOk:
                break;
            case BootstrapSystemBusy:
                return Fail(BootstrapSystemBusy, "system busy");
            case BootstrapInternalError:
                return Fail(endpoint.Code, endpoint.Msg ?? "internal error");
            default:
                throw new FeishuWsFatalException(endpoint.Code, endpoint.Msg ?? $"bootstrap code {endpoint.Code}");
        }

        if (endpoint.Data?.Url is not { Length: > 0 })
            return Fail(500, "endpoint is null");

        if (endpoint.Data.ClientConfig != null)
            ApplyServerConfig(endpoint.Data.ClientConfig);

        return endpoint.Data.Url;

        string Fail(int code, string msg) => throw new FeishuWsFatalException(code, msg);
    }

    private async Task SendPingAsync(CancellationToken ct)
    {
        int serviceId;
        await _stateGate.WaitAsync(ct);
        try
        {
            if (_ws is not { State: WebSocketState.Open }) return;
            serviceId = int.TryParse(_serviceId, out var sid) ? sid : 0;
        }
        finally
        {
            _stateGate.Release();
        }

        await SendFrameAsync(BuildPingFrame(serviceId), ct);
    }

    internal static WsFrame BuildPingFrame(int serviceId)
    {
        var ping = new WsFrame
        {
            Method = FrameTypeControl,
            Service = serviceId,
        };
        ping.SetHeader(HeaderType, MessageTypePing);
        return ping;
    }

    private async Task SendFrameAsync(WsFrame frame, CancellationToken ct)
    {
        var bytes = frame.ToBytes();
        await _stateGate.WaitAsync(ct);
        try
        {
            if (_ws is not { State: WebSocketState.Open })
                throw new FeishuWsFatalException("connection is closed");
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Binary, true, ct);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private void ApplyServerConfig(WsClientConfig config)
    {
        if (config.ReconnectCount is { } c) _reconnectCount = c;
        if (config.ReconnectInterval is { } i) _reconnectIntervalTicks = (long)TimeSpan.FromSeconds(i).TotalMilliseconds * TimeSpan.TicksPerMillisecond;
        if (config.ReconnectNonce is { } n) _reconnectNonceSeconds = n;
        if (config.PingInterval is { } p) _pingIntervalTicks = (long)TimeSpan.FromSeconds(p).TotalMilliseconds * TimeSpan.TicksPerMillisecond;
        _options.Logger.Debug($"client config applied: reconnectCount={_reconnectCount}, pingInterval={new TimeSpan(_pingIntervalTicks).TotalSeconds}s");
    }

    private static string? TryGetMsg(string body)
    {
        try
        {
            return SystemTextJsonFeishuSerializer.Instance.Deserialize<WsEndpointResp>(body).Msg;
        }
        catch
        {
            return null;
        }
    }

    private static string ParseQueryParam(Uri uri, string key)
    {
        var query = uri.Query.TrimStart('?').Split('&');
        foreach (var pair in query)
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq] == key)
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return "";
    }

    private static string? ExtractHost(string url)
    {
        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;
    }

    private static string BuildProxyUrl(string targetService, string? targetPrefix, string apiPath)
    {
        if (!targetService.Contains("://", StringComparison.Ordinal)) targetService = "https://" + targetService;
        return targetService.TrimEnd('/') + (targetPrefix ?? "").TrimEnd('/') + apiPath;
    }
}

/// <summary>认证失败等不可恢复错误：不再重连。</summary>
public sealed class FeishuWsFatalException : Exception
{
    /// <summary>飞书业务错误码（HTTP 状态码或 bootstrap code）。</summary>
    public int Code { get; }

    public FeishuWsFatalException(int code, string message) : base($"code: {code}, msg: {message}") => Code = code;

    public FeishuWsFatalException(string message) : base(message) => Code = 0;
}
