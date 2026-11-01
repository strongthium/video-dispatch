using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using VideoDispatch.App;
using VideoDispatch.Core.Accounts.Secrets;
using VideoDispatch.Core.Media;
using VideoDispatch.Core.Model;
using VideoDispatch.Core.Presets;
using VideoDispatch.Core.Queue;
using VideoDispatch.Core.Reencoding;
using VideoDispatch.Core.Tests.Media;
using VideoDispatch.Tests.Reencoding;

namespace VideoDispatch.Tests.EndToEnd;

/// <summary>
/// The whole flow, through the screens' logic and everything behind them as the app wires it, against local servers
/// over real connections. Only the secure store is swapped, so this leaves nothing in Credential Manager. TikTok and
/// Google themselves still haven't been tried.
/// </summary>
public sealed class EndToEndTests : IAsyncDisposable
{
    private const long Mib = 1024 * 1024;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private readonly string _folder = Path.Combine(AppContext.BaseDirectory, "end-to-end-" + Guid.NewGuid().ToString("N"));
    private readonly LocalPlatforms _platforms = new();
    private readonly MemorySecrets _secrets = new();
    private readonly List<AppRun> _runs = [];

    public EndToEndTests() => Directory.CreateDirectory(_folder);

    public async ValueTask DisposeAsync()
    {
        foreach (var run in _runs)
        {
            await run.CloseAsync();
        }

        await _platforms.DisposeAsync();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task A_video_goes_from_signing_in_to_both_platforms()
    {
        var path = NewVideo("morning-walk.mp4", 33 * Mib);
        var bytes = File.ReadAllBytes(path);
        var app = OpenApp();

        await app.ConnectBothAsync(wantPlaylists: true);
        Assert.Equal("Connected as Trail Notes", app.TikTok.StateText);
        Assert.Equal("Connected as Trail Notes Channel", app.YouTube.StateText);

        var tikTokPreset = new TikTokPreset { Id = Guid.NewGuid(), Name = "Walks", CaptionTemplate = "{title}", Hashtags = ["walk"] };
        var youTubePreset = new YouTubePreset
        {
            Id = Guid.NewGuid(),
            Name = "Walks",
            TitleTemplate = "{title}: a walk",
            DescriptionTemplate = "From {filename}",
            Tags = ["walk", "morning"],
            CategoryId = "19",
            Privacy = YouTubePrivacy.Private,
            MadeForKids = false,
            PlaylistId = "PL2",
            Schedule = new YouTubeSchedule(2, new TimeOnly(9, 0)),
        };
        await app.Services.Presets.SaveAsync(tikTokPreset);
        await app.Services.Presets.SaveAsync(youTubePreset);

        await app.NewPost.RefreshAsync();
        Assert.Contains(app.NewPost.Categories, category => category.Title == "Travel & Events");
        Assert.DoesNotContain(app.NewPost.Categories, category => category.Title == "Short Movies");
        Assert.Contains(app.NewPost.Playlists, playlist => playlist.Title == "Summit Days");

        await app.NewPost.ChooseVideoAsync(path);
        app.NewPost.TikTokPreset = tikTokPreset;
        app.NewPost.YouTubePreset = youTubePreset;

        Assert.Contains("1080 × 1920", app.NewPost.MetadataText);
        Assert.Contains("probably make this a Short", app.NewPost.ShortsText);
        Assert.Contains("Title: morning-walk: a walk", app.NewPost.YouTubePreview);
        Assert.Contains("Summit Days", app.NewPost.YouTubePreview);
        Assert.Contains("morning-walk #walk", app.NewPost.TikTokPreview);
        Assert.Empty(app.NewPost.Problems);
        Assert.True(app.NewPost.CanQueue);

        await app.NewPost.QueueAsync();
        Assert.Equal("Queued. You can follow it under Queue.", app.NewPost.Message);

        await app.UntilAsync(Platform.TikTok, job => job.State == JobState.AwaitingCreator);
        await app.UntilAsync(Platform.YouTube, job => job is { State: JobState.Completed, PlaylistAdded: true });

        // What arrived is exactly the file, as each platform's rules ask.
        Assert.Equal(bytes, Assert.Single(_platforms.TikTokVideos));
        Assert.Equal(bytes, Assert.Single(_platforms.YouTubeVideos));
        Assert.Equal([16 * Mib, 16 * Mib, Mib], _platforms.YouTubeChunkSizes);
        Assert.DoesNotContain("morning-walk", Assert.Single(_platforms.TikTokInits));

        using var started = JsonDocument.Parse(Assert.Single(_platforms.YouTubeStarts));
        var snippet = started.RootElement.GetProperty("snippet");
        var status = started.RootElement.GetProperty("status");
        Assert.Equal("morning-walk: a walk", snippet.GetProperty("title").GetString());
        Assert.Equal("From morning-walk", snippet.GetProperty("description").GetString());
        Assert.Equal(["walk", "morning"], snippet.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()));
        Assert.Equal("19", snippet.GetProperty("categoryId").GetString());
        Assert.Equal("private", status.GetProperty("privacyStatus").GetString());
        Assert.False(status.GetProperty("selfDeclaredMadeForKids").GetBoolean());
        Assert.True(status.GetProperty("publishAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);

        var youTubeJob = app.JobOn(Platform.YouTube);
        Assert.Equal(("PL2", youTubeJob.RemoteId), Assert.Single(_platforms.PlaylistItems));

        // The queue screen tells the creator what's left to do, and finds out when they've done it.
        var tikTokRow = await app.RowOfAsync(Platform.TikTok);
        Assert.Contains("Open TikTok to finish the post", tikTokRow.StateText);
        Assert.True(tikTokRow.ShowsCaption);
        Assert.Equal("morning-walk #walk", tikTokRow.Caption);

        _platforms.CreatorPosted = true;
        await app.Queue.RetryAsync(tikTokRow);
        await app.UntilAsync(Platform.TikTok, job => job.State == JobState.Completed);

        AssertNoSecretOnDisk();
        Assert.DoesNotContain(_secrets.Values.Values, value => value.Contains("upload_id", StringComparison.Ordinal));

        // Leaving ends the sign-ins at each platform too.
        await app.TikTok.DisconnectAsync();
        await app.YouTube.DisconnectAsync();
        Assert.Equal(["TikTok", "YouTube"], _platforms.Revoked);
        Assert.False(app.TikTok.IsConnected);
        Assert.False(app.YouTube.IsConnected);

        Assert.Empty(_platforms.Problems);
    }

    [Fact]
    public async Task Closing_during_an_upload_carries_on_at_the_next_start_with_no_second_copy()
    {
        var path = NewVideo("summit.mp4", 33 * Mib);
        var bytes = File.ReadAllBytes(path);
        var first = OpenApp();

        await first.ConnectBothAsync(wantPlaylists: false);
        await first.NewPost.RefreshAsync();

        _platforms.StallLaterChunks = true;
        await first.ChooseAndQueueAsync(path);
        await _platforms.YouTubeStalled.Task.WaitAsync(Patience);
        await _platforms.TikTokStalled.Task.WaitAsync(Patience);

        var ids = first.Services.Queue.Jobs.Select(job => job.Id).Order().ToList();
        await first.CloseAsync();

        // The next start has no access token in memory, so it must use the saved sign-in, and finds its jobs on disk.
        _platforms.StallLaterChunks = false;
        var second = OpenApp();
        Assert.Equal(ids, second.Services.Queue.Jobs.Select(job => job.Id).Order());

        await second.UntilAsync(Platform.YouTube, job => job.State == JobState.Completed);
        await second.UntilAsync(Platform.TikTok, job => job.State == JobState.AwaitingCreator);

        Assert.Equal(bytes, Assert.Single(_platforms.YouTubeVideos));
        Assert.Single(_platforms.YouTubeStarts);
        Assert.True(_platforms.YouTubeStatusChecks > 0);

        // TikTok has no way to resume, so it starts again under a new init.
        Assert.Equal(bytes, Assert.Single(_platforms.TikTokVideos));
        Assert.Equal(2, _platforms.TikTokInits.Count);

        Assert.Contains("TikTok refresh_token", _platforms.Grants);
        Assert.Contains("YouTube refresh_token", _platforms.Grants);

        AssertNoSecretOnDisk();
        Assert.Empty(_platforms.Problems);
    }

    [Fact]
    public async Task A_sign_in_taken_away_pauses_only_that_platform_until_it_is_connected_again()
    {
        var path = NewVideo("ridge.mp4", Mib);
        var bytes = File.ReadAllBytes(path);
        var app = OpenApp();

        await app.ConnectBothAsync(wantPlaylists: false);
        await app.NewPost.RefreshAsync();
        _platforms.EndAllSignIns();

        app.NewPost.PostToTikTok = false;
        await app.ChooseAndQueueAsync(path);

        await app.UntilAsync(Platform.YouTube, job => job.State == JobState.NeedsReconnect);
        Assert.True(app.Services.Queue.NeedsReconnect(Platform.YouTube));
        Assert.False(app.Services.Queue.NeedsReconnect(Platform.TikTok));
        Assert.Contains("YouTube uploads are paused", app.Queue.Notice);
        Assert.Empty(_platforms.YouTubeVideos);

        await app.YouTube.LoadAsync();
        Assert.Equal("The sign-in has expired or was revoked. Connect again.", app.YouTube.StateText);

        await app.YouTube.ConnectAsync();
        await app.UntilAsync(Platform.YouTube, job => job.State == JobState.Completed);

        Assert.Equal(bytes, Assert.Single(_platforms.YouTubeVideos));
        Assert.Empty(_platforms.Problems);
    }

    [Fact]
    public async Task Running_out_of_YouTube_quota_holds_YouTube_and_lets_TikTok_finish()
    {
        var path = NewVideo("lake.mp4", Mib);
        var bytes = File.ReadAllBytes(path);
        var app = OpenApp();

        await app.ConnectBothAsync(wantPlaylists: false);
        await app.NewPost.RefreshAsync();

        _platforms.OutOfQuota = true;
        await app.ChooseAndQueueAsync(path);

        var waiting = await app.UntilAsync(Platform.YouTube, job => job.State == JobState.Waiting);
        await app.UntilAsync(Platform.TikTok, job => job.State == JobState.AwaitingCreator);

        Assert.Equal(bytes, Assert.Single(_platforms.TikTokVideos));
        Assert.Empty(_platforms.YouTubeVideos);
        Assert.True(waiting.NextAttemptAt > DateTimeOffset.UtcNow);
        Assert.Contains("midnight Pacific", waiting.LastError);

        _platforms.OutOfQuota = false;
        await app.Queue.RetryAsync(await app.RowOfAsync(Platform.YouTube));
        await app.UntilAsync(Platform.YouTube, job => job.State == JobState.Completed);

        Assert.Equal(bytes, Assert.Single(_platforms.YouTubeVideos));
        Assert.Empty(_platforms.Problems);
    }

    [Fact]
    public async Task A_video_re_encoded_with_Windows_tools_reaches_both_platforms_and_the_original_stays_as_it_was()
    {
        // Not an MP4, so the new file is kept whatever its size, and TikTok wouldn't have taken the original.
        var made = await TestVideos.MakeAsync(_folder, "walk.mp4", 1280, 720);
        var path = Path.ChangeExtension(made, ".m4v");
        File.Move(made, path);
        var original = File.ReadAllBytes(path);
        var app = OpenApp();

        await app.ConnectBothAsync(wantPlaylists: false);
        await app.NewPost.RefreshAsync();
        await app.NewPost.ChooseVideoAsync(path);
        app.NewPost.Caption = "A caption";
        app.NewPost.PrivacyId = nameof(YouTubePrivacy.Private);
        app.NewPost.MadeForKidsId = YouTubeChoices.NoId;
        app.NewPost.TikTokEncodeId = nameof(EncodeGoal.SmallFile);
        app.NewPost.YouTubeEncodeId = nameof(EncodeGoal.SmallFile);
        Assert.True(app.NewPost.CanQueue, string.Join(" ", app.NewPost.Problems));

        await app.NewPost.QueueAsync();

        await app.UntilAsync(Platform.TikTok, job => job.State == JobState.AwaitingCreator);
        var finished = await app.UntilAsync(Platform.YouTube, job => job.State == JobState.Completed);

        var sent = Assert.Single(_platforms.YouTubeVideos);
        Assert.Equal(sent, Assert.Single(_platforms.TikTokVideos));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.NotEqual(original, sent);
        Assert.Equal(sent.Length, finished.TotalBytes);

        // The file itself reads back as what was asked for, and it's gone from the app's folder once both have it.
        var check = Path.Combine(_folder, "sent.mp4");
        await File.WriteAllBytesAsync(check, sent);
        var read = Mp4MetadataReader.Read(check);
        Assert.Equal((1280, 720), (read!.DisplayWidth, read.DisplayHeight));
        Assert.Equal("avc1", read.CodecTag);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_folder, "data", "rendered")));

        AssertNoSecretOnDisk();
        Assert.Empty(_platforms.Problems);
    }

    /// <summary>A vertical 30 fps video that reads as real, made up to its size with bytes that aren't all the same.</summary>
    private string NewVideo(string name, long size)
    {
        var path = Path.Combine(_folder, name);
        var start = Mp4Builder.Join(
            Mp4Builder.Ftyp(),
            Mp4Builder.Box("moov", Mp4Builder.Mvhd(30000, 300000), Mp4Builder.VideoTrack(1080, 1920)));

        var padding = new byte[size - start.Length - 8];
        new Random(42).NextBytes(padding);

        using var file = File.Create(path);
        file.Write(start);
        file.Write(Mp4Builder.U32((uint)(padding.Length + 8)));
        file.Write(Encoding.ASCII.GetBytes("mdat"));
        file.Write(padding);
        return path;
    }

    private AppRun OpenApp()
    {
        var run = new AppRun(Services.Create(
            _secrets,
            new AppFolders(Path.Combine(_folder, "data")),
            _platforms.ApproveSignInAsync,
            _platforms.Through));

        _runs.Add(run);
        return run;
    }

    private void AssertNoSecretOnDisk()
    {
        var files = Directory.EnumerateFiles(Path.Combine(_folder, "data"), "*", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var secret in _platforms.Secrets)
            {
                Assert.DoesNotContain(secret, text);
            }
        }
    }

    private sealed class MemorySecrets : ISecretStore
    {
        public ConcurrentDictionary<string, string> Values { get; } = new();

        public string? Get(string key) => Values.GetValueOrDefault(key);

        public void Set(string key, string value) => Values[key] = value;

        public void Delete(string key) => Values.TryRemove(key, out _);
    }

    /// <summary>One run of the app: its screens' logic, joined up as <see cref="MainWindow"/> joins them.</summary>
    private sealed class AppRun
    {
        private readonly object _uiThread = new();
        private bool _closed;

        public AppRun(Services services)
        {
            Services = services;
            var choices = new YouTubeChoices(services.YouTube, services.Accounts.GetStatus);
            NewPost = new NewPostPanel(services.Queue, services.Presets, choices, services.Accounts.GetStatus, services.ReadMetadata, TimeProvider.System, TimeZoneInfo.Local);
            TikTok = new AccountPanel(services.Accounts, Platform.TikTok, services.OpenBrowser);
            YouTube = new AccountPanel(services.Accounts, Platform.YouTube, services.OpenBrowser);

            foreach (var account in new[] { TikTok, YouTube })
            {
                account.Connected += platform => _ = Task.Run(() => services.Queue.AccountReconnected(platform));
                account.AccountChanged += _ => choices.Forget();
            }

            services.Queue.Start();
            Queue = new QueuePanel(services.Queue, action =>
            {
                lock (_uiThread)
                {
                    action();
                }
            });
        }

        public Services Services { get; }

        public NewPostPanel NewPost { get; }

        public QueuePanel Queue { get; }

        public AccountPanel TikTok { get; }

        public AccountPanel YouTube { get; }

        public async Task ConnectBothAsync(bool wantPlaylists)
        {
            TikTok.ClientId = LocalPlatforms.TikTokKey;
            TikTok.ClientSecret = LocalPlatforms.TikTokSecret;
            await TikTok.SaveKeysAsync();
            await TikTok.ConnectAsync();
            Assert.Null(TikTok.Message);

            YouTube.ClientId = LocalPlatforms.GoogleId;
            YouTube.ClientSecret = LocalPlatforms.GoogleSecret;
            await YouTube.SaveKeysAsync();
            YouTube.WantsPlaylists = wantPlaylists;
            await YouTube.ConnectAsync();
            Assert.Null(YouTube.Message);
        }

        /// <summary>The fields a person must fill in themselves, then the Add to queue button.</summary>
        public async Task ChooseAndQueueAsync(string path)
        {
            await NewPost.ChooseVideoAsync(path);
            NewPost.Caption = "A caption";
            NewPost.PrivacyId = nameof(YouTubePrivacy.Private);
            NewPost.MadeForKidsId = YouTubeChoices.NoId;
            Assert.True(NewPost.CanQueue, string.Join(" ", NewPost.Problems));
            await NewPost.QueueAsync();
        }

        public UploadJob JobOn(Platform platform) => Services.Queue.Jobs.Single(job => job.Platform == platform);

        public async Task<UploadJob> UntilAsync(Platform platform, Func<UploadJob, bool> isDone)
        {
            var giveUpAt = DateTimeOffset.UtcNow + Patience;
            while (true)
            {
                var job = JobOn(platform);
                if (isDone(job))
                {
                    return job;
                }

                Assert.True(DateTimeOffset.UtcNow < giveUpAt, $"The {platform} job is stuck: {job.State}, {job.LastError}");
                await Task.Delay(20);
            }
        }

        public async Task<JobRow> RowOfAsync(Platform platform)
        {
            var id = JobOn(platform).Id;
            var giveUpAt = DateTimeOffset.UtcNow + Patience;
            while (true)
            {
                lock (_uiThread)
                {
                    var row = Queue.Rows.FirstOrDefault(found => found.Id == id && found.State == JobOn(platform).State);
                    if (row is not null)
                    {
                        return row;
                    }
                }

                Assert.True(DateTimeOffset.UtcNow < giveUpAt, "The queue screen didn't catch up.");
                await Task.Delay(20);
            }
        }

        /// <summary>The app closing. Jobs stay as they are on disk.</summary>
        public async Task CloseAsync()
        {
            if (!_closed)
            {
                _closed = true;
                await Services.Queue.DisposeAsync();
            }
        }
    }
}
