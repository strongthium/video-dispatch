using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VideoDispatch.Core.Model;
using VideoDispatch.Core.Tests.TikTok;
using VideoDispatch.Core.YouTube;

namespace VideoDispatch.Core.Tests.YouTube;

/// <summary>
/// The upload over a real socket to a local server, because a fake handler can't show what goes on the wire, how a
/// real 308 and its Range header come through .NET, or how a dropped connection surfaces. YouTube itself still hasn't
/// been tried.
/// </summary>
public sealed class YouTubeUploaderSocketTests : IAsyncDisposable
{
    private const int Chunk = 256 * 1024;

    private readonly string _folder = Path.Combine(AppContext.BaseDirectory, "uploads-" + Guid.NewGuid().ToString("N"));
    private readonly LocalResumableServer _server = new();
    private readonly MemorySessions _sessions = new();

    public YouTubeUploaderSocketTests() => Directory.CreateDirectory(_folder);

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        Directory.Delete(_folder, recursive: true);
    }

    private YouTubeUploader NewUploader(TimeSpan? stallTimeout = null) => new(
        new HttpClient(new ToLocalServer(_server.Port)) { Timeout = Timeout.InfiniteTimeSpan },
        new FakeTokens(Platform.YouTube),
        _sessions,
        new JumpingTime(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero)))
    {
        ChunkSize = Chunk,
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
            Details = new YouTubePostDetails { Title = "Title", Privacy = YouTubePrivacy.Private, MadeForKids = false },
            CreatedAt = DateTimeOffset.UnixEpoch,
        }, bytes);
    }

    [Fact]
    public async Task Real_308s_and_their_Range_headers_come_through_and_the_file_arrives_whole()
    {
        var size = (2L * Chunk) + 17;
        var (job, bytes) = NewJob(size);

        var receipt = await NewUploader().UploadAsync(job);

        Assert.Equal("video-1", receipt.RemoteId);
        Assert.Equal(bytes, _server.Received());
        Assert.Equal(3, _server.Requests.Count);

        var first = _server.Requests.First();
        Assert.StartsWith("PUT /session HTTP/1.1", first.Head);
        Assert.Contains($"Content-Range: bytes 0-{Chunk - 1}/{size}", first.Head);
        Assert.Contains($"Content-Length: {Chunk}", first.Head);
        Assert.Contains("Content-Type: video/mp4", first.Head);
        Assert.Contains("Authorization: Bearer token-1", first.Head);
        Assert.All(_server.Requests, request => Assert.DoesNotContain("Transfer-Encoding", request.Head, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_status_check_goes_out_as_an_empty_PUT()
    {
        var size = 2L * Chunk;
        var (job, bytes) = NewJob(size);
        _server.Prefill(bytes.AsSpan(0, 5000));
        _sessions.Save(job.Id, "https://www.googleapis.com/session");

        await NewUploader().UploadAsync(job);

        var check = _server.Requests.First();
        Assert.Contains($"Content-Range: bytes */{size}", check.Head);
        Assert.Contains("Content-Length: 0", check.Head);
        Assert.Empty(check.Body);

        Assert.Contains($"Content-Range: bytes 5000-{5000 + Chunk - 1}/{size}", _server.Requests.ElementAt(1).Head);
        Assert.Equal(bytes, _server.Received());
    }

    [Fact]
    public async Task A_connection_dropped_during_a_chunk_resumes_where_YouTube_says()
    {
        var (job, bytes) = NewJob(2L * Chunk);
        _server.DropConnectionOnRequest = 1;

        await NewUploader().UploadAsync(job);

        Assert.Equal(bytes, _server.Received());
        Assert.Contains(_server.Requests, request => request.Head.Contains("bytes */", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_server_that_goes_silent_is_given_up_on_and_the_upload_resumes()
    {
        var (job, bytes) = NewJob(1000);
        _server.GoSilentOnRequest = 0;

        await NewUploader(TimeSpan.FromMilliseconds(300)).UploadAsync(job);

        Assert.Equal(bytes, _server.Received());
    }

    /// <summary>Answers YouTube's session start itself and sends the rest to the local server, as a plain http request.</summary>
    private sealed class ToLocalServer(int port) : DelegatingHandler(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                var started = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                started.Headers.Location = new Uri("https://www.googleapis.com/session");
                return Task.FromResult(started);
            }

            request.RequestUri = new UriBuilder(request.RequestUri!) { Scheme = "http", Host = "127.0.0.1", Port = port }.Uri;
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed record WireRequest(string Head, byte[] Body);

    /// <summary>Reads requests off a socket and answers as YouTube's docs describe.</summary>
    private sealed class LocalResumableServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly MemoryStream _file = new();
        private int _requestsSeen = -1;

        public LocalResumableServer()
        {
            _listener.Start();
            _ = AcceptAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public ConcurrentQueue<WireRequest> Requests { get; } = new();

        public int? DropConnectionOnRequest { get; set; }

        public int? GoSilentOnRequest { get; set; }

        public byte[] Received()
        {
            lock (_file)
            {
                return _file.ToArray();
            }
        }

        public void Prefill(ReadOnlySpan<byte> bytes)
        {
            lock (_file)
            {
                _file.Write(bytes);
            }
        }

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
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(Answer(request)), _stop.Token);
                    }
                }
                catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
                {
                }
            }
        }

        private string Answer(WireRequest request)
        {
            var range = request.Head.Split("\r\n").Single(line => line.StartsWith("Content-Range:", StringComparison.OrdinalIgnoreCase));
            var total = long.Parse(range[(range.LastIndexOf('/') + 1)..]);

            long length;
            lock (_file)
            {
                _file.Write(request.Body);
                length = _file.Length;
            }

            if (length == total)
            {
                const string video = """{"id":"video-1","status":{"privacyStatus":"private"}}""";
                return $"HTTP/1.1 201 Created\r\nContent-Type: application/json\r\nContent-Length: {video.Length}\r\n\r\n{video}";
            }

            var header = length > 0 ? $"Range: bytes=0-{length - 1}\r\n" : "";
            return $"HTTP/1.1 308 Resume Incomplete\r\n{header}Content-Length: 0\r\n\r\n";
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
            var line = text.Split("\r\n").Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            var body = new byte[long.Parse(line["Content-Length:".Length..].Trim())];
            await stream.ReadExactlyAsync(body, _stop.Token);
            return new WireRequest(text, body);
        }
    }
}
