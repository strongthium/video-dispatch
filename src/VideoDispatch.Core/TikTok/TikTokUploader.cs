using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using VideoDispatch.Core.Accounts;
using VideoDispatch.Core.Model;
using VideoDispatch.Core.Uploads;

namespace VideoDispatch.Core.TikTok;

/// <summary>
/// Sends a video to the creator's TikTok inbox, where they finish the post. TikTok documents no way to resume an
/// upload, so a file that can't continue starts again under a new init, and only once: each init may count against
/// the creator's limit of pending shares.
/// </summary>
public sealed class TikTokUploader : IVideoUploader
{
    private const string InitEndpoint = "https://open.tiktokapis.com/v2/post/publish/inbox/video/init/";
    private const string StatusEndpoint = "https://open.tiktokapis.com/v2/post/publish/status/fetch/";

    private const int MaxSessions = 2;
    private const int MaxChunkRetries = 3;
    private const int MaxServerMessageLength = 300;

    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FirstChunkRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LongestChunkRetryWait = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FirstPollDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LongestPollDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PollLimit = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DefaultRateLimitWait = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PostingCapWait = TimeSpan.FromHours(24);

    private readonly HttpClient _http;
    private readonly IAccessTokenSource _tokens;
    private readonly TimeProvider _time;
    private readonly SlidingWindowRateLimiter _initLimit;
    private readonly SlidingWindowRateLimiter _statusLimit;

    /// <param name="http">Must have no timeout of its own: a chunk on a slow line can take far longer than the default.</param>
    /// <param name="tokens">Hands out the account's access tokens.</param>
    /// <param name="time">The clock used for waits and limits.</param>
    public TikTokUploader(HttpClient http, IAccessTokenSource tokens, TimeProvider time)
    {
        if (http.Timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentException("The HttpClient must have an infinite timeout.", nameof(http));
        }

        _http = http;
        _tokens = tokens;
        _time = time;
        _initLimit = new SlidingWindowRateLimiter(6, TimeSpan.FromMinutes(1), time);
        _statusLimit = new SlidingWindowRateLimiter(30, TimeSpan.FromMinutes(1), time);
    }

    public Platform Platform => Platform.TikTok;

    /// <summary>A chunk that sends nothing and hears nothing for this long counts as getting no response.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public async Task<UploadReceipt> UploadAsync(UploadJob job, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        if (job.Platform != Platform)
        {
            throw new ArgumentException("This job isn't for TikTok.", nameof(job));
        }

        var contentType = TikTokVideoLimits.ContentTypeFor(job.PublishPath)
            ?? throw Failure(UploadFailureKind.Permanent, "TikTok takes MP4, MOV and WebM files.");

        using var file = SourceFile.OpenUnchanged(job);
        var size = RandomAccess.GetLength(file);
        if (!TikTokChunkPlan.CanUpload(size))
        {
            throw Failure(UploadFailureKind.Permanent, size == 0 ? "The file is empty." : "TikTok takes videos up to 4 GB.");
        }

        var plan = TikTokChunkPlan.For(size);
        for (var session = 1; ; session++)
        {
            var (publishId, uploadUrl) = await InitAsync(plan, cancellationToken);
            progress?.Report(0);

            var whyNot = await SendChunksAsync(file, plan, uploadUrl, contentType, progress, cancellationToken);
            if (whyNot is null)
            {
                return new UploadReceipt(publishId);
            }

            if (session == MaxSessions)
            {
                throw Failure(UploadFailureKind.Retryable, whyNot);
            }
        }
    }

    public async Task<JobState> WaitForResultAsync(string remoteId, CancellationToken cancellationToken = default)
    {
        var giveUpAt = _time.GetUtcNow() + PollLimit;
        var delay = FirstPollDelay;

        while (true)
        {
            await _statusLimit.WaitAsync(cancellationToken);

            TimeSpan? wait = null;
            try
            {
                var state = await FetchStateAsync(remoteId, cancellationToken);
                if (state != JobState.Processing)
                {
                    return state;
                }
            }
            catch (TryLaterException exception)
            {
                wait = exception.After;
            }

            // Waiting past the limit would hold up the platform's queue, so a long wait goes back to the caller too.
            if (_time.GetUtcNow() + (wait ?? TimeSpan.Zero) >= giveUpAt)
            {
                return JobState.Processing;
            }

            await Task.Delay(wait ?? delay, _time, cancellationToken);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, LongestPollDelay.Ticks));
        }
    }

    private static bool IsServerTrouble(HttpStatusCode status) =>
        (int)status >= 500 || status == HttpStatusCode.TooManyRequests;

    private static string Shorten(string text) =>
        text.Length > MaxServerMessageLength ? text[..MaxServerMessageLength] : text;

    private static string? FindString(JsonElement? root, string parent, string name) =>
        root is { ValueKind: JsonValueKind.Object } json && json.TryGetProperty(parent, out var section)
            ? OAuthHttp.GetString(section, name)
            : null;

    private UploadException Failure(UploadFailureKind kind, string message, DateTimeOffset? retryAt = null) =>
        new(Platform, kind, message, retryAt);

    private async Task<(string PublishId, Uri UploadUrl)> InitAsync(TikTokChunkPlan plan, CancellationToken cancellationToken)
    {
        await _initLimit.WaitAsync(cancellationToken);

        // Inbox mode has no caption field. The creator adds the caption in TikTok.
        var body = JsonSerializer.Serialize(new
        {
            source_info = new
            {
                source = "FILE_UPLOAD",
                video_size = plan.VideoSize,
                chunk_size = plan.ChunkSize,
                total_chunk_count = plan.ChunkCount,
            },
        });

        ApiReply reply;
        try
        {
            reply = await PostJsonAsync(InitEndpoint, body, cancellationToken);
        }
        catch (TryLaterException exception)
        {
            throw Failure(UploadFailureKind.Retryable, exception.Message, exception.After is { } after ? _time.GetUtcNow() + after : null);
        }

        if (reply.ErrorCode is not (null or "ok"))
        {
            throw reply.ErrorCode switch
            {
                "spam_risk_too_many_pending_share" => Failure(
                    UploadFailureKind.PlatformCap,
                    "TikTok allows at most 5 pending shares in 24 hours. This video will wait.",
                    _time.GetUtcNow() + PostingCapWait),
                "spam_risk_user_banned_from_posting" => Failure(UploadFailureKind.Permanent, "TikTok has banned this account from making new posts."),
                _ => Failure(UploadFailureKind.Permanent, $"TikTok refused the upload ({Shorten(reply.ErrorCode)}){(reply.ErrorMessage is { } message ? ": " + Shorten(message) : "")}"),
            };
        }

        if (FindString(reply.Root, "data", "publish_id") is not { Length: > 0 } publishId ||
            !Uri.TryCreate(FindString(reply.Root, "data", "upload_url"), UriKind.Absolute, out var uploadUrl) ||
            uploadUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw Failure(UploadFailureKind.Retryable, "TikTok sent an unexpected reply to the upload request.");
        }

        return (publishId, uploadUrl);
    }

    /// <summary>Returns null once TikTok has the whole file, or why this upload has to start again.</summary>
    private async Task<string?> SendChunksAsync(
        SafeFileHandle file,
        TikTokChunkPlan plan,
        Uri uploadUrl,
        string contentType,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        for (var chunk = 0; chunk < plan.ChunkCount; chunk++)
        {
            var outcome = await SendChunkAsync(file, plan, chunk, uploadUrl, contentType, progress, cancellationToken);
            if (outcome.StartAgainBecause is not null)
            {
                return outcome.StartAgainBecause;
            }

            if (outcome.Finished)
            {
                return null;
            }
        }

        throw Failure(UploadFailureKind.Retryable, "TikTok didn't confirm the upload finished.");
    }

    private async Task<ChunkOutcome> SendChunkAsync(
        SafeFileHandle file,
        TikTokChunkPlan plan,
        int chunk,
        Uri uploadUrl,
        string contentType,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        var (first, last) = plan.RangeOf(chunk);

        for (var attempt = 0; ; attempt++)
        {
            TimeSpan? retryAfter = null;
            string trouble;

            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stall.CancelAfter(StallTimeout);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
                {
                    Content = new FileChunkContent(file, first, last - first + 1, contentType, sent =>
                    {
                        stall.CancelAfter(StallTimeout);
                        progress?.Report(first + sent);
                    }),
                };
                request.Content.Headers.ContentRange = new ContentRangeHeaderValue(first, last, plan.VideoSize);

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);

                switch (response.StatusCode)
                {
                    case HttpStatusCode.Created:
                        return new ChunkOutcome(true, null);

                    case HttpStatusCode.PartialContent:
                        // Only the final chunk should finish the file, so more wanted after it means the bytes aren't all there.
                        if (chunk == plan.ChunkCount - 1)
                        {
                            return new ChunkOutcome(false, "TikTok didn't confirm the upload finished.");
                        }

                        // The header's last number is either a byte position or a count, depending on how the docs are read.
                        // Both are at least the chunk's last byte if TikTok has it all.
                        return response.Content.Headers.ContentRange?.To < last
                            ? new ChunkOutcome(false, "TikTok lost track of the upload.")
                            : new ChunkOutcome(false, null);

                    case HttpStatusCode.Forbidden:
                        return new ChunkOutcome(false, "TikTok's upload link ran out. It lasts one hour, so a very slow upload may not fit.");

                    case HttpStatusCode.NotFound:
                        return new ChunkOutcome(false, "TikTok no longer knows this upload.");

                    case HttpStatusCode.RequestedRangeNotSatisfiable:
                        return new ChunkOutcome(false, "TikTok's record of the upload doesn't match what was sent.");
                }

                if (!IsServerTrouble(response.StatusCode))
                {
                    throw Failure(UploadFailureKind.Permanent, $"TikTok refused part of the upload (HTTP {(int)response.StatusCode}).");
                }

                trouble = $"TikTok couldn't take part of the upload (HTTP {(int)response.StatusCode}).";
                retryAfter = RetryDelayFrom(response);
            }
            catch (HttpRequestException)
            {
                trouble = "The connection to TikTok failed during the upload.";
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                trouble = "TikTok stopped responding during the upload.";
            }
            catch (IOException)
            {
                trouble = "The connection to TikTok failed during the upload.";
            }

            if (retryAfter > LongestChunkRetryWait)
            {
                throw Failure(UploadFailureKind.Retryable, trouble, _time.GetUtcNow() + retryAfter);
            }

            if (attempt == MaxChunkRetries)
            {
                throw Failure(UploadFailureKind.Retryable, trouble);
            }

            await Task.Delay(retryAfter ?? FirstChunkRetryDelay * Math.Pow(2, attempt), _time, cancellationToken);
        }
    }

    private async Task<JobState> FetchStateAsync(string remoteId, CancellationToken cancellationToken)
    {
        var reply = await PostJsonAsync(StatusEndpoint, JsonSerializer.Serialize(new { publish_id = remoteId }), cancellationToken);

        if (reply.ErrorCode is not (null or "ok"))
        {
            throw Failure(UploadFailureKind.Permanent, $"TikTok couldn't report on the upload ({Shorten(reply.ErrorCode)}).");
        }

        return FindString(reply.Root, "data", "status") switch
        {
            "SEND_TO_USER_INBOX" => JobState.AwaitingCreator,
            "PUBLISH_COMPLETE" => JobState.Completed,
            "FAILED" => throw FailureFrom(FindString(reply.Root, "data", "fail_reason")),
            _ => JobState.Processing,
        };
    }

    private UploadException FailureFrom(string? failReason) => failReason switch
    {
        "internal" => Failure(UploadFailureKind.Retryable, "TikTok had a problem processing the video."),
        "spam_risk_too_many_posts" => Failure(
            UploadFailureKind.PlatformCap,
            "This account reached TikTok's limit on posts in 24 hours. This video will wait.",
            _time.GetUtcNow() + PostingCapWait),
        "file_format_check_failed" => Failure(UploadFailureKind.Permanent, "TikTok doesn't support this video's format."),
        "duration_check_failed" => Failure(UploadFailureKind.Permanent, "This video's length is outside TikTok's limits."),
        "frame_rate_check_failed" => Failure(UploadFailureKind.Permanent, "TikTok doesn't support this video's frame rate."),
        "picture_size_check_failed" => Failure(UploadFailureKind.Permanent, "TikTok doesn't support this video's picture size."),
        "auth_removed" => Failure(UploadFailureKind.Permanent, "The creator removed this app's access to TikTok during processing."),
        "spam_risk_user_banned_from_posting" => Failure(UploadFailureKind.Permanent, "TikTok has banned this account from making new posts."),
        "spam_risk" or "spam_risk_text" => Failure(UploadFailureKind.Permanent, "TikTok flagged this upload as risky."),
        null => Failure(UploadFailureKind.Permanent, "TikTok couldn't process the video, and didn't say why."),
        _ => Failure(UploadFailureKind.Permanent, $"TikTok couldn't process the video ({Shorten(failReason)})."),
    };

    /// <summary>Sends a request with the account's token. A refused token is replaced once.</summary>
    /// <exception cref="TryLaterException">A network or server problem, or a rate limit.</exception>
    private async Task<ApiReply> PostJsonAsync(string url, string body, CancellationToken cancellationToken)
    {
        var replaced = false;

        while (true)
        {
            var token = await GetTokenAsync(cancellationToken);
            var (status, retryAfter, text) = await SendJsonAsync(url, body, token, cancellationToken);
            var root = OAuthHttp.TryParse(text);
            var code = FindString(root, "error", "code");

            if (status == HttpStatusCode.Unauthorized)
            {
                if (replaced || code == "scope_not_authorized")
                {
                    throw Failure(UploadFailureKind.Reconnect, "TikTok refused this account's sign-in. Connect the account again.");
                }

                replaced = true;
                await _tokens.ReportRejectedAsync(Platform, token, cancellationToken);
                continue;
            }

            if (status == HttpStatusCode.TooManyRequests)
            {
                throw new TryLaterException("TikTok is limiting requests. Trying again shortly.", retryAfter ?? DefaultRateLimitWait);
            }

            if (IsServerTrouble(status))
            {
                throw new TryLaterException("TikTok is temporarily unavailable.", retryAfter);
            }

            return new ApiReply(root, code, FindString(root, "error", "message"));
        }
    }

    private async Task<(HttpStatusCode Status, TimeSpan? RetryAfter, string Text)> SendJsonAsync(
        string url,
        string body,
        string token,
        CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(ApiTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await _http.SendAsync(request, limit.Token);
            return (response.StatusCode, RetryDelayFrom(response), await response.Content.ReadAsStringAsync(limit.Token));
        }
        catch (HttpRequestException)
        {
            throw new TryLaterException("Couldn't reach TikTok.", null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TryLaterException("TikTok took too long to respond.", null);
        }
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _tokens.GetAccessTokenAsync(Platform, cancellationToken);
        }
        catch (Exception exception) when (exception is ReconnectRequiredException or AccountSetupException)
        {
            throw Failure(UploadFailureKind.Reconnect, exception.Message);
        }
        catch (TransientAccountException exception)
        {
            throw new TryLaterException(exception.Message, null);
        }
    }

    private TimeSpan? RetryDelayFrom(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is not { } retryAfter)
        {
            return null;
        }

        var delay = retryAfter.Delta ?? retryAfter.Date - _time.GetUtcNow();
        return delay > TimeSpan.Zero ? delay : null;
    }

    /// <param name="Finished">TikTok says it has the whole file.</param>
    /// <param name="StartAgainBecause">Set when this upload can't go on and has to start again.</param>
    private readonly record struct ChunkOutcome(bool Finished, string? StartAgainBecause);

    private sealed record ApiReply(JsonElement? Root, string? ErrorCode, string? ErrorMessage);

    /// <summary>Nothing is known to be wrong with the upload itself, so it's a matter of asking again.</summary>
    private sealed class TryLaterException(string message, TimeSpan? after) : Exception(message)
    {
        public TimeSpan? After { get; } = after;
    }
}
