using Feishu.Events;
using Microsoft.AspNetCore.Http;

namespace Feishu.AspNetCore;

/// <summary>webhook 端点核心处理（事件与卡片共用），可直接在测试中用 DefaultHttpContext 调用。</summary>
public static class FeishuWebhookEndpoint
{
    public static async Task HandleAsync(HttpContext context, IWebhookHandler handler)
    {
        using var reader = new StreamReader(context.Request.Body);
        var body = System.Text.Encoding.UTF8.GetBytes(await reader.ReadToEndAsync());

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in context.Request.Headers)
            headers[k] = v.ToString();

        var request = new EventRequest
        {
            Headers = headers,
            Body = body,
            Path = context.Request.Path,
        };
        var response = await handler.HandleAsync(request, context.RequestAborted);

        context.Response.StatusCode = response.StatusCode;
        context.Response.ContentType = response.ContentType;
        await context.Response.Body.WriteAsync(response.Body, context.RequestAborted);
    }
}

