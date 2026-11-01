using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VideoDispatch.Core.Uploads;

namespace VideoDispatch.Core.Tests.YouTube;

internal sealed record StartRecord(string Body, string Authorization, string? UploadLength, string? UploadType, string Url);

internal sealed record PutRecord(string Session, string? ContentRange, long? ContentLength, string? ContentType, int BodyLength, string? Authorization)
{
    public bool IsStatusCheck => BodyLength == 0;
}

/// <summary>Behaves like YouTube's resumable upload as its docs describe it. Tests can step in on any request.</summary>
internal sealed class FakeYouTube : HttpMessageHandler
{
    public const string SessionPrefix = "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&upload_id=";

    private readonly Dictionary<string, MemoryStream> _sessions = [];
    private int _sessionCount;

    public List<StartRecord> Starts { get; } = [];
    public List<PutRecord> Puts { get; } = [];
    public List<string> Requests { get; } = [];
    public List<string> Bodies { get; } = [];

    /// <summary>What the finished video's status says, when a test wants it to differ from what was asked for.</summary>
    public string? PrivacyToReport { get; set; }

    /// <summary>Return a response to replace the normal answer to a session start.</summary>
    public Func<int, HttpResponseMessage?>? OnStart { get; set; }

    /// <summary>Return a response to replace the normal answer to a PUT. The number counts every PUT so far, from zero.</summary>
    public Func<int, PutRecord, HttpResponseMessage?>? OnPut { get; set; }

    /// <summary>Runs after a chunk is taken and before the answer. Throw to lose the answer.</summary>
    public Action<int, PutRecord>? AfterTaking { get; set; }

    /// <summary>How much of a chunk arrives, for a connection that broke part way. Null means all of it.</summary>
    public Func<int, PutRecord, long?>? KeepOnly { get; set; }

    /// <summary>Answers to anything else, by the start of the address. The first match wins.</summary>
    public List<(string Prefix, Func<HttpResponseMessage> Answer)> Other { get; } = [];

    public byte[]? ReceivedIn(string session) => _sessions.TryGetValue(session, out var stream) ? stream.ToArray() : null;

    /// <summary>Makes a session that already holds the start of a file, as one left from before would.</summary>
    public string Seed(byte[] alreadyReceived)
    {
        var url = NewSession();
        _sessions[url].Write(alreadyReceived);
        return url;
    }

    public static HttpResponseMessage Json(HttpStatusCode status, object value) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Error(HttpStatusCode status, string reason, string message = "From the server") =>
        Json(status, new { error = new { errors = new[] { new { domain = "youtube", reason, message } }, code = (int)status, message } });

    public static HttpResponseMessage VideoStatus(string uploadStatus, string? failureReason = null, string? rejectionReason = null) =>
        Json(HttpStatusCode.OK, new { items = new[] { new { id = "video-1", status = new { uploadStatus, failureReason, rejectionReason } } } });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        var body = request.Content is null ? "" : request.Method == HttpMethod.Put ? "" : await request.Content.ReadAsStringAsync(cancellationToken);

        if (request.Method == HttpMethod.Post && url.StartsWith("https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=", StringComparison.Ordinal))
        {
            Starts.Add(new StartRecord(
                body,
                request.Headers.Authorization!.ToString(),
                request.Headers.TryGetValues("X-Upload-Content-Length", out var length) ? length.Single() : null,
                request.Headers.TryGetValues("X-Upload-Content-Type", out var type) ? type.Single() : null,
                url));

            if (OnStart?.Invoke(Starts.Count - 1) is { } answer)
            {
                return answer;
            }

            var started = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            started.Headers.Location = new Uri(NewSession());
            return started;
        }

        if (request.Method == HttpMethod.Put)
        {
            var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var headers = request.Content.Headers;
            var record = new PutRecord(url, headers.ContentRange?.ToString(), headers.ContentLength, headers.ContentType?.MediaType, bytes.Length, request.Headers.Authorization?.ToString());
            Puts.Add(record);
            var number = Puts.Count - 1;

            if (OnPut?.Invoke(number, record) is { } answer)
            {
                return answer;
            }

            var reply = ReceiveChunk(url, headers.ContentRange!, bytes, KeepOnly?.Invoke(number, record));
            AfterTaking?.Invoke(number, record);
            return reply;
        }

        Requests.Add($"{request.Method} {url}");
        Bodies.Add(body);
        foreach (var (prefix, respond) in Other)
        {
            if (url.StartsWith(prefix, StringComparison.Ordinal))
            {
                return respond();
            }
        }

        throw new InvalidOperationException($"Unexpected request to {url}");
    }

    private string NewSession()
    {
        var url = SessionPrefix + ++_sessionCount;
        _sessions[url] = new MemoryStream();
        return url;
    }

    private HttpResponseMessage ReceiveChunk(string url, ContentRangeHeaderValue range, byte[] body, long? keepOnly)
    {
        if (!_sessions.TryGetValue(url, out var received))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (range.HasRange)
        {
            // The docs say overlapping or skipping bytes means none of it uploads.
            if (range.From == received.Length)
            {
                received.Write(body, 0, (int)(keepOnly ?? body.Length));
            }
        }

        if (received.Length == range.Length)
        {
            var privacy = PrivacyToReport ?? "private";
            return Json(HttpStatusCode.Created, new { id = $"video-{url[SessionPrefix.Length..]}", status = new { privacyStatus = privacy, uploadStatus = "uploaded" } });
        }

        var response = new HttpResponseMessage(HttpStatusCode.PermanentRedirect) { Content = new ByteArrayContent([]) };
        if (received.Length > 0)
        {
            response.Headers.TryAddWithoutValidation("Range", $"bytes=0-{received.Length - 1}");
        }

        return response;
    }
}

internal sealed class MemorySessions : IUploadSessionStore
{
    public Dictionary<Guid, string> Saved { get; } = [];
    public List<string> Ever { get; } = [];

    public string? Get(Guid jobId) => Saved.GetValueOrDefault(jobId);

    public void Save(Guid jobId, string sessionUrl)
    {
        Saved[jobId] = sessionUrl;
        Ever.Add(sessionUrl);
    }

    public void Delete(Guid jobId) => Saved.Remove(jobId);
}
