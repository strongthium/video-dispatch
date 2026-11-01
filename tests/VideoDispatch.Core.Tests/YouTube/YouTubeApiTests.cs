using System.Net;
using VideoDispatch.Core.Media;
using VideoDispatch.Core.Model;
using VideoDispatch.Core.Tests.TikTok;
using VideoDispatch.Core.Uploads;
using VideoDispatch.Core.YouTube;

namespace VideoDispatch.Core.Tests.YouTube;

public sealed class YouTubeApiTests
{
    private const string Playlists = "https://www.googleapis.com/youtube/v3/playlists";
    private const string Items = "https://www.googleapis.com/youtube/v3/playlistItems";
    private const string Categories = "https://www.googleapis.com/youtube/v3/videoCategories";

    private readonly FakeYouTube _server = new();
    private readonly FakeTokens _tokens = new(Platform.YouTube);
    private readonly YouTubeApi _api;

    public YouTubeApiTests()
    {
        _api = new YouTubeApi(new HttpClient(_server), _tokens, new JumpingTime(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero)));
    }

    private static object Playlist(string id, string title) => new { id, snippet = new { title } };

    [Fact]
    public async Task Lists_every_page_of_playlists()
    {
        var pages = new Queue<HttpResponseMessage>(
        [
            FakeYouTube.Json(HttpStatusCode.OK, new { nextPageToken = "next+page", items = new[] { Playlist("PL1", "First") } }),
            FakeYouTube.Json(HttpStatusCode.OK, new { items = new[] { Playlist("PL2", "Second") } }),
        ]);
        _server.Other.Add((Playlists, pages.Dequeue));

        var playlists = await _api.ListPlaylistsAsync();

        Assert.Equal([new YouTubePlaylist("PL1", "First"), new YouTubePlaylist("PL2", "Second")], playlists);
        Assert.Equal($"GET {Playlists}?part=snippet&mine=true&maxResults=50", _server.Requests[0]);
        Assert.Equal($"GET {Playlists}?part=snippet&mine=true&maxResults=50&pageToken=next%2Bpage", _server.Requests[1]);
    }

    [Fact]
    public async Task A_server_that_never_stops_paging_is_cut_off()
    {
        _server.Other.Add((Playlists, () => FakeYouTube.Json(HttpStatusCode.OK, new { nextPageToken = "again", items = Array.Empty<object>() })));

        await _api.ListPlaylistsAsync();

        Assert.Equal(100, _server.Requests.Count);
    }

    [Fact]
    public async Task Lists_only_categories_a_video_can_be_put_in()
    {
        _server.Other.Add((Categories, () => FakeYouTube.Json(HttpStatusCode.OK, new
        {
            items = new[]
            {
                new { id = "22", snippet = new { title = "People & Blogs", assignable = true } },
                new { id = "18", snippet = new { title = "Short Movies", assignable = false } },
            },
        })));

        var categories = await _api.ListCategoriesAsync("CA");

        Assert.Equal([new YouTubeCategory("22", "People & Blogs")], categories);
        Assert.Equal($"GET {Categories}?part=snippet&regionCode=CA", _server.Requests[0]);
    }

    [Fact]
    public async Task Adds_a_video_to_a_playlist()
    {
        _server.Other.Add((Items, () => FakeYouTube.Json(HttpStatusCode.OK, new { id = "item" })));

        await _api.AddToPlaylistAsync("PL1", "video-1");

        var snippet = JsonText.Parse(_server.Bodies.Single()).GetProperty("snippet");
        Assert.Equal("PL1", snippet.GetProperty("playlistId").GetString());
        Assert.Equal("youtube#video", snippet.GetProperty("resourceId").GetProperty("kind").GetString());
        Assert.Equal("video-1", snippet.GetProperty("resourceId").GetProperty("videoId").GetString());
    }

    [Fact]
    public async Task A_video_already_in_the_playlist_counts_as_done()
    {
        _server.Other.Add((Items, () => FakeYouTube.Error(HttpStatusCode.Conflict, "videoAlreadyInPlaylist")));

        await _api.AddToPlaylistAsync("PL1", "video-1");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "playlistNotFound", UploadFailureKind.Permanent)]
    [InlineData(HttpStatusCode.Forbidden, "playlistContainsMaximumNumberOfVideos", UploadFailureKind.Permanent)]
    [InlineData(HttpStatusCode.Forbidden, "playlistItemsNotAccessible", UploadFailureKind.Permanent)]
    [InlineData(HttpStatusCode.NotFound, "videoNotFound", UploadFailureKind.Retryable)]
    [InlineData(HttpStatusCode.Forbidden, "insufficientPermissions", UploadFailureKind.Reconnect)]
    [InlineData(HttpStatusCode.Forbidden, "quotaExceeded", UploadFailureKind.PlatformCap)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "backendError", UploadFailureKind.Retryable)]
    public async Task A_refused_playlist_step_says_what_to_do_next(HttpStatusCode status, string reason, UploadFailureKind kind)
    {
        _server.Other.Add((Items, () => FakeYouTube.Error(status, reason)));

        var failure = await Assert.ThrowsAsync<UploadException>(() => _api.AddToPlaylistAsync("PL1", "video-1"));

        Assert.Equal(kind, failure.Kind);
    }

    [Fact]
    public async Task A_lookup_replaces_a_refused_token_once()
    {
        var answers = new Queue<HttpResponseMessage>(
            [FakeYouTube.Error(HttpStatusCode.Unauthorized, "authError"), FakeYouTube.Json(HttpStatusCode.OK, new { items = Array.Empty<object>() })]);
        _server.Other.Add((Playlists, answers.Dequeue));

        await _api.ListPlaylistsAsync();

        Assert.Equal(["token-1"], _tokens.Rejected);
    }

    [Theory]
    [InlineData("2026-11-01T12:00:00Z", "2026-11-02T00:00:00-08:00")]
    [InlineData("2026-11-02T07:59:59Z", "2026-11-02T00:00:00-08:00")]
    [InlineData("2026-11-02T08:00:00Z", "2026-11-03T00:00:00-08:00")]
    [InlineData("2026-07-01T12:00:00Z", "2026-07-02T00:00:00-07:00")]
    [InlineData("2026-03-08T07:00:00Z", "2026-03-08T00:00:00-08:00")]
    [InlineData("2026-03-08T09:00:00Z", "2026-03-09T00:00:00-07:00")]
    public void Quota_resets_at_midnight_Pacific_through_daylight_saving(string now, string expected)
    {
        Assert.Equal(DateTimeOffset.Parse(expected), YouTubeQuota.NextReset(DateTimeOffset.Parse(now)));
    }

    [Theory]
    [InlineData(1080, 1920, 0, 60, ShortsPrediction.Short)]
    [InlineData(1080, 1080, 0, 60, ShortsPrediction.Short)]
    [InlineData(1080, 1920, 0, 180, ShortsPrediction.Short)]
    [InlineData(1080, 1920, 0, 181, ShortsPrediction.LongForm)]
    [InlineData(1920, 1080, 0, 60, ShortsPrediction.LongForm)]
    [InlineData(1920, 1080, 90, 60, ShortsPrediction.Short)]
    [InlineData(1080, 1920, 90, 60, ShortsPrediction.LongForm)]
    [InlineData(1920, 1080, 180, 60, ShortsPrediction.LongForm)]
    public void Predicts_a_Short_from_what_viewers_see(int width, int height, int rotation, int seconds, ShortsPrediction expected)
    {
        var metadata = new VideoMetadata(width, height, rotation, TimeSpan.FromSeconds(seconds), 30, "avc1");

        Assert.Equal(expected, YouTubeVideoLimits.PredictShort(metadata));
    }

    [Fact]
    public void Unknown_metadata_predicts_nothing()
    {
        Assert.Equal(ShortsPrediction.Unknown, YouTubeVideoLimits.PredictShort(null));
        Assert.Equal(ShortsPrediction.Unknown, YouTubeVideoLimits.PredictShort(new VideoMetadata(0, 0, 0, TimeSpan.FromSeconds(5), null, null)));
        Assert.Equal(ShortsPrediction.Unknown, YouTubeVideoLimits.PredictShort(new VideoMetadata(1080, 1920, 0, null, null, null)));
    }

    [Fact]
    public void A_wide_video_of_unknown_length_is_long_form_whatever_its_length()
    {
        Assert.Equal(ShortsPrediction.LongForm, YouTubeVideoLimits.PredictShort(new VideoMetadata(1920, 1080, 0, null, null, null)));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(YouTubeVideoLimits.MaxVideoSize, false)]
    [InlineData(YouTubeVideoLimits.MaxVideoSize + 1, true)]
    public void Checks_the_file_size(long size, bool blocks)
    {
        Assert.Equal(blocks, YouTubeVideoLimits.Check(size).Any(finding => finding.Blocks));
    }

    [Theory]
    [InlineData("a.MP4", "video/mp4")]
    [InlineData("a.mov", "video/quicktime")]
    [InlineData("a.webm", "video/webm")]
    [InlineData("a.unknown", "application/octet-stream")]
    [InlineData("a", "application/octet-stream")]
    public void Names_the_media_type(string path, string expected)
    {
        Assert.Equal(expected, YouTubeVideoLimits.ContentTypeFor(path));
    }
}
