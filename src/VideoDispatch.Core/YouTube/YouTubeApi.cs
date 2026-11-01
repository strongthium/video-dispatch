using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VideoDispatch.Core.Accounts;
using VideoDispatch.Core.Model;
using VideoDispatch.Core.Uploads;

namespace VideoDispatch.Core.YouTube;

public sealed record YouTubePlaylist(string Id, string Title);

public sealed record YouTubeCategory(string Id, string Title);

/// <summary>What the app asks of YouTube besides the upload itself.</summary>
public interface IYouTubeApi
{
    Task<IReadOnlyList<YouTubePlaylist>> ListPlaylistsAsync(CancellationToken cancellationToken = default);

    /// <summary>The categories a video can be given in this country.</summary>
    /// <param name="regionCode">A two-letter country code.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<IReadOnlyList<YouTubeCategory>> ListCategoriesAsync(string regionCode, CancellationToken cancellationToken = default);

    /// <summary>Puts an uploaded video in a playlist. A video already there counts as done, so this is safe to repeat.</summary>
    Task AddToPlaylistAsync(string playlistId, string videoId, CancellationToken cancellationToken = default);
}

/// <summary>Sends signed-in requests to YouTube and turns its errors into <see cref="UploadException"/>.</summary>
public sealed class YouTubeApi(HttpClient http, IAccessTokenSource tokens, TimeProvider time) : IYouTubeApi
{
    private const string ApiBase = "https://www.googleapis.com/youtube/v3/";
    private const int MaxPages = 100;
    private const int MaxServerMessageLength = 300;

    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DefaultRateLimitWait = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan UploadCapWait = TimeSpan.FromHours(24);

    public async Task<IReadOnlyList<YouTubePlaylist>> ListPlaylistsAsync(CancellationToken cancellationToken = default)
    {
        var playlists = new List<YouTubePlaylist>();
        string? pageToken = null;

        for (var page = 0; page < MaxPages; page++)
        {
            var url = $"{ApiBase}playlists?part=snippet&mine=true&maxResults=50";
            if (pageToken is not null)
            {
                url += "&pageToken=" + Uri.EscapeDataString(pageToken);
            }

            var reply = await SendAsync(HttpMethod.Get, url, null, cancellationToken);
            foreach (var item in Items(reply.Root))
            {
                if (OAuthHttp.GetString(item, "id") is { Length: > 0 } id)
                {
                    playlists.Add(new YouTubePlaylist(id, SnippetTitle(item) ?? id));
                }
            }

            pageToken = reply.Root is { } root ? OAuthHttp.GetString(root, "nextPageToken") : null;
            if (string.IsNullOrEmpty(pageToken))
            {
                break;
            }
        }

        return playlists;
    }

    public async Task<IReadOnlyList<YouTubeCategory>> ListCategoriesAsync(string regionCode, CancellationToken cancellationToken = default)
    {
        var url = $"{ApiBase}videoCategories?part=snippet&regionCode={Uri.EscapeDataString(regionCode)}";
        var reply = await SendAsync(HttpMethod.Get, url, null, cancellationToken);
        var categories = new List<YouTubeCategory>();

        foreach (var item in Items(reply.Root))
        {
            // Some categories exist only for viewing, and a video can't be put in them.
            var assignable = item.TryGetProperty("snippet", out var snippet) &&
                snippet.ValueKind == JsonValueKind.Object &&
                snippet.TryGetProperty("assignable", out var flag) &&
                flag.ValueKind == JsonValueKind.True;

            if (assignable && OAuthHttp.GetString(item, "id") is { Length: > 0 } id)
            {
                categories.Add(new YouTubeCategory(id, SnippetTitle(item) ?? id));
            }
        }

        return categories;
    }

    public async Task AddToPlaylistAsync(string playlistId, string videoId, CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            snippet = new
            {
                playlistId,
                resourceId = new { kind = "youtube#video", videoId },
            },
        });

        var reply = await SendAsync(HttpMethod.Post, $"{ApiBase}playlistItems?part=snippet", body, cancellationToken, throwOnError: false);
        if (reply.Status is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices)
        {
            return;
        }

        switch (reply.Reason)
        {
            case "videoAlreadyInPlaylist":
                return;

            case "playlistNotFound":
                throw Failure(UploadFailureKind.Permanent, "That playlist no longer exists.");

            case "playlistContainsMaximumNumberOfVideos":
                throw Failure(UploadFailureKind.Permanent, "That playlist is full.");

            // A video just uploaded may take a moment to be visible here.
            case "videoNotFound":
                throw Failure(UploadFailureKind.Retryable, "YouTube can't find the new video yet.");
        }

        throw FailureFrom(reply);
    }

    internal static string? ReasonIn(JsonElement? root)
    {
        if (root is not { ValueKind: JsonValueKind.Object } json ||
            !json.TryGetProperty("error", out var error) ||
            error.ValueKind != JsonValueKind.Object ||
            !error.TryGetProperty("errors", out var errors) ||
            errors.ValueKind != JsonValueKind.Array ||
            errors.GetArrayLength() == 0)
        {
            return null;
        }

        return OAuthHttp.GetString(errors[0], "reason");
    }

    internal static TimeSpan? RetryDelayFrom(HttpResponseMessage response, DateTimeOffset now)
    {
        if (response.Headers.RetryAfter is not { } retryAfter)
        {
            return null;
        }

        var delay = retryAfter.Delta ?? retryAfter.Date - now;
        return delay > TimeSpan.Zero ? delay : null;
    }

    /// <summary>The token to send now. Failures come back as an <see cref="UploadException"/> saying what to do.</summary>
    internal async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await tokens.GetAccessTokenAsync(Platform.YouTube, cancellationToken);
        }
        catch (Exception exception) when (exception is ReconnectRequiredException or AccountSetupException)
        {
            throw Failure(UploadFailureKind.Reconnect, exception.Message);
        }
        catch (TransientAccountException exception)
        {
            throw Failure(UploadFailureKind.Retryable, exception.Message);
        }
    }

    internal Task ReportRejectedAsync(string token, CancellationToken cancellationToken) =>
        tokens.ReportRejectedAsync(Platform.YouTube, token, cancellationToken);

    /// <summary>What a refused request means for the upload. Only a server fault or a rate limit is worth trying again.</summary>
    internal UploadException FailureFrom(ApiReply reply)
    {
        var now = time.GetUtcNow();

        switch (reply.Reason)
        {
            case "quotaExceeded" or "dailyLimitExceeded":
                return Failure(
                    UploadFailureKind.PlatformCap,
                    "Your Google project has used up its YouTube quota for today. This will wait until midnight Pacific time.",
                    YouTubeQuota.NextReset(now));

            case "uploadLimitExceeded":
                return Failure(
                    UploadFailureKind.PlatformCap,
                    "This channel reached YouTube's daily limit on uploads. This video will wait.",
                    now + UploadCapWait);

            case "rateLimitExceeded" or "userRateLimitExceeded":
                return Failure(
                    UploadFailureKind.Retryable,
                    "YouTube is limiting requests. Trying again shortly.",
                    now + (reply.RetryAfter ?? DefaultRateLimitWait));

            case "insufficientPermissions":
                return Failure(UploadFailureKind.Reconnect, "This account's sign-in doesn't allow this. Connect the account again.");
        }

        if (reply.Status == HttpStatusCode.TooManyRequests)
        {
            return Failure(
                UploadFailureKind.Retryable,
                "YouTube is limiting requests. Trying again shortly.",
                now + (reply.RetryAfter ?? DefaultRateLimitWait));
        }

        if ((int)reply.Status >= 500)
        {
            return Failure(
                UploadFailureKind.Retryable,
                $"YouTube is temporarily unavailable (HTTP {(int)reply.Status}).",
                reply.RetryAfter is { } after ? now + after : null);
        }

        var detail = reply.Reason is { } reason ? $"{Shorten(reason)}" : $"HTTP {(int)reply.Status}";
        var message = MessageIn(reply.Root) is { } text ? ": " + Shorten(text) : "";
        return Failure(UploadFailureKind.Permanent, $"YouTube refused the request ({detail}){message}");
    }

    /// <summary>Sends a request with the account's token. A refused token is replaced once.</summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="url">The full address.</param>
    /// <param name="jsonBody">The body, if there is one.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <param name="throwOnError">False hands back a refusal for the caller to read.</param>
    /// <param name="headers">Extra headers.</param>
    internal async Task<ApiReply> SendAsync(
        HttpMethod method,
        string url,
        string? jsonBody,
        CancellationToken cancellationToken,
        bool throwOnError = true,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var replaced = false;

        while (true)
        {
            var token = await GetTokenAsync(cancellationToken);
            var reply = await SendOnceAsync(method, url, jsonBody, token, headers, cancellationToken);

            if (reply.Status == HttpStatusCode.Unauthorized)
            {
                if (replaced)
                {
                    throw Failure(UploadFailureKind.Reconnect, "YouTube refused this account's sign-in. Connect the account again.");
                }

                replaced = true;
                await ReportRejectedAsync(token, cancellationToken);
                continue;
            }

            var refused = (int)reply.Status >= 400;
            if (refused && (throwOnError || (int)reply.Status >= 500 || reply.Status == HttpStatusCode.TooManyRequests))
            {
                throw FailureFrom(reply);
            }

            return reply;
        }
    }

    private static IEnumerable<JsonElement> Items(JsonElement? root) =>
        root is { ValueKind: JsonValueKind.Object } json &&
        json.TryGetProperty("items", out var items) &&
        items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object)
            : [];

    private static string? SnippetTitle(JsonElement item) =>
        item.TryGetProperty("snippet", out var snippet) ? OAuthHttp.GetString(snippet, "title") : null;

    private static string? MessageIn(JsonElement? root) =>
        root is { ValueKind: JsonValueKind.Object } json && json.TryGetProperty("error", out var error)
            ? OAuthHttp.GetString(error, "message")
            : null;

    private static string Shorten(string text) =>
        text.Length > MaxServerMessageLength ? text[..MaxServerMessageLength] : text;

    private static UploadException Failure(UploadFailureKind kind, string message, DateTimeOffset? retryAt = null) =>
        new(Platform.YouTube, kind, message, retryAt);

    private async Task<ApiReply> SendOnceAsync(
        HttpMethod method,
        string url,
        string? jsonBody,
        string token,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(ApiTimeout);

        try
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            if (jsonBody is not null)
            {
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            }

            foreach (var (name, value) in headers ?? new Dictionary<string, string>())
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            using var response = await http.SendAsync(request, limit.Token);
            var text = await response.Content.ReadAsStringAsync(limit.Token);
            var root = OAuthHttp.TryParse(text);

            return new ApiReply(response.StatusCode, ReasonIn(root), root, RetryDelayFrom(response, time.GetUtcNow()), response.Headers.Location);
        }
        catch (HttpRequestException)
        {
            throw Failure(UploadFailureKind.Retryable, "Couldn't reach YouTube.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure(UploadFailureKind.Retryable, "YouTube took too long to respond.");
        }
    }

    internal sealed record ApiReply(HttpStatusCode Status, string? Reason, JsonElement? Root, TimeSpan? RetryAfter, Uri? Location);
}
