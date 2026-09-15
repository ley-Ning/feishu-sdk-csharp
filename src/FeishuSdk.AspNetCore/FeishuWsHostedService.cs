using Feishu.Ws;
using Microsoft.Extensions.Hosting;

namespace Feishu.AspNetCore;

/// <summary>随主机生命周期启停 WebSocket 长连接。</summary>
public sealed class FeishuWsHostedService(FeishuWsClient client) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => client.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => client.ShutdownAsync();
}
