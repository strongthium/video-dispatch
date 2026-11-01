using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Web;
using VideoDispatch.Core.Accounts;
using VideoDispatch.Core.Accounts.Secrets;
using VideoDispatch.Core.Media;
using VideoDispatch.Core.Model;
using VideoDispatch.Core.Presets;
using VideoDispatch.Core.Queue;
using VideoDispatch.Core.Reencoding;
using VideoDispatch.Core.Uploads;
using VideoDispatch.Core.YouTube;

namespace VideoDispatch.App;

/// <summary>
/// The whole app on made-up data. The real sign-in code and the real queue run, but the platforms are answered from
/// here. Nothing leaves this computer, nothing is saved, and no browser opens.
/// </summary>
public static class SampleServices
{
    private static readonly VideoMetadata SampleVideo = new(1080, 1920, 0, TimeSpan.FromSeconds(47), 30, "avc1");

    public static async Task<Services> CreateAsync()
    {
        var platforms = new SamplePlatforms();
        var http = new HttpClient(platforms);
        var secrets = new MemorySecretStore();
        var accounts = new AccountService(
            secrets,
            [new TikTokOAuthProvider(http), new GoogleOAuthProvider(http)],
            TimeProvider.System);

        await accounts.SaveCredentialsAsync(Platform.TikTok, new AppCredentials("sample-client-key", "sample-secret"));
        await accounts.SaveCredentialsAsync(Platform.YouTube, new AppCredentials("sample-client-id.apps.example", null));
        await accounts.ConnectAsync(Platform.TikTok, platforms.ApproveAsync);
        await accounts.ConnectAsync(Platform.YouTube, platforms.ApproveAsync, includeOptionalScopes: true);

        var youTube = new SampleYouTube();
        var queue = new UploadQueue(
            new MemoryRecords<UploadJob>(SampleJobs()),
            [new SampleUploader(Platform.TikTok), new SampleUploader(Platform.YouTube)],
            new MemorySessions(),
            youTube,
            new SampleRenderStep(),
            accounts.GetStatus,
            TimeProvider.System);

        var presets = new PresetLibrary(
            new MemoryRecords<TikTokPreset>(
            [
                new TikTokPreset { Id = Guid.NewGuid(), Name = "Trail clips", CaptionTemplate = "{title}", Hashtags = ["hiking", "outdoors"] },
            ]),
            new MemoryRecords<YouTubePreset>(
            [
                new YouTubePreset
                {
                    Id = Guid.NewGuid(),
                    Name = "Weekly vlog",
                    TitleTemplate = "{title} | Trail Notes",
                    DescriptionTemplate = "New every week. Filmed {date}.",
                    Tags = ["hiking", "vlog"],
                    CategoryId = "19",
                    Privacy = YouTubePrivacy.Private,
                    MadeForKids = false,
                    PlaylistId = "PL-trails",
                    Schedule = new YouTubeSchedule(1, new TimeOnly(18, 0)),
                },
            ]));

        return new Services(
            accounts,
            platforms.ApproveAsync,
            queue,
            presets,
            youTube,
            _ => SampleVideo,
            new AppSettingsStore(null),
            (_, _) => Task.FromResult(new FfmpegInfo("ffmpeg, in the sample", true)));
    }

    private static IEnumerable<UploadJob> SampleJobs()
    {
        var now = DateTimeOffset.Now;

        UploadJob Job(string file, PostDetails details, JobState state, TimeSpan age) => new()
        {
            Id = Guid.NewGuid(),
            BatchId = Guid.NewGuid(),
            SourcePath = $@"C:\Videos\{file}",
            SourceLength = 734_003_200,
            SourceLastWriteUtc = now.AddDays(-1),
            TotalBytes = 734_003_200,
            Details = details,
            CreatedAt = now - age,
            State = state,
        };

        YouTubePostDetails Video(string title) => new()
        {
            Title = title,
            Privacy = YouTubePrivacy.Unlisted,
            MadeForKids = false,
        };

        return
        [
            Job("lake-morning.mp4", Video("Lake at first light"), JobState.Completed, TimeSpan.FromDays(2)) with
            {
                RemoteId = "sample-video-1",
                BytesSent = 734_003_200,
            },
            Job("ridge-walk.mp4", new TikTokPostDetails { Caption = "The ridge at sunset #hiking #outdoors" }, JobState.AwaitingCreator, TimeSpan.FromHours(3)) with
            {
                RemoteId = "sample-publish-1",
                UploadedAt = now.AddHours(-3),
                BytesSent = 734_003_200,
            },
            Job("old-bridge.mov", Video("The old bridge"), JobState.Failed, TimeSpan.FromHours(5)) with
            {
                LastError = "The video file has changed since it was queued. Queue it again to post the new version.",
            },
            Job("forest-loop.mp4", Video("Forest loop, part 2"), JobState.Waiting, TimeSpan.FromHours(1)) with
            {
                NextAttemptAt = YouTubeQuota.NextReset(now),
                LastError = "Your Google project has used up its YouTube quota for today. This will wait until midnight Pacific time.",
            },
            Job("summit.mp4", Video("Summit day"), JobState.Queued, TimeSpan.FromMinutes(10)),
        ];
    }

    /// <summary>Takes about as long as a short video might, and changes nothing.</summary>
    private sealed class SampleRenderStep : IRenderStep
    {
        public async Task<RenderResult> RenderAsync(UploadJob job, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            if (job.Details.Encode is null)
            {
                return new RenderResult(null);
            }

            for (var percent = 0; percent < 100; percent += 2)
            {
                progress?.Report(percent);
                await Task.Delay(200, cancellationToken);
            }

            return new RenderResult(null, "In the sample, nothing is really re-encoded.");
        }

        public void Release(string path)
        {
        }

        public void Tidy(IReadOnlyCollection<string> stillNeeded)
        {
        }
    }

    private sealed class MemoryRecords<T>(IEnumerable<T>? initial = null) : IRecordStore<T>
    {
        private readonly List<T> _initial = [.. initial ?? []];

        public StoreLoad<T> LoadAll() => new(_initial, []);

        public void Save(Guid id, T item)
        {
        }

        public void Delete(Guid id)
        {
        }
    }

    private sealed class MemorySessions : IUploadSessionStore
    {
        public string? Get(Guid jobId) => null;

        public void Save(Guid jobId, string sessionUrl)
        {
        }

        public void Delete(Guid jobId)
        {
        }
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly ConcurrentDictionary<string, string> _values = new();

        public string? Get(string key) => _values.GetValueOrDefault(key);

        public void Set(string key, string value) => _values[key] = value;

        public void Delete(string key) => _values.TryRemove(key, out _);
    }

    /// <summary>Takes its time, so the queue can be watched moving.</summary>
    private sealed class SampleUploader(Platform platform) : IVideoUploader
    {
        public Platform Platform => platform;

        public async Task<UploadReceipt> UploadAsync(UploadJob job, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
        {
            for (var share = 1; share <= 100; share++)
            {
                await Task.Delay(250, cancellationToken);
                progress?.Report(job.TotalBytes * share / 100);
            }

            return new UploadReceipt("sample-" + job.Id.ToString("N"));
        }

        public async Task<JobState> WaitForResultAsync(string remoteId, CancellationToken cancellationToken = default)
        {
            await Task.Delay(3000, cancellationToken);
            return platform == Platform.TikTok ? JobState.AwaitingCreator : JobState.Completed;
        }
    }

    private sealed class SampleYouTube : IYouTubeApi
    {
        public Task<IReadOnlyList<YouTubePlaylist>> ListPlaylistsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<YouTubePlaylist>>([new("PL-trails", "Trail Notes"), new("PL-gear", "Gear reviews")]);

        public Task<IReadOnlyList<YouTubeCategory>> ListCategoriesAsync(string regionCode, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<YouTubeCategory>>([new("19", "Travel & Events"), new("22", "People & Blogs"), new("26", "Howto & Style")]);

        public Task AddToPlaylistAsync(string playlistId, string videoId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class SamplePlatforms : HttpMessageHandler
    {
        private static readonly HttpClient Redirects = new();
        private string _approvedScopes = "";

        /// <summary>Does what the user's browser would: signs in at once and follows the redirect back.</summary>
        public async Task ApproveAsync(Uri authorizationUri, CancellationToken cancellationToken)
        {
            var query = HttpUtility.ParseQueryString(authorizationUri.Query);
            _approvedScopes = query["scope"] ?? "";

            var callback = $"{query["redirect_uri"]}?code=sample-code&state={Uri.EscapeDataString(query["state"] ?? "")}";
            using var response = await Redirects.GetAsync(callback, cancellationToken);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Json(request.RequestUri?.AbsoluteUri switch
            {
                var url when url?.StartsWith("https://open.tiktokapis.com/v2/oauth/token/") == true =>
                    $$"""{"access_token":"sample","expires_in":86400,"refresh_token":"sample","refresh_expires_in":31536000,"scope":"{{_approvedScopes}}"}""",
                var url when url?.StartsWith("https://open.tiktokapis.com/v2/user/info/") == true =>
                    """{"data":{"user":{"open_id":"sample","display_name":"Sample Creator"}}}""",
                var url when url?.StartsWith("https://oauth2.googleapis.com/token") == true =>
                    $$"""{"access_token":"sample","expires_in":3600,"refresh_token":"sample","scope":"{{_approvedScopes}}"}""",
                var url when url?.StartsWith("https://www.googleapis.com/youtube/v3/channels") == true =>
                    """{"items":[{"id":"sample","snippet":{"title":"Sample Channel"}}]}""",
                var url when url?.Contains("revoke") == true => "",
                var url => throw new InvalidOperationException($"The sample doesn't answer {url}."),
            }));

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
