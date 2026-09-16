using System.Text;
using Feishu;
using Feishu.EventBus;
using Feishu.Events;
using Feishu.Services.Im;
using Feishu.Ws;

namespace FeishuSdk.Tests;

/// <summary>
/// 事件总线契约：订阅形态（typed/raw/通配/全量）、异常隔离、退订、
/// 生命周期、IObservable 流，以及 WS→总线→订阅者的真实全链路。
/// </summary>
public class EventBusTests
{
    private static byte[] Payload(string eventType, string extra = "") =>
        Encoding.UTF8.GetBytes(
            "{\"schema\":\"2.0\",\"header\":{\"event_id\":\"ev_1\",\"event_type\":\"" + eventType +
            "\",\"create_time\":\"1700000000000\",\"app_id\":\"app_1\",\"tenant_key\":\"tk_1\"}," +
            "\"event\":{\"app_ticket\":\"t\",\"extra\":\"" + extra + "\"}}");

    private static byte[] MessagePayload(string text = "hi") =>
        Encoding.UTF8.GetBytes(
            "{\"schema\":\"2.0\",\"header\":{\"event_id\":\"ev_m1\",\"event_type\":\"im.message.receive_v1\",\"create_time\":\"1700000000000\"}," +
            "\"event\":{\"sender\":{\"sender_id\":{\"open_id\":\"ou_1\"},\"sender_type\":\"user\"}," +
            "\"message\":{\"message_id\":\"om_1\",\"chat_id\":\"oc_1\",\"chat_type\":\"p2p\",\"message_type\":\"text\"," +
            "\"content\":\"{\\\"text\\\":\\\"" + text + "\\\"}\"}}}");

    // ---- 订阅形态 ----

    [Fact]
    public async Task Typed_Subscribe_Should_Receive_Envelope_And_Strong_Typed_View()
    {
        var bus = new FeishuEventBus();
        FeishuEventEnvelope? got = null;
        P2MessageReceiveV1? typed = null;
        bus.Subscribe<P2MessageReceiveV1>("im.message.receive_v1", (envelope, evt, _) =>
        {
            got = envelope; typed = evt;
            return Task.CompletedTask;
        });

        var ok = await bus.DispatchAsync(MessagePayload("总线测试"));

        Assert.True(ok);
        Assert.NotNull(got);
        Assert.Equal("im.message.receive_v1", got!.EventType);
        Assert.Equal("ev_m1", got.EventId);
        Assert.Equal(FeishuEventSource.Ws, got.Source);
        Assert.NotNull(typed);
        Assert.Equal("om_1", typed!.Message?.MessageId);
        Assert.Equal("ou_1", typed.Sender?.SenderId?.OpenId);
    }

    [Fact]
    public async Task Raw_Subscribe_Should_Receive_Full_Payload()
    {
        var bus = new FeishuEventBus();
        byte[]? got = null;
        bus.SubscribeRaw("app_ticket", (raw, _) => { got = raw; return Task.CompletedTask; });

        var ok = await bus.DispatchAsync(Payload("app_ticket"));

        Assert.True(ok);
        Assert.NotNull(got);
        Assert.Contains("app_ticket", Encoding.UTF8.GetString(got!));
    }

    [Fact]
    public async Task Pattern_Subscribe_Should_Match_Prefix_And_Ignore_Others()
    {
        var bus = new FeishuEventBus();
        var hits = new List<string>();
        bus.SubscribePattern("im.message.*", (envelope, _) => { hits.Add(envelope.EventType); return Task.CompletedTask; });

        await bus.DispatchAsync(Payload("im.message.reaction.created_v1"));
        await bus.DispatchAsync(Payload("im.chat.updated_v1"));

        Assert.Single(hits);
        Assert.Equal("im.message.reaction.created_v1", hits[0]);
    }

    [Fact]
    public async Task SubscribeAll_Should_Receive_Every_Event_Type()
    {
        var bus = new FeishuEventBus();
        var types = new List<string>();
        bus.SubscribeAll((envelope, _) => { types.Add(envelope.EventType); return Task.CompletedTask; });

        await bus.DispatchAsync(Payload("a.b_v1"));
        await bus.DispatchAsync(Payload("c.d_v1"));

        Assert.Equal(["a.b_v1", "c.d_v1"], types);
    }

    // ---- 分发语义（对齐 Go 回执） ----

    [Fact]
    public async Task No_Subscriber_Should_Return_True_For_Ack_200()
    {
        var bus = new FeishuEventBus();
        var ok = await bus.DispatchAsync(Payload("unknown.event_v1"));
        Assert.True(ok); // 无订阅者：回执 200，避免飞书无谓重推
    }

    [Fact]
    public async Task Handler_Exception_Should_Isolate_Others_And_Return_False()
    {
        var bus = new FeishuEventBus();
        var secondRan = false;
        var errors = new List<FeishuHandlerErrorEvent>();
        bus.OnHandlerError(e => errors.Add(e));
        bus.Subscribe<P2MessageReceiveV1>("im.message.receive_v1", (_, _, _) => throw new InvalidOperationException("boom"));
        bus.Subscribe<P2MessageReceiveV1>("im.message.receive_v1", (_, _, _) => { secondRan = true; return Task.CompletedTask; });

        var ok = await bus.DispatchAsync(MessagePayload());

        Assert.False(ok); // 有失败 → WS 回执 500（对齐 Go）
        Assert.True(secondRan); // 异常隔离：后续订阅者照常执行
        Assert.Single(errors);
        Assert.Equal("im.message.receive_v1", errors[0].EventType);
    }

    [Fact]
    public async Task Dispose_Should_Unsubscribe()
    {
        var bus = new FeishuEventBus();
        var count = 0;
        var sub = bus.SubscribeAll((_, _) => { count++; return Task.CompletedTask; });

        await bus.DispatchAsync(Payload("a_v1"));
        sub.Dispose();
        await bus.DispatchAsync(Payload("a_v1"));

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Invalid_Json_Should_Return_False()
    {
        var bus = new FeishuEventBus();
        Assert.False(await bus.DispatchAsync("not json"u8.ToArray()));
        // 有 header 无 event_type 也视为无效
        Assert.False(await bus.DispatchAsync("""{"header":{}}"""u8.ToArray()));
    }

    // ---- 生命周期与可观察流 ----

    [Fact]
    public void Lifecycle_Should_FanOut_To_Subscribers()
    {
        var bus = new FeishuEventBus();
        var got = new List<FeishuLifecycleEvent>();
        bus.OnLifecycle(e => got.Add(e));

        bus.PublishLifecycle(new FeishuLifecycleEvent(FeishuLifecycleKind.WsReady, null, DateTimeOffset.UtcNow));
        bus.PublishLifecycle(new FeishuLifecycleEvent(FeishuLifecycleKind.WsError, new Exception("dial failed"), DateTimeOffset.UtcNow));

        Assert.Equal(2, got.Count);
        Assert.Equal(FeishuLifecycleKind.WsReady, got[0].Kind);
        Assert.NotNull(got[1].Error);
    }

    [Fact]
    public async Task AsObservable_Should_Stream_Envelopes()
    {
        var bus = new FeishuEventBus();
        var seen = new List<FeishuEventEnvelope>();
        var sub = bus.AsObservable().Subscribe(e => seen.Add(e));

        await bus.PublishAsync(Payload("a_v1"));
        await bus.PublishAsync(Payload("b_v1"));
        sub.Dispose();
        await bus.PublishAsync(Payload("c_v1"));

        Assert.Equal(["a_v1", "b_v1"], seen.Select(e => e.EventType).ToList());
    }

    // ---- 契约互操作：EventDispatcher 与 Bus 可互换挂到 WS 上 ----

    [Fact]
    public async Task EventDispatcher_Should_Implement_IEventHub_With_Unsubscribe()
    {
        var dispatcher = new EventDispatcher();
        var hub = (IEventHub)dispatcher;
        var count = 0;
        var sub = hub.SubscribeRaw("app_ticket", (_, _) => { count++; return Task.CompletedTask; });

        await hub.DispatchAsync(Payload("app_ticket"));
        sub.Dispose();
        await hub.DispatchAsync(Payload("app_ticket"));

        Assert.Equal(1, count); // 退订后不再分发
    }

    // ---- WS → 总线真实全链路（mock 服务端推送 → WS 收帧 → 总线分发 → 订阅者收到） ----

    [Fact]
    public async Task Full_E2E_Ws_To_Bus_Typed_Pattern_Lifecycle()
    {
        using var server = new MockFeishuServer();
        var bus = new FeishuEventBus();

        var typedDone = new TaskCompletionSource<P2MessageReceiveV1>(TaskCreationOptions.RunContinuationsAsynchronously);
        var patternDone = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var patternTypes = new List<string>();
        bus.Subscribe<P2MessageReceiveV1>(ImEventTypes.MessageReceiveV1, (_, evt, _) =>
        {
            if (evt != null) typedDone.TrySetResult(evt);
            return Task.CompletedTask;
        });
        bus.SubscribePattern("im.message.*", (envelope, _) =>
        {
            lock (patternTypes) patternTypes.Add(envelope.EventType);
            patternDone.TrySetResult(envelope.EventType);
            return Task.CompletedTask;
        });

        await using var wsClient = new FeishuWsClient("mock_app", "mock_secret", new FeishuWsOptions
        {
            Domain = server.BaseUrl,
            Logger = NullFeishuLogger.Instance,
        });
        wsClient.Bind(bus); // 关键差异：WS 直接绑总线（不再经过 EventDispatcher）
        using var lifecycleBridge = bus.ObserveWsLifecycle(wsClient);

        var lifecycleKinds = new List<FeishuLifecycleKind>();
        bus.OnLifecycle(e => lifecycleKinds.Add(e.Kind));

        await wsClient.StartAsync();
        await server.WaitForWsClientAsync(TimeSpan.FromSeconds(5));
        await server.PushMessageEventAsync("事件驱动架构");

        var typed = await typedDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await patternDone.Task.WaitAsync(TimeSpan.FromSeconds(5)); // 与 typed 恢复并发，须各自等齐再断言
        Assert.Equal("om_evt_1", typed.Message?.MessageId);
        lock (patternTypes) Assert.Contains(ImEventTypes.MessageReceiveV1, patternTypes);
        Assert.Contains(FeishuLifecycleKind.WsReady, lifecycleKinds); // 建连成功 → 总线生命周期事件

        await wsClient.ShutdownAsync();
        Assert.Contains(FeishuLifecycleKind.WsDisconnected, lifecycleKinds); // 主动停机 → 生命周期事件
    }
}
