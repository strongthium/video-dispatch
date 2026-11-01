using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VideoDispatch.Core.Accounts;
using VideoDispatch.Core.Model;

namespace VideoDispatch.Core.Tests.TikTok;

internal sealed record PutRecord(string Session, string? ContentRange, long? ContentLength, string? ContentType, int BodyLength);

/// <summary>Behaves like TikTok's upload service as its docs describe it. Tests can step in on any request.</summary>
internal sealed class FakeTikTok : HttpMessageHandler
{
    private readonly Dictionary<string, MemoryStream> _sessions = [];
    private int _initCount;

    public List<string> InitBodies { get; } = [];
    public List<string> InitAuthorizations { get; } = [];
    public List<PutRecord> Puts { get; } = [];
    public List<string> StatusBodies { get; } = [];
    public List<string> StatusAuthorizations { get; } = [];

    /// <summary>Return a response to replace the normal answer to an init.</summary>
    public Func<int, HttpResponseMessage?>? OnInit { get; set; }

    /// <summary>Return a response to replace the normal answer to a chunk. The number counts every PUT so far, from zero.</summary>
    public Func<int, PutRecord, HttpResponseMessage?>? OnPut { get; set; }

    /// <summary>Answers to status requests, in order. The last one repeats.</summary>
    public Queue<Func<HttpResponseMessage>> Statuses { get; } = new();

    /// <summary>What to report in the Content-Range of a 206, given the bytes received. Null leaves the header out.</summary>
    public Func<long, long, string?> Progress { get; set; } = (received, total) => $"bytes 0-{received - 1}/{total}";

    public byte[]? ReceivedIn(string session) => _sessions.TryGetValue(session, out var stream) ? stream.ToArray() : null;

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Error(HttpStatusCode status, string code, string message = "From the server") =>
        Json(status, JsonSerializer.Serialize(new { error = new { code, message, log_id = "log" } }));

    public static HttpResponseMessage Status(string status, string? failReason = null) =>
        Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { data = new { status, fail_reason = failReason }, error = new { code = "ok" } }));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;

        if (url == "https://open.tiktokapis.com/v2/post/publish/inbox/video/init/")
        {
            InitBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            InitAuthorizations.Add(request.Headers.Authorization!.ToString());
            return OnInit?.Invoke(InitBodies.Count - 1) ?? StartSession();
        }

        if (url == "https://open.tiktokapis.com/v2/post/publish/status/fetch/")
        {
            StatusBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            StatusAuthorizations.Add(request.Headers.Authorization!.ToString());
            return Statuses.Count > 1 ? Statuses.Dequeue()() : Statuses.Count == 1 ? Statuses.Peek()() : Status("SEND_TO_USER_INBOX");
        }

        if (request.Method == HttpMethod.Put && url.StartsWith("https://upload.test/", StringComparison.Ordinal))
        {
            Assert.Null(request.Headers.Authorization);
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var headers = request.Content.Headers;
            var record = new PutRecord(url, headers.ContentRange?.ToString(), headers.ContentLength, headers.ContentType?.MediaType, body.Length);
            Puts.Add(record);

            return OnPut?.Invoke(Puts.Count - 1, record) ?? ReceiveChunk(url, headers.ContentRange!, body);
        }

        throw new InvalidOperationException($"Unexpected request to {url}");
    }

    private HttpResponseMessage StartSession()
    {
        _initCount++;
        var url = $"https://upload.test/session-{_initCount}?signature=very-secret-url";
        _sessions[url] = new MemoryStream();
        return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { data = new { publish_id = $"publish-{_initCount}", upload_url = url }, error = new { code = "ok" } }));
    }

    private HttpResponseMessage ReceiveChunk(string url, ContentRangeHeaderValue range, byte[] body)
    {
        var received = _sessions[url];
        if (range.From != received.Length)
        {
            return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
        }

        received.Write(body);

        if (received.Length == range.Length)
        {
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new ByteArrayContent([]) };
        }

        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([]) };
        if (Progress(received.Length, range.Length!.Value) is { } header)
        {
            response.Content.Headers.TryAddWithoutValidation("Content-Range", header);
        }

        return response;
    }
}

internal sealed class FakeTokens : IAccessTokenSource
{
    private int _issued;

    public List<string> Rejected { get; } = [];
    public Exception? Throw { get; set; }

    public Task<string> GetAccessTokenAsync(Platform platform, CancellationToken cancellationToken = default)
    {
        Assert.Equal(Platform.TikTok, platform);
        return Throw is not null ? Task.FromException<string>(Throw) : Task.FromResult($"token-{_issued + 1}");
    }

    public Task ReportRejectedAsync(Platform platform, string accessToken, CancellationToken cancellationToken = default)
    {
        Rejected.Add(accessToken);
        _issued++;
        return Task.CompletedTask;
    }
}

/// <summary>A clock that jumps ahead instead of waiting, so tests see every delay without taking any time.</summary>
internal sealed class JumpingTime(DateTimeOffset start) : TimeProvider
{
    private readonly object _lock = new();
    private DateTimeOffset _now = start;

    public ConcurrentQueue<TimeSpan> Delays { get; } = new();

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            Delays.Enqueue(dueTime);
            lock (_lock)
            {
                _now += dueTime;
            }

            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }

        return new NoTimer();
    }

    private sealed class NoTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class RecordingProgress : IProgress<long>
{
    public ConcurrentQueue<long> Values { get; } = new();

    public void Report(long value) => Values.Enqueue(value);
}

internal static class JsonText
{
    public static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;
}
