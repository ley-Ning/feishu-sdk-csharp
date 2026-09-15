using Feishu;
using System.Net;
using System.Text;

namespace FeishuSdk.Tests;

/// <summary>可编程的 HttpMessageHandler：按路径+计数路由响应，并记录全部请求。</summary>
public sealed class FakeHandler : HttpMessageHandler
{
    private readonly Func<RequestLog, Task<HttpResponseMessage>> _responder;

    public List<RequestLog> Requests { get; } = new();

    public FakeHandler(Func<RequestLog, Task<HttpResponseMessage>> responder) => _responder = responder;

    public static HttpResponseMessage Json(int statusCode, string body) => new((HttpStatusCode)statusCode)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content == null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in request.Headers)
            headers[k] = string.Join(",", v);
        var log = new RequestLog(
            request.Method,
            request.RequestUri?.PathAndQuery ?? "",
            headers,
            body,
            request.RequestUri?.ToString() ?? "");
        Requests.Add(log);
        var response = await _responder(log);
        response.Headers.TryAddWithoutValidation("X-Tt-Logid", $"log-{Requests.Count}");
        return response;
    }
}

public sealed record RequestLog(
    HttpMethod Method,
    string PathAndQuery,
    IReadOnlyDictionary<string, string> Headers,
    byte[]? Body,
    string Url = "")
{
    public string BodyText => Body == null ? "" : Encoding.UTF8.GetString(Body);
}

public static class FeishuTestHarness
{
    /// <summary>构建一个使用 FakeHandler 的 FeishuClient。</summary>
    public static (FeishuClient Client, FakeHandler Handler) CreateClient(
        Func<RequestLog, Task<HttpResponseMessage>> responder,
        Action<FeishuOptions>? configure = null)
    {
        var handler = new FakeHandler(responder);
        var options = new FeishuOptions
        {
            AppId = "cli_test",
            AppSecret = "secret_test",
            Logger = NullFeishuLogger.Instance,
            HttpMessageHandlerFactory = () => handler,
        };
        configure?.Invoke(options);
        return (new FeishuClient(options), handler);
    }
}
