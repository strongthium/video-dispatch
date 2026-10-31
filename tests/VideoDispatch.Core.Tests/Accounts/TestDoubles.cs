using System.Collections.Concurrent;
using System.Net;
using System.Text;
using VideoDispatch.Core.Accounts.Secrets;

namespace VideoDispatch.Core.Tests.Accounts;

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Body, string? Authorization)
{
    public IReadOnlyDictionary<string, string> Form => QueryParser.Parse(Body);
}

internal static class QueryParser
{
    public static Dictionary<string, string> Parse(string query) =>
        query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0]),
                parts => parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : "");
}

internal sealed class FakeHttpHandler(Func<RecordedRequest, HttpResponseMessage> respond) : HttpMessageHandler
{
    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(request.Method, request.RequestUri!, body, request.Headers.Authorization?.ToString());
        Requests.Enqueue(recorded);

        // Let concurrent callers pile up, so tests can show they share one request.
        await Task.Delay(10, cancellationToken);
        return respond(recorded);
    }
}

internal sealed class InMemorySecretStore : ISecretStore
{
    public ConcurrentDictionary<string, string> Values { get; } = new();

    public string? Get(string key) => Values.GetValueOrDefault(key);

    public void Set(string key, string value) => Values[key] = value;

    public void Delete(string key) => Values.TryRemove(key, out _);
}

internal sealed class ManualTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
