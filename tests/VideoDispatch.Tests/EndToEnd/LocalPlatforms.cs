using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

namespace VideoDispatch.Tests.EndToEnd;

/// <summary>
/// TikTok and Google on one local socket, built from their docs. The app's real requests come here over real
/// connections. Anything they do that the docs don't allow goes in <see cref="Problems"/> and gets refused.
/// </summary>
internal sealed class LocalPlatforms : IAsyncDisposable
{
    public const string TikTokKey = "tiktok-key";
    public const string TikTokSecret = "tiktok-secret-value";
    public const string GoogleId = "google-id";
    public const string GoogleSecret = "google-secret-value";

    private const int YouTubeChunkUnit = 256 * 1024;
    private const string PlaylistScope = "https://www.googleapis.com/auth/youtube.force-ssl";
    private const string UploadScope = "https://www.googleapis.com/auth/youtube.upload";
    private const string ReadOnlyScope = "https://www.googleapis.com/auth/youtube.readonly";

    private static readonly HttpClient Browser = new();

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, SignIn> _codes = [];
    private readonly Dictionary<string, IReadOnlyList<string>> _accessTokens = [];
    private readonly Dictionary<string, IReadOnlyList<string>> _refreshTokens = [];
    private readonly Dictionary<string, TikTokUpload> _tikTok = [];
    private readonly Dictionary<string, YouTubeUpload> _youTube = [];
    private int _next;

    public LocalPlatforms()
    {
        _listener.Start();
        _ = AcceptAsync();
    }

    public ConcurrentQueue<string> Problems { get; } = new();

    /// <summary>Every grant asked for, like <c>TikTok refresh_token</c>.</summary>
    public ConcurrentQueue<string> Grants { get; } = new();

    public ConcurrentQueue<string> Revoked { get; } = new();

    /// <summary>The refresh tokens, access tokens, secrets and session addresses a file on disk must never hold.</summary>
    public ConcurrentQueue<string> Secrets { get; } = new([TikTokSecret, GoogleSecret]);

    /// <summary>The body of each TikTok init, as it arrived.</summary>
    public ConcurrentQueue<string> TikTokInits { get; } = new();

    public ConcurrentQueue<(string Playlist, string Video)> PlaylistItems { get; } = new();

    /// <summary>What each YouTube session was started with.</summary>
    public ConcurrentQueue<string> YouTubeStarts { get; } = new();

    public ConcurrentQueue<long> YouTubeChunkSizes { get; } = new();

    public int YouTubeStatusChecks => Volatile.Read(ref _statusChecks);

    /// <summary>Upload chunks after the first get no answer, as if the line had stalled.</summary>
    public bool StallLaterChunks { get; set; }

    public TaskCompletionSource TikTokStalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource YouTubeStalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The Google project has used up its YouTube quota.</summary>
    public bool OutOfQuota { get; set; }

    /// <summary>The creator finished the post in TikTok.</summary>
    public bool CreatorPosted { get; set; }

    private int _statusChecks;

    /// <summary>What finished arriving on TikTok, in order.</summary>
    public IReadOnlyList<byte[]> TikTokVideos
    {
        get
        {
            lock (_lock)
            {
                return _tikTok.Values.Where(upload => upload.Finished).Select(upload => upload.Bytes.ToArray()).ToList();
            }
        }
    }

    /// <summary>What finished arriving on YouTube.</summary>
    public IReadOnlyList<byte[]> YouTubeVideos
    {
        get
        {
            lock (_lock)
            {
                return _youTube.Values.Where(upload => upload.VideoId is not null).Select(upload => upload.Bytes.ToArray()).ToList();
            }
        }
    }

    /// <summary>The user took the app's access away, on the platform's own site.</summary>
    public void EndAllSignIns()
    {
        lock (_lock)
        {
            _accessTokens.Clear();
            _refreshTokens.Clear();
        }
    }

    public HttpMessageHandler Through(HttpMessageHandler real) => new ToLocal(real, ((IPEndPoint)_listener.LocalEndpoint).Port, Problems);

    /// <summary>Plays the browser: the user reads the sign-in page and approves.</summary>
    public async Task ApproveSignInAsync(Uri page, CancellationToken cancellationToken)
    {
        var query = HttpUtility.ParseQueryString(page.Query);
        var isTikTok = page.Host == "www.tiktok.com";
        if (!isTikTok && page.Host != "accounts.google.com")
        {
            Problems.Enqueue($"Sign-in went to {page.Host}.");
        }

        var key = isTikTok ? query["client_key"] : query["client_id"];
        var scopes = (query["scope"] ?? "").Split(isTikTok ? ',' : ' ', StringSplitOptions.RemoveEmptyEntries);

        Check(key == (isTikTok ? TikTokKey : GoogleId), "The sign-in page was given the wrong client.");
        Check(query["response_type"] == "code", "The sign-in page wasn't asked for a code.");
        Check(query["code_challenge_method"] == "S256", "The sign-in didn't use S256.");
        Check(query["state"] is { Length: > 0 }, "The sign-in had no state.");
        Check(isTikTok || query["access_type"] == "offline", "Google wasn't asked for a refresh token.");
        Check(page.Query.Contains("secret", StringComparison.OrdinalIgnoreCase) == false, "A secret went in the sign-in address.");

        var code = "code-" + Interlocked.Increment(ref _next);
        lock (_lock)
        {
            _codes[code] = new SignIn(isTikTok, query["code_challenge"] ?? "", query["redirect_uri"] ?? "", scopes);
        }

        var address = $"{query["redirect_uri"]}?code={code}&state={Uri.EscapeDataString(query["state"] ?? "")}";
        using var response = await Browser.GetAsync(address, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        _stop.Dispose();
    }

    private static string ValueOf(string head, string name)
    {
        foreach (var line in head.Split("\r\n"))
        {
            if (line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
            {
                return line[(name.Length + 1)..].Trim();
            }
        }

        return "";
    }

    private static Reply Json(string json, int status = 200) => new(status, json);

    private static Reply GoogleError(int status, string reason) =>
        new(status, $$$"""{"error":{"code":{{{status}}},"errors":[{"reason":"{{{reason}}}"}]}}""");

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private void Check(bool isFine, string problem)
    {
        if (!isFine)
        {
            Problems.Enqueue(problem);
        }
    }

    private Reply Refuse(string problem)
    {
        Problems.Enqueue(problem);
        return new Reply(400, "{}");
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                _ = ServeAsync(await _listener.AcceptTcpClientAsync(_stop.Token));
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
                while (await ReadAsync(stream) is { } request)
                {
                    var reply = await AnswerAsync(request);
                    var head = new StringBuilder();
                    head.Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {reply.Status} {(reply.Status == 308 ? "Resume Incomplete" : "Answer")}\r\n");
                    head.Append(CultureInfo.InvariantCulture, $"Content-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(reply.Body)}\r\n");
                    foreach (var (name, value) in reply.Headers ?? [])
                    {
                        head.Append(CultureInfo.InvariantCulture, $"{name}: {value}\r\n");
                    }

                    await stream.WriteAsync(Encoding.UTF8.GetBytes(head + "\r\n" + reply.Body), _stop.Token);
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException or SocketException)
            {
            }
        }
    }

    private async Task<Request?> ReadAsync(NetworkStream stream)
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
        Check(!text.Contains("Transfer-Encoding", StringComparison.OrdinalIgnoreCase), "A request went out without a length.");

        var length = ValueOf(text, "Content-Length");
        var body = new byte[length.Length > 0 ? long.Parse(length, CultureInfo.InvariantCulture) : 0];
        await stream.ReadExactlyAsync(body, _stop.Token);

        var first = text.Split("\r\n")[0].Split(' ');
        var target = new Uri("http://local" + first[1]);
        return new Request(first[0], ValueOf(text, "X-Real-Host"), target.AbsolutePath, HttpUtility.ParseQueryString(target.Query), text, body);
    }

    private async Task<Reply> AnswerAsync(Request request) => (request.Host, request.Method, request.Path) switch
    {
        ("open.tiktokapis.com", "POST", "/v2/oauth/token/") => TikTokToken(request),
        ("open.tiktokapis.com", "POST", "/v2/oauth/revoke/") => TikTokRevoke(request),
        ("open.tiktokapis.com", "GET", "/v2/user/info/") => TikTokUser(request),
        ("open.tiktokapis.com", "POST", "/v2/post/publish/inbox/video/init/") => TikTokInit(request),
        ("open.tiktokapis.com", "POST", "/v2/post/publish/status/fetch/") => TikTokStatus(request),
        ("open-upload.tiktokapis.com", "PUT", "/video/") => await TikTokChunkAsync(request),
        ("oauth2.googleapis.com", "POST", "/token") => GoogleToken(request),
        ("oauth2.googleapis.com", "POST", "/revoke") => GoogleRevoke(request),
        ("www.googleapis.com", "GET", "/youtube/v3/channels") => Channels(request),
        ("www.googleapis.com", "GET", "/youtube/v3/videoCategories") => Categories(request),
        ("www.googleapis.com", "GET", "/youtube/v3/playlists") => Playlists(request),
        ("www.googleapis.com", "POST", "/youtube/v3/playlistItems") => AddToPlaylist(request),
        ("www.googleapis.com", "GET", "/youtube/v3/videos") => VideoStatus(request),
        ("www.googleapis.com", "POST", "/upload/youtube/v3/videos") => StartYouTube(request),
        ("www.googleapis.com", "PUT", "/upload/youtube/v3/videos") => await YouTubeChunkAsync(request),
        _ => Refuse($"Nothing here answers {request.Method} {request.Host}{request.Path}."),
    };

    private Reply TikTokToken(Request request)
    {
        var form = request.Form();
        Check(form["client_key"] == TikTokKey, "TikTok was given the wrong client key.");
        Check(form["client_secret"] == TikTokSecret, "TikTok was given the wrong client secret.");
        return IssueToken(form, isTikTok: true, secret => Hex(SHA256.HashData(Encoding.ASCII.GetBytes(secret))));
    }

    private Reply GoogleToken(Request request)
    {
        var form = request.Form();
        Check(form["client_id"] == GoogleId, "Google was given the wrong client ID.");
        Check(form["client_secret"] == GoogleSecret, "Google was given the wrong client secret.");
        return IssueToken(form, isTikTok: false, secret => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(secret))));
    }

    private Reply IssueToken(System.Collections.Specialized.NameValueCollection form, bool isTikTok, Func<string, string> challengeOf)
    {
        var grant = form["grant_type"] ?? "";
        Grants.Enqueue($"{(isTikTok ? "TikTok" : "YouTube")} {grant}");

        IReadOnlyList<string> scopes;
        lock (_lock)
        {
            if (grant == "authorization_code")
            {
                if (!_codes.Remove(form["code"] ?? "", out var signIn) || signIn.IsTikTok != isTikTok)
                {
                    return Refuse("A sign-in code was unknown, or used twice.");
                }

                Check(form["redirect_uri"] == signIn.RedirectUri, "The token request's redirect address didn't match the sign-in's.");
                Check(challengeOf(form["code_verifier"] ?? "") == signIn.Challenge, "The code verifier didn't match the challenge.");
                scopes = signIn.Scopes;
            }
            else if (grant == "refresh_token" && _refreshTokens.TryGetValue(form["refresh_token"] ?? "", out var kept))
            {
                scopes = kept;
            }
            else
            {
                return Json("""{"error":"invalid_grant","error_description":"The refresh token is no longer valid."}""", 400);
            }

            var number = Interlocked.Increment(ref _next);
            var access = $"access-{number}-issued";
            _accessTokens[access] = scopes;
            Secrets.Enqueue(access);

            // TikTok replaces its refresh token. Google only sends one at sign-in.
            var refresh = grant == "authorization_code" || isTikTok ? $"refresh-{number}-issued" : null;
            if (refresh is not null)
            {
                _refreshTokens[refresh] = scopes;
                Secrets.Enqueue(refresh);
            }

            var refreshField = refresh is null ? "" : $$""","refresh_token":"{{refresh}}" """;
            var scopeText = string.Join(isTikTok ? ',' : ' ', scopes);
            return Json($$"""{"access_token":"{{access}}","expires_in":3600{{refreshField}},"scope":"{{scopeText}}","token_type":"Bearer"}""");
        }
    }

    private Reply TikTokRevoke(Request request)
    {
        var form = request.Form();
        Check(form["client_secret"] == TikTokSecret, "TikTok's revoke was given the wrong secret.");
        Check(_accessTokens.ContainsKey(form["token"] ?? ""), "TikTok's revoke wasn't given an access token it knows.");
        Revoked.Enqueue("TikTok");
        return Json("{}");
    }

    private Reply GoogleRevoke(Request request)
    {
        Check(_refreshTokens.ContainsKey(request.Form()["token"] ?? ""), "Google's revoke wasn't given a refresh token it knows.");
        Revoked.Enqueue("YouTube");
        return Json("{}");
    }

    /// <summary>The scopes of the token a request carries, or null if it has no good one.</summary>
    private IReadOnlyList<string>? ScopesOf(Request request)
    {
        var header = ValueOf(request.Head, "Authorization");
        lock (_lock)
        {
            return header.StartsWith("Bearer ", StringComparison.Ordinal) && _accessTokens.TryGetValue(header["Bearer ".Length..], out var scopes)
                ? scopes
                : null;
        }
    }

    private Reply TikTokUser(Request request)
    {
        Check(request.Query["fields"]?.Contains("display_name", StringComparison.Ordinal) == true, "TikTok wasn't asked for a display name.");
        return ScopesOf(request) is { } scopes && scopes.Contains("user.info.basic")
            ? Json("""{"data":{"user":{"open_id":"open-1","display_name":"Trail Notes"}},"error":{"code":"ok"}}""")
            : Json("""{"error":{"code":"access_token_invalid"}}""", 401);
    }

    private Reply TikTokInit(Request request)
    {
        if (ScopesOf(request) is not { } scopes || !scopes.Contains("video.upload"))
        {
            return Json("""{"error":{"code":"access_token_invalid"}}""", 401);
        }

        var text = Encoding.UTF8.GetString(request.Body);
        TikTokInits.Enqueue(text);

        using var json = JsonDocument.Parse(text);
        var source = json.RootElement.GetProperty("source_info");
        var size = source.GetProperty("video_size").GetInt64();
        var chunk = source.GetProperty("chunk_size").GetInt64();
        var count = source.GetProperty("total_chunk_count").GetInt32();

        Check(source.GetProperty("source").GetString() == "FILE_UPLOAD", "TikTok's init didn't say FILE_UPLOAD.");
        Check(!json.RootElement.TryGetProperty("post_info", out _), "TikTok's inbox init has no post_info.");
        Check(count == Math.Max(1, size / chunk), "TikTok's chunk count isn't the size over the chunk size, rounded down.");

        var id = "publish-" + Interlocked.Increment(ref _next);
        lock (_lock)
        {
            _tikTok[id] = new TikTokUpload(size);
        }

        return Json($$$"""{"data":{"publish_id":"{{{id}}}","upload_url":"https://open-upload.tiktokapis.com/video/?upload_id={{{id}}}"},"error":{"code":"ok"}}""");
    }

    private async Task<Reply> TikTokChunkAsync(Request request)
    {
        Check(ValueOf(request.Head, "Authorization").Length == 0, "The account's token went with a TikTok chunk.");
        Check(ValueOf(request.Head, "Content-Type") == "video/mp4", "A TikTok chunk wasn't marked as video/mp4.");

        var (first, last, total) = RangeOf(request);
        if (first > 0 && StallLaterChunks)
        {
            TikTokStalled.TrySetResult();
            await Task.Delay(Timeout.Infinite, _stop.Token);
        }

        lock (_lock)
        {
            if (!_tikTok.TryGetValue(request.Query["upload_id"] ?? "", out var upload))
            {
                return new Reply(404, "{}");
            }

            Check(upload.Size == total, "A TikTok chunk gave a different total from its init.");
            if (first != upload.Bytes.Length || last - first + 1 != request.Body.Length)
            {
                return new Reply(416, "{}");
            }

            upload.Bytes.Write(request.Body);
            if (upload.Bytes.Length < total)
            {
                return new Reply(206, "{}", [("Content-Range", $"bytes 0-{last}/{total}")]);
            }

            upload.Finished = true;
            return new Reply(201, "{}");
        }
    }

    private Reply TikTokStatus(Request request)
    {
        if (ScopesOf(request) is null)
        {
            return Json("""{"error":{"code":"access_token_invalid"}}""", 401);
        }

        using var json = JsonDocument.Parse(request.Body);
        lock (_lock)
        {
            var finished = _tikTok.TryGetValue(json.RootElement.GetProperty("publish_id").GetString() ?? "", out var upload) && upload.Finished;
            var status = !finished ? "PROCESSING_UPLOAD" : CreatorPosted ? "PUBLISH_COMPLETE" : "SEND_TO_USER_INBOX";
            return Json($$$"""{"data":{"status":"{{{status}}}"},"error":{"code":"ok"}}""");
        }
    }

    private Reply Channels(Request request)
    {
        Check(request.Query["mine"] == "true", "YouTube wasn't asked for this account's channel.");
        return ScopesOf(request) is { } scopes && scopes.Contains(ReadOnlyScope)
            ? Json("""{"items":[{"id":"UC1","snippet":{"title":"Trail Notes Channel"}}]}""")
            : GoogleError(401, "authError");
    }

    private Reply Categories(Request request)
    {
        Check(request.Query["regionCode"]?.Length == 2, "YouTube's categories need a two-letter country.");
        return ScopesOf(request) is { } scopes && scopes.Contains(ReadOnlyScope)
            ? Json("""{"items":[{"id":"19","snippet":{"title":"Travel & Events","assignable":true}},{"id":"18","snippet":{"title":"Short Movies","assignable":false}}]}""")
            : GoogleError(401, "authError");
    }

    private Reply Playlists(Request request)
    {
        if (ScopesOf(request) is not { } scopes || !scopes.Contains(ReadOnlyScope))
        {
            return GoogleError(401, "authError");
        }

        return request.Query["pageToken"] == "page-2"
            ? Json("""{"items":[{"id":"PL2","snippet":{"title":"Summit Days"}}]}""")
            : Json("""{"items":[{"id":"PL1","snippet":{"title":"Morning Walks"}}],"nextPageToken":"page-2"}""");
    }

    private Reply AddToPlaylist(Request request)
    {
        if (ScopesOf(request) is not { } scopes)
        {
            return GoogleError(401, "authError");
        }

        if (!scopes.Contains(PlaylistScope))
        {
            return GoogleError(403, "insufficientPermissions");
        }

        using var json = JsonDocument.Parse(request.Body);
        var snippet = json.RootElement.GetProperty("snippet");
        PlaylistItems.Enqueue((snippet.GetProperty("playlistId").GetString() ?? "", snippet.GetProperty("resourceId").GetProperty("videoId").GetString() ?? ""));
        return Json("{}");
    }

    private Reply VideoStatus(Request request)
    {
        if (ScopesOf(request) is null)
        {
            return GoogleError(401, "authError");
        }

        lock (_lock)
        {
            return _youTube.Values.Any(upload => upload.VideoId == request.Query["id"])
                ? Json("""{"items":[{"status":{"uploadStatus":"processed"}}]}""")
                : Json("""{"items":[]}""");
        }
    }

    private Reply StartYouTube(Request request)
    {
        if (ScopesOf(request) is not { } scopes)
        {
            return GoogleError(401, "authError");
        }

        if (!scopes.Contains(UploadScope))
        {
            return GoogleError(403, "insufficientPermissions");
        }

        if (OutOfQuota)
        {
            return GoogleError(403, "quotaExceeded");
        }

        Check(request.Query["uploadType"] == "resumable", "YouTube's upload wasn't asked to be resumable.");
        Check(ValueOf(request.Head, "X-Upload-Content-Type") == "video/mp4", "YouTube's session wasn't told the video's type.");

        var body = Encoding.UTF8.GetString(request.Body);
        YouTubeStarts.Enqueue(body);

        using var json = JsonDocument.Parse(body);
        Check(json.RootElement.GetProperty("status").TryGetProperty("privacyStatus", out _), "YouTube wasn't told the privacy, so it would be public.");

        var id = "session-" + Interlocked.Increment(ref _next);
        lock (_lock)
        {
            _youTube[id] = new YouTubeUpload(long.Parse(ValueOf(request.Head, "X-Upload-Content-Length"), CultureInfo.InvariantCulture), json.RootElement.GetProperty("status").GetProperty("privacyStatus").GetString() ?? "");
        }

        var address = $"https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&upload_id={id}";
        Secrets.Enqueue(address);
        return new Reply(200, "{}", [("Location", address)]);
    }

    private async Task<Reply> YouTubeChunkAsync(Request request)
    {
        if (ScopesOf(request) is null)
        {
            return GoogleError(401, "authError");
        }

        var range = ValueOf(request.Head, "Content-Range");
        var isCheck = range.StartsWith("bytes */", StringComparison.Ordinal);

        if (!isCheck && RangeOf(request).First > 0 && StallLaterChunks)
        {
            YouTubeStalled.TrySetResult();
            await Task.Delay(Timeout.Infinite, _stop.Token);
        }

        lock (_lock)
        {
            if (!_youTube.TryGetValue(request.Query["upload_id"] ?? "", out var upload))
            {
                return new Reply(404, "{}");
            }

            if (isCheck)
            {
                Interlocked.Increment(ref _statusChecks);
                Check(request.Body.Length == 0, "A YouTube status check carried a body.");
            }
            else if (upload.VideoId is null)
            {
                var (first, last, total) = RangeOf(request);
                Check(total == upload.Size, "A YouTube chunk gave a different total from its session.");
                Check(ValueOf(request.Head, "Content-Type") == "video/mp4", "A YouTube chunk wasn't marked as video/mp4.");
                Check(last - first + 1 == request.Body.Length, "A YouTube chunk's range didn't match its length.");
                Check(last + 1 == total || request.Body.Length % YouTubeChunkUnit == 0, "A YouTube chunk wasn't a multiple of 256 KiB.");

                // Overlapping or skipping bytes means none of the request is taken.
                if (first == upload.Bytes.Length)
                {
                    upload.Bytes.Write(request.Body);
                    YouTubeChunkSizes.Enqueue(request.Body.Length);
                }

                if (upload.Bytes.Length == upload.Size)
                {
                    upload.VideoId = "video-" + Interlocked.Increment(ref _next);
                }
            }

            if (upload.VideoId is { } video)
            {
                return new Reply(201, $$$"""{"id":"{{{video}}}","status":{"privacyStatus":"{{{upload.Privacy}}}"}}""");
            }

            return upload.Bytes.Length > 0
                ? new Reply(308, "", [("Range", $"bytes=0-{upload.Bytes.Length - 1}")])
                : new Reply(308, "");
        }
    }

    private (long First, long Last, long Total) RangeOf(Request request)
    {
        var range = ValueOf(request.Head, "Content-Range");
        var parts = range["bytes ".Length..].Split('-', '/');
        return (long.Parse(parts[0], CultureInfo.InvariantCulture), long.Parse(parts[1], CultureInfo.InvariantCulture), long.Parse(parts[2], CultureInfo.InvariantCulture));
    }

    /// <summary>Sends each request to the local socket as plain http, saying which host it was really for.</summary>
    private sealed class ToLocal(HttpMessageHandler real, int port, ConcurrentQueue<string> problems) : DelegatingHandler(real)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var address = request.RequestUri!;
            if (address.Scheme != Uri.UriSchemeHttps)
            {
                problems.Enqueue($"A request went out without https: {address.Host}.");
            }

            request.Headers.TryAddWithoutValidation("X-Real-Host", address.Host);
            request.RequestUri = new UriBuilder(address) { Scheme = "http", Host = "127.0.0.1", Port = port }.Uri;
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed record SignIn(bool IsTikTok, string Challenge, string RedirectUri, IReadOnlyList<string> Scopes);

    private sealed record Request(string Method, string Host, string Path, System.Collections.Specialized.NameValueCollection Query, string Head, byte[] Body)
    {
        public System.Collections.Specialized.NameValueCollection Form() => HttpUtility.ParseQueryString(Encoding.UTF8.GetString(Body));
    }

    private sealed record Reply(int Status, string Body, IReadOnlyList<(string Name, string Value)>? Headers = null);

    private sealed class TikTokUpload(long size)
    {
        public long Size { get; } = size;

        public MemoryStream Bytes { get; } = new();

        public bool Finished { get; set; }
    }

    private sealed class YouTubeUpload(long size, string privacy)
    {
        public long Size { get; } = size;

        public string Privacy { get; } = privacy;

        public MemoryStream Bytes { get; } = new();

        public string? VideoId { get; set; }
    }
}
