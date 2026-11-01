using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VideoDispatch.Core.Model;
using VideoDispatch.Core.TikTok;
using VideoDispatch.Core.Uploads;

namespace VideoDispatch.Core.Tests.TikTok;

/// <summary>
/// The chunk uploads over a real socket to a local server, because a fake handler can't show what goes on the wire
/// or how a dropped connection really surfaces. TikTok itself still hasn't been tried.
/// </summary>
public sealed class TikTokUploaderSocketTests : IAsyncDisposable
{
    private const long Chunk = TikTokChunkPlan.StandardChunkSize;

    private readonly string _folder = Path.Combine(AppContext.BaseDirectory, "uploads-" + Guid.NewGuid().ToString("N"));
    private readonly LocalUploadServer _server = new();

    public TikTokUploaderSocketTests() => Directory.CreateDirectory(_folder);

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        Directory.Delete(_folder, recursive: true);
    }

    private TikTokUploader NewUploader(TimeSpan? stallTimeout = null) => new(
        new HttpClient(new ToLocalServer(_server.Port)) { Timeout = Timeout.InfiniteTimeSpan },
        new FakeTokens(),
        new JumpingTime(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero)))
    {
        StallTimeout = stallTimeout ?? TimeSpan.FromMinutes(1),
    };

    private (UploadJob Job, byte[] Bytes) NewJob(long size)
    {
        var path = Path.Combine(_folder, "clip.mp4");
        var bytes = new byte[size];
        new Random(42).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);

        return (new UploadJob
        {
            Id = Guid.NewGuid(),
            BatchId = Guid.NewGuid(),
            SourcePath = path,
            SourceLength = size,
            SourceLastWriteUtc = File.GetLastWriteTimeUtc(path),
            Details = new TikTokPostDetails(),
            CreatedAt = DateTimeOffset.UnixEpoch,
        }, bytes);
    }

    [Fact]
    public async Task Sends_exactly_what_TikTok_asks_for_on_the_wire()
    {
        var size = 2 * Chunk + 17;
        var (job, bytes) = NewJob(size);

        await NewUploader().UploadAsync(job);

        Assert.Equal(2, _server.Requests.Count);
        var first = _server.Requests.First();
        var second = _server.Requests.Last();

        Assert.StartsWith("PUT /session?signature=abc HTTP/1.1", first.Head);
        Assert.Contains($"Content-Range: bytes 0-{Chunk - 1}/{size}", first.Head);
        Assert.Contains($"Content-Length: {Chunk}", first.Head);
        Assert.Contains("Content-Type: video/mp4", first.Head);

        Assert.Contains($"Content-Range: bytes {Chunk}-{size - 1}/{size}", second.Head);
        Assert.Contains($"Content-Length: {Chunk + 17}", second.Head);

        // The address is what authorizes a chunk, so the account's token has no business going there.
        Assert.All(_server.Requests, request =>
        {
            Assert.DoesNotContain("Authorization", request.Head, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Transfer-Encoding", request.Head, StringComparison.OrdinalIgnoreCase);
        });

        Assert.Equal(bytes, _server.Received());
    }

    [Fact]
    public async Task A_connection_dropped_during_a_chunk_retries_that_chunk()
    {
        var (job, bytes) = NewJob(2 * Chunk);
        _server.DropConnectionOnRequest = 1;

        await NewUploader().UploadAsync(job);

        Assert.Equal(bytes, _server.Received());
    }

    [Fact]
    public async Task A_server_that_goes_silent_is_given_up_on_and_the_chunk_retried()
    {
        var (job, bytes) = NewJob(1000);
        _server.GoSilentOnRequest = 0;

        await NewUploader(TimeSpan.FromMilliseconds(300)).UploadAsync(job);

        Assert.Equal(bytes, _server.Received());
    }

    [Fact]
    public async Task A_cancel_during_a_chunk_stops_at_once()
    {
        var (job, _) = NewJob(1000);
        _server.GoSilentOnRequest = 0;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NewUploader().UploadAsync(job, null, cancel.Token));

        Assert.Empty(_server.Requests);
    }

    /// <summary>Answers TikTok's API itself and sends the chunks to the local server, as a plain http request.</summary>
    private sealed class ToLocalServer(int port) : DelegatingHandler(new SocketsHttpHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "open.tiktokapis.com")
            {
                var reply = JsonSerializer.Serialize(new
                {
                    data = new { publish_id = "publish", upload_url = "https://upload.test/session?signature=abc" },
                    error = new { code = "ok" },
                });
                return Task.FromResult(FakeTikTok.Json(HttpStatusCode.OK, reply));
            }

            request.RequestUri = new UriBuilder(request.RequestUri) { Scheme = "http", Host = "127.0.0.1", Port = port }.Uri;
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed record WireRequest(string Head, long From, byte[] Body);

    /// <summary>Reads chunks off a socket and answers as TikTok's docs describe.</summary>
    private sealed class LocalUploadServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private int _requestsSeen = -1;

        public LocalUploadServer()
        {
            _listener.Start();
            _ = AcceptAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public ConcurrentQueue<WireRequest> Requests { get; } = new();

        public int? DropConnectionOnRequest { get; set; }

        public int? GoSilentOnRequest { get; set; }

        public byte[] Received() => Requests.OrderBy(request => request.From).SelectMany(request => request.Body).ToArray();

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            _stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = ServeAsync(client);
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    while (await ReadRequestAsync(stream) is { } request)
                    {
                        var number = Interlocked.Increment(ref _requestsSeen);

                        if (number == DropConnectionOnRequest)
                        {
                            client.Client.LingerState = new LingerOption(true, 0);
                            return;
                        }

                        if (number == GoSilentOnRequest)
                        {
                            await Task.Delay(Timeout.Infinite, _stop.Token);
                        }

                        Requests.Enqueue(request);
                        var isLast = LastByte(request.Head) + 1 == Total(request.Head);
                        var answer = isLast
                            ? "HTTP/1.1 201 Created\r\nContent-Length: 0\r\n\r\n"
                            : $"HTTP/1.1 206 Partial Content\r\nContent-Range: bytes 0-{LastByte(request.Head)}/{Total(request.Head)}\r\nContent-Length: 0\r\n\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(answer), _stop.Token);
                    }
                }
                catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
                {
                }
            }
        }

        private async Task<WireRequest?> ReadRequestAsync(NetworkStream stream)
        {
            var head = new StringBuilder();
            var one = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(one, _stop.Token) == 0)
                {
                    return null;
                }

                head.Append((char)one[0]);
            }

            var text = head.ToString();
            var body = new byte[Header(text, "Content-Length")];
            await stream.ReadExactlyAsync(body, _stop.Token);
            return new WireRequest(text, FirstByte(text), body);
        }

        private static long Header(string head, string name)
        {
            var line = head.Split("\r\n").Single(line => line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase));
            return long.Parse(line[(name.Length + 1)..].Trim());
        }

        private static string[] RangeParts(string head)
        {
            var line = head.Split("\r\n").Single(line => line.StartsWith("Content-Range:", StringComparison.OrdinalIgnoreCase));
            return line["Content-Range: bytes ".Length..].Split('-', '/');
        }

        private static long FirstByte(string head) => long.Parse(RangeParts(head)[0]);

        private static long LastByte(string head) => long.Parse(RangeParts(head)[1]);

        private static long Total(string head) => long.Parse(RangeParts(head)[2]);
    }
}
