using Feishu.AspNetCore;
using Feishu.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Microsoft.AspNetCore.Builder;

public static class FeishuWebHostExtensions
{
    /// <summary>
    /// 把事件回调端点挂到指定路径（如 /feishu/events）。
    /// 自动处理 URL 验证（challenge）、解密、验签与分发，并回写 EventResponse。
    /// </summary>
    public static IEndpointRouteBuilder MapFeishuEvents(this IEndpointRouteBuilder endpoints, string pattern, EventDispatcher dispatcher) =>
        MapFeishuWebhook(endpoints, pattern, dispatcher);

    /// <summary>通用 webhook 端点（事件分发器或卡片处理器均可挂载）。</summary>
    public static IEndpointRouteBuilder MapFeishuWebhook(this IEndpointRouteBuilder endpoints, string pattern, IWebhookHandler handler)
    {
        endpoints.MapPost(pattern, (HttpContext context) => FeishuWebhookEndpoint.HandleAsync(context, handler));
        return endpoints;
    }
}
