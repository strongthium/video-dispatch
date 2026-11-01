using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using VideoDispatch.Core.Accounts;
using VideoDispatch.Core.Model;
using VideoDispatch.Core.Uploads;

namespace VideoDispatch.Core.YouTube;

/// <summary>
/// Sends a video with YouTube's resumable upload. The session is saved as soon as it exists, so an upload interrupted
/// at any point, even by the app closing, carries on from what YouTube says it has, and can't make a second copy.
/// </summary>
public sealed class YouTubeUploader : IVideoUploader
{
    /// <summary>YouTube's API rejects a video with no category. This is "People &amp; Blogs".</summary>
    public const string DefaultCategoryId = "22";

    private const string SessionEndpoint = "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet%2Cstatus";
    private const string VideosEndpoint = "https://www.googleapis.com/youtube/v3/videos?part=status&id=";

    private const int ChunkUnit = 256 * 1024;
    private const int MaxSessions = 2;
    private const int MaxRetries = 5;

    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LongestRetryWait = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FirstPollDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LongestPollDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PollLimit = TimeSpan.FromMinutes(30);

    private static readonly JsonSerializerOptions BodyOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly YouTubeApi _api;
    private readonly IUploadSessionStore _sessions;
    private readonly TimeProvider _time;
    private readonly int _chunkSize = 64 * ChunkUnit;

    /// <param name="http">
    /// Must have no timeout of its own, and shouldn't follow redirects: YouTube's "keep going" answer is a 308.
    /// </param>
    /// <param name="tokens">Hands out the account's access tokens.</param>
    /// <param name="sessions">Keeps upload sessions, so they can be resumed.</param>
    /// <param name="time">The clock used for waits and limits.</param>
    public YouTubeUploader(HttpClient http, IAccessTokenSource tokens, IUploadSessionStore sessions, TimeProvider time)
    {
        if (http.Timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentException("The HttpClient must have an infinite timeout.", nameof(http));
        }

        _http = http;
        _api = new YouTubeApi(http, tokens, time);
        _sessions = sessions;
        _time = time;
    }

    public Platform Platform => Platform.YouTube;

    /// <summary>A chunk that sends nothing and hears nothing for this long counts as getting no response.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>YouTube requires a multiple of 256 KiB.</summary>
    public int ChunkSize
    {
        get => _chunkSize;
        init
        {
            if (value <= 0 || value % ChunkUnit != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "A chunk must be a multiple of 256 KiB.");
            }

            _chunkSize = value;
        }
    }

    public async Task<UploadReceipt> UploadAsync(UploadJob job, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        if (job.Details is not YouTubePostDetails details)
        {
            throw new ArgumentException("This job isn't for YouTube.", nameof(job));
        }

        using var file = SourceFile.OpenUnchanged(job);
        var size = RandomAccess.GetLength(file);
        if (YouTubeVideoLimits.Check(size).FirstOrDefault(finding => finding.Blocks) is { } tooBig)
        {
            throw Failure(UploadFailureKind.Permanent, tooBig.Message);
        }

        var upload = new Upload(job, details, file, size, YouTubeVideoLimits.ContentTypeFor(job.PublishPath), progress);
        var saved = Uri.TryCreate(_sessions.Get(job.Id), UriKind.Absolute, out var savedUri) && IsYouTubeAddress(savedUri)
            ? savedUri
            : null;

        for (var session = 1; ; session++)
        {
            var address = saved ?? await StartSessionAsync(upload, cancellationToken);
            if (saved is null)
            {
                _sessions.Save(job.Id, address.AbsoluteUri);
                progress?.Report(0);
            }

            var receipt = await RunSessionAsync(upload, address, resuming: saved is not null, cancellationToken);
            _sessions.Delete(job.Id);
            if (receipt is not null)
            {
                return receipt;
            }

            saved = null;
            if (session == MaxSessions)
            {
                throw Failure(UploadFailureKind.Retryable, "YouTube's upload session ended before the file was sent.");
            }
        }
    }

    public async Task<JobState> WaitForResultAsync(string remoteId, CancellationToken cancellationToken = default)
    {
        var giveUpAt = _time.GetUtcNow() + PollLimit;
        var delay = FirstPollDelay;

        while (true)
        {
            TimeSpan? wait = null;
            JsonElement? answer = null;
            try
            {
                var reply = await _api.SendAsync(HttpMethod.Get, VideosEndpoint + Uri.EscapeDataString(remoteId), null, cancellationToken);
                answer = reply.Root;
            }
            catch (UploadException exception) when (exception.Kind is UploadFailureKind.Retryable or UploadFailureKind.PlatformCap)
            {
                wait = exception.RetryAt - _time.GetUtcNow();
            }

            var state = StateFrom(answer);
            if (state != JobState.Processing)
            {
                return state;
            }

            // Waiting past the limit would hold up the platform's queue, so a long wait goes back to the caller too.
            if (_time.GetUtcNow() + (wait ?? TimeSpan.Zero) >= giveUpAt)
            {
                return JobState.Processing;
            }

            await Task.Delay(wait > TimeSpan.Zero ? wait.Value : delay, _time, cancellationToken);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, LongestPollDelay.Ticks));
        }
    }

    /// <summary>The token goes with every chunk, so a session address from anywhere else must never be used.</summary>
    private static bool IsYouTubeAddress(Uri address) =>
        address.Scheme == Uri.UriSchemeHttps &&
        (address.Host.Equals("googleapis.com", StringComparison.OrdinalIgnoreCase) ||
         address.Host.EndsWith(".googleapis.com", StringComparison.OrdinalIgnoreCase));

    private static string PrivacyName(YouTubePrivacy privacy) => privacy.ToString().ToLowerInvariant();

    private static string StartBody(YouTubePostDetails details)
    {
        var body = new
        {
            snippet = new
            {
                title = details.Title,
                description = details.Description,

                // An empty list can cause a 400.
                tags = details.Tags.Count > 0 ? details.Tags : null,
                categoryId = string.IsNullOrEmpty(details.CategoryId) ? DefaultCategoryId : details.CategoryId,
            },
            status = new
            {
                privacyStatus = PrivacyName(details.Privacy!.Value),
                publishAt = details.PublishAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                selfDeclaredMadeForKids = details.MadeForKids,
            },
        };

        return JsonSerializer.Serialize(body, BodyOptions);
    }

    /// <summary>What's in the Range header of a 308. No header means nothing was received. Null means it couldn't be read.</summary>
    private static long? ReceivedFrom(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Range", out var values))
        {
            return 0;
        }

        var text = values.FirstOrDefault() ?? "";
        const string prefix = "bytes=0-";
        return text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(text.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var last) &&
            last < long.MaxValue
                ? last + 1
                : null;
    }

    private static string? PrivacyIn(JsonElement? root) =>
        root is { ValueKind: JsonValueKind.Object } json &&
        json.TryGetProperty("status", out var status)
            ? OAuthHttp.GetString(status, "privacyStatus")
            : null;

    private static UploadReceipt? ReceiptFrom(JsonElement? root, YouTubePostDetails details)
    {
        if (root is not { ValueKind: JsonValueKind.Object } json || OAuthHttp.GetString(json, "id") is not { Length: > 0 } id)
        {
            return null;
        }

        // Google's docs disagree on whether unverified projects are limited to private, so trust what came back.
        var requested = PrivacyName(details.Privacy!.Value);
        return PrivacyIn(root) == requested
            ? new UploadReceipt(id)
            : new UploadReceipt(
                id,
                $"You asked for {requested}, but YouTube says this video is {PrivacyIn(root) ?? "something else"}. Google may limit uploads from projects it hasn't verified. Check the video in YouTube Studio.");
    }

    private UploadException Failure(UploadFailureKind kind, string message, DateTimeOffset? retryAt = null) =>
        new(Platform, kind, message, retryAt);

    private async Task<Uri> StartSessionAsync(Upload upload, CancellationToken cancellationToken)
    {
        // YouTube publishes a past time at once, so a job that waited too long must not start.
        var problems = PostDetailsValidator.Validate(upload.Details, _time.GetUtcNow());
        if (problems.Count > 0)
        {
            throw Failure(UploadFailureKind.Permanent, string.Join(" ", problems));
        }

        var headers = new Dictionary<string, string>
        {
            ["X-Upload-Content-Length"] = upload.Size.ToString(CultureInfo.InvariantCulture),
            ["X-Upload-Content-Type"] = upload.ContentType,
        };

        var reply = await _api.SendAsync(HttpMethod.Post, SessionEndpoint, StartBody(upload.Details), cancellationToken, headers: headers);

        return reply.Location is { } location && IsYouTubeAddress(location)
            ? location
            : throw Failure(UploadFailureKind.Retryable, "YouTube sent an unexpected reply to the upload request.");
    }

    /// <summary>Returns the receipt once YouTube has the whole file, or null if the session is gone and it must start again.</summary>
    private async Task<UploadReceipt?> RunSessionAsync(Upload upload, Uri session, bool resuming, CancellationToken cancellationToken)
    {
        // Null means YouTube has to be asked where things stand before sending anything more.
        long? offset = resuming ? null : 0;
        long mostReceived = 0;
        var retries = 0;
        var tokenReplaced = false;
        var checkSchedule = resuming;

        while (true)
        {
            string trouble;
            TimeSpan? wait = null;

            try
            {
                var reply = offset is { } position
                    ? await SendChunkAsync(upload, session, position, cancellationToken)
                    : await AskWhereThingsStandAsync(upload, session, cancellationToken);

                switch (reply.Status)
                {
                    case HttpStatusCode.OK:
                    case HttpStatusCode.Created:
                        if (ReceiptFrom(reply.Api.Root, upload.Details) is { } receipt)
                        {
                            upload.Progress?.Report(upload.Size);
                            return receipt;
                        }

                        trouble = "YouTube sent an unexpected reply when the upload finished.";
                        offset = null;
                        break;

                    case HttpStatusCode.PermanentRedirect when reply.Received > upload.Size:
                        return null;

                    case HttpStatusCode.PermanentRedirect when reply.Received is null:
                        trouble = "YouTube sent an unexpected reply during the upload.";
                        offset = null;
                        break;

                    case HttpStatusCode.PermanentRedirect:
                        var received = reply.Received.Value;

                        // A session left over from before can't go on past the scheduled time, for the same reason one can't start.
                        if (checkSchedule && upload.Details.PublishAt <= _time.GetUtcNow())
                        {
                            _sessions.Delete(upload.Job.Id);
                            throw Failure(UploadFailureKind.Permanent, "The scheduled time passed before this upload could finish. Choose a new time and queue it again.");
                        }

                        checkSchedule = false;
                        upload.Progress?.Report(received);
                        wait = reply.Api.RetryAfter;

                        var movedOn = received > mostReceived;
                        var wasSending = offset is not null;
                        if (movedOn)
                        {
                            mostReceived = received;
                            retries = 0;
                            tokenReplaced = false;
                        }

                        if (received == upload.Size)
                        {
                            trouble = "YouTube has the file but hasn't confirmed the upload.";
                            offset = null;
                            break;
                        }

                        offset = received;
                        if (wasSending && !movedOn)
                        {
                            trouble = "YouTube didn't take part of the upload.";
                            offset = null;
                            break;
                        }

                        await WaitBeforeContinuingAsync(wait, "YouTube asked to wait before continuing the upload.", cancellationToken);
                        continue;

                    case HttpStatusCode.Unauthorized when tokenReplaced:
                        throw Failure(UploadFailureKind.Reconnect, "YouTube refused this account's sign-in. Connect the account again.");

                    case HttpStatusCode.Unauthorized:
                        tokenReplaced = true;
                        await _api.ReportRejectedAsync(reply.Token, cancellationToken);
                        offset = null;
                        continue;

                    case HttpStatusCode.NotFound:
                    case HttpStatusCode.Gone:
                        return null;

                    default:
                        var failure = _api.FailureFrom(reply.Api);
                        if ((int)reply.Status < 500)
                        {
                            if (failure.Kind == UploadFailureKind.Permanent)
                            {
                                _sessions.Delete(upload.Job.Id);
                            }

                            throw failure;
                        }

                        trouble = failure.Message;
                        wait = reply.Api.RetryAfter;
                        offset = null;
                        break;
                }
            }
            catch (HttpRequestException)
            {
                trouble = "The connection to YouTube failed during the upload.";
                offset = null;
            }
            catch (IOException)
            {
                trouble = "The connection to YouTube failed during the upload.";
                offset = null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                trouble = "YouTube stopped responding during the upload.";
                offset = null;
            }

            if (retries == MaxRetries)
            {
                throw Failure(UploadFailureKind.Retryable, trouble);
            }

            await WaitBeforeContinuingAsync(wait ?? FirstRetryDelay * Math.Pow(2, retries), trouble, cancellationToken);
            retries++;
        }
    }

    /// <summary>A long wait goes back to the caller, so it doesn't hold up the platform's queue. The session stays saved.</summary>
    private async Task WaitBeforeContinuingAsync(TimeSpan? wait, string why, CancellationToken cancellationToken)
    {
        if (wait is not { } delay)
        {
            return;
        }

        if (delay > LongestRetryWait)
        {
            throw Failure(UploadFailureKind.Retryable, why, _time.GetUtcNow() + delay);
        }

        await Task.Delay(delay, _time, cancellationToken);
    }

    private Task<SessionReply> SendChunkAsync(Upload upload, Uri session, long position, CancellationToken cancellationToken)
    {
        var length = Math.Min(_chunkSize, upload.Size - position);

        return PutAsync(
            session,
            stall =>
            {
                var content = new FileChunkContent(upload.File, position, length, upload.ContentType, sent =>
                {
                    stall.CancelAfter(StallTimeout);
                    upload.Progress?.Report(position + sent);
                });
                content.Headers.ContentRange = new ContentRangeHeaderValue(position, position + length - 1, upload.Size);
                return content;
            },
            cancellationToken);
    }

    private Task<SessionReply> AskWhereThingsStandAsync(Upload upload, Uri session, CancellationToken cancellationToken) =>
        PutAsync(
            session,
            _ =>
            {
                var content = new ByteArrayContent([]);
                content.Headers.ContentRange = new ContentRangeHeaderValue(upload.Size);
                return content;
            },
            cancellationToken);

    private async Task<SessionReply> PutAsync(Uri session, Func<CancellationTokenSource, HttpContent> makeContent, CancellationToken cancellationToken)
    {
        string token;
        try
        {
            token = await _api.GetTokenAsync(cancellationToken);
        }
        catch (UploadException exception) when (exception.Kind == UploadFailureKind.Retryable)
        {
            throw new IOException(exception.Message);
        }

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(StallTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Put, session) { Content = makeContent(stall) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);
        var root = OAuthHttp.TryParse(await response.Content.ReadAsStringAsync(stall.Token));
        var api = new YouTubeApi.ApiReply(
            response.StatusCode,
            YouTubeApi.ReasonIn(root),
            root,
            YouTubeApi.RetryDelayFrom(response, _time.GetUtcNow()),
            null);

        return new SessionReply(response.StatusCode, ReceivedFrom(response), token, api);
    }

    private JobState StateFrom(JsonElement? root)
    {
        if (root is not { ValueKind: JsonValueKind.Object } json ||
            !json.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array ||
            items.GetArrayLength() == 0 ||
            !items[0].TryGetProperty("status", out var status))
        {
            // No answer, or a video just uploaded that isn't listed yet.
            return JobState.Processing;
        }

        return OAuthHttp.GetString(status, "uploadStatus") switch
        {
            "processed" => JobState.Completed,
            "failed" => throw FailedBecause(OAuthHttp.GetString(status, "failureReason")),
            "rejected" => throw RejectedBecause(OAuthHttp.GetString(status, "rejectionReason")),
            "deleted" => throw Failure(UploadFailureKind.Permanent, "This video was deleted from YouTube."),
            _ => JobState.Processing,
        };
    }

    private UploadException FailedBecause(string? reason) => reason switch
    {
        "uploadAborted" => Failure(UploadFailureKind.Retryable, "YouTube says the upload was cut off."),
        "codec" => Failure(UploadFailureKind.Permanent, "YouTube doesn't support this video's codec."),
        "conversion" => Failure(UploadFailureKind.Permanent, "YouTube couldn't convert this video."),
        "emptyFile" => Failure(UploadFailureKind.Permanent, "YouTube says this file is empty."),
        "invalidFile" => Failure(UploadFailureKind.Permanent, "YouTube says this isn't a video file it can read."),
        "tooSmall" => Failure(UploadFailureKind.Permanent, "YouTube says this file is too small."),
        _ => Failure(UploadFailureKind.Permanent, "YouTube couldn't process this video, and didn't say why."),
    };

    private UploadException RejectedBecause(string? reason) => Failure(
        UploadFailureKind.Permanent,
        reason switch
        {
            "duplicate" => "YouTube rejected this video as a duplicate of one already on the channel.",
            "copyright" or "claim" => "YouTube rejected this video over a copyright claim.",
            "length" => "YouTube rejected this video for its length.",
            "uploaderAccountClosed" or "uploaderAccountSuspended" => "YouTube rejected this video because the account is closed or suspended.",
            null => "YouTube rejected this video, and didn't say why.",
            _ => $"YouTube rejected this video ({reason}).",
        });

    private sealed record Upload(
        UploadJob Job,
        YouTubePostDetails Details,
        SafeFileHandle File,
        long Size,
        string ContentType,
        IProgress<long>? Progress);

    private sealed record SessionReply(HttpStatusCode Status, long? Received, string Token, YouTubeApi.ApiReply Api);
}
