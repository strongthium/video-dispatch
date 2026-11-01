using VideoDispatch.Core.Accounts;
using VideoDispatch.Core.Media;
using VideoDispatch.Core.Model;
using VideoDispatch.Core.Presets;
using VideoDispatch.Core.Queue;
using VideoDispatch.Core.Templates;

namespace VideoDispatch.Core.Tests.Queue;

public sealed class QueueStorageAndChecksTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(AppContext.BaseDirectory, "store-" + Guid.NewGuid().ToString("N"));
    private readonly JsonFolderStore<UploadJob> _store;

    public QueueStorageAndChecksTests() => _store = new JsonFolderStore<UploadJob>(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static UploadJob NewJob() => new()
    {
        Id = Guid.NewGuid(),
        BatchId = Guid.NewGuid(),
        SourcePath = "clip.mp4",
        SourceLength = 10,
        SourceLastWriteUtc = Now,
        Details = new YouTubePostDetails { Title = "Title", Tags = ["a"], Privacy = YouTubePrivacy.Private, MadeForKids = false },
        CreatedAt = Now,
    };

    private static AccountStatus Account(Platform platform, ConnectionState state, params string[] scopes) =>
        new(platform, true, state, "Sample", scopes);

    private static YouTubePostDetails YouTube() => new() { Title = "Title", Privacy = YouTubePrivacy.Private, MadeForKids = false };

    private static VideoMetadata Good() => new(1080, 1920, 0, TimeSpan.FromSeconds(30), 30, "avc1");

    [Fact]
    public void A_folder_that_doesnt_exist_yet_is_just_empty()
    {
        var load = _store.LoadAll();

        Assert.Empty(load.Items);
        Assert.Empty(load.Damaged);
    }

    [Fact]
    public void Saves_and_reads_back_and_replaces()
    {
        var job = NewJob();
        _store.Save(job.Id, job);
        _store.Save(job.Id, job with { State = JobState.Completed });

        var read = Assert.Single(_store.LoadAll().Items);

        Assert.Equal(JobState.Completed, read.State);
        Assert.Equal(job.Id, read.Id);
        Assert.IsType<YouTubePostDetails>(read.Details);
    }

    [Fact]
    public void Deletes()
    {
        var job = NewJob();
        _store.Save(job.Id, job);

        _store.Delete(job.Id);
        _store.Delete(job.Id);

        Assert.Empty(_store.LoadAll().Items);
    }

    [Fact]
    public void Leaves_nothing_behind_but_the_finished_file()
    {
        var job = NewJob();
        _store.Save(job.Id, job);

        Assert.Equal($"{job.Id:N}.json", Path.GetFileName(Directory.GetFiles(_folder).Single()));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("""{"id":"00000000-0000-0000-0000-000000000001"}""")]
    [InlineData("""{"id":"00000000-0000-0000-0000-000000000001","batchId":"00000000-0000-0000-0000-000000000001","sourcePath":"a","sourceLength":1,"sourceLastWriteUtc":"2026-01-01T00:00:00Z","createdAt":"2026-01-01T00:00:00Z","details":{"title":"no type"}}""")]
    public void A_damaged_file_is_set_aside_and_loses_nothing_else(string content)
    {
        var job = NewJob();
        _store.Save(job.Id, job);
        File.WriteAllText(Path.Combine(_folder, "broken.json"), content);

        var load = _store.LoadAll();

        Assert.Equal(job.Id, Assert.Single(load.Items).Id);
        Assert.Equal(["broken.json"], load.Damaged);
        Assert.True(File.Exists(Path.Combine(_folder, "broken.json.damaged")));

        // It's reported once, and kept for whoever wants to look at it.
        Assert.Empty(_store.LoadAll().Damaged);
    }

    [Fact]
    public void A_write_cut_off_part_way_leaves_the_last_good_copy()
    {
        var job = NewJob();
        _store.Save(job.Id, job);
        File.WriteAllText(Path.Combine(_folder, $"{job.Id:N}.json.tmp"), """{"id":""");

        var load = _store.LoadAll();

        Assert.Equal(job.Id, Assert.Single(load.Items).Id);
        Assert.Empty(load.Damaged);
        Assert.False(File.Exists(Path.Combine(_folder, $"{job.Id:N}.json.tmp")));
    }

    [Fact]
    public void The_saved_file_holds_no_session_address()
    {
        var job = NewJob();
        _store.Save(job.Id, job);

        var text = File.ReadAllText(Directory.GetFiles(_folder).Single());

        Assert.DoesNotContain("session", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Keeps_presets_too()
    {
        var store = new JsonFolderStore<TikTokPreset>(_folder);
        var preset = new TikTokPreset { Id = Guid.NewGuid(), Name = "Clips", Hashtags = ["fun"] };

        store.Save(preset.Id, preset);

        Assert.Equal("Clips", Assert.Single(store.LoadAll().Items).Name);
    }

    [Fact]
    public void A_good_post_passes()
    {
        var check = QueueChecks.Check(YouTube(), "a.mp4", 100, Good(), Account(Platform.YouTube, ConnectionState.Connected), Now);

        Assert.True(check.CanQueue);
        Assert.Empty(check.Warnings);
    }

    [Fact]
    public void Runs_the_post_details_checks()
    {
        var check = QueueChecks.Check(YouTube() with { MadeForKids = null }, "a.mp4", 100, Good(), Account(Platform.YouTube, ConnectionState.Connected), Now);

        Assert.Contains("Choose whether the video is made for kids.", check.Problems);
    }

    [Theory]
    [InlineData("Hello {titel}")]
    [InlineData("Hello {title}")]
    public void A_variable_left_in_the_text_stops_it_reaching_a_public_post(string title)
    {
        var check = QueueChecks.Check(YouTube() with { Title = title }, "a.mp4", 100, Good(), Account(Platform.YouTube, ConnectionState.Connected), Now);

        Assert.False(check.CanQueue);
    }

    [Fact]
    public void Doubled_braces_are_not_a_variable()
    {
        Assert.Empty(TemplateRenderer.FindVariables("Use {{this}} and { that } and {}"));
    }

    [Fact]
    public void Checks_a_TikTok_caption_for_variables_too()
    {
        var check = QueueChecks.Check(new TikTokPostDetails { Caption = "{oops}" }, "a.mp4", 100, Good(), Account(Platform.TikTok, ConnectionState.Connected), Now);

        Assert.False(check.CanQueue);
    }

    [Fact]
    public void TikToks_limits_block_and_unknowns_warn()
    {
        var connected = Account(Platform.TikTok, ConnectionState.Connected);

        Assert.False(QueueChecks.Check(new TikTokPostDetails(), "a.avi", 100, Good(), connected, Now).CanQueue);

        var unknown = QueueChecks.Check(new TikTokPostDetails(), "a.mp4", 100, null, connected, Now);
        Assert.True(unknown.CanQueue);
        Assert.NotEmpty(unknown.Warnings);
    }

    [Fact]
    public void YouTubes_size_limit_blocks()
    {
        var check = QueueChecks.Check(YouTube(), "a.mp4", 0, Good(), Account(Platform.YouTube, ConnectionState.Connected), Now);

        Assert.Contains("The file is empty.", check.Problems);
    }

    [Theory]
    [InlineData(ConnectionState.NotConnected)]
    [InlineData(ConnectionState.NeedsReconnect)]
    public void An_account_that_isnt_working_blocks(ConnectionState state)
    {
        var check = QueueChecks.Check(YouTube(), "a.mp4", 100, Good(), Account(Platform.YouTube, state), Now);

        Assert.False(check.CanQueue);
    }

    [Fact]
    public void A_playlist_needs_the_permission_for_it()
    {
        var details = YouTube() with { PlaylistId = "PL1" };

        var without = QueueChecks.Check(details, "a.mp4", 100, Good(), Account(Platform.YouTube, ConnectionState.Connected, GoogleOAuthProvider.UploadScope), Now);
        var with = QueueChecks.Check(details, "a.mp4", 100, Good(), Account(Platform.YouTube, ConnectionState.Connected, GoogleOAuthProvider.PlaylistScope), Now);

        Assert.False(without.CanQueue);
        Assert.True(with.CanQueue);
    }
}
