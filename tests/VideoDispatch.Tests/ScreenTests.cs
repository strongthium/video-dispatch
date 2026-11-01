using VideoDispatch.App;
using VideoDispatch.Core.Accounts;
using VideoDispatch.Core.Media;
using VideoDispatch.Core.Model;
using VideoDispatch.Core.Presets;
using VideoDispatch.Core.Queue;
using VideoDispatch.Core.Uploads;

namespace VideoDispatch.Tests;

public sealed class NewPostPanelTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly VideoMetadata Vertical = new(1080, 1920, 0, TimeSpan.FromSeconds(47), 30, "avc1");

    private readonly string _folder = Path.Combine(AppContext.BaseDirectory, "new-post-" + Guid.NewGuid().ToString("N"));
    private readonly string _video;
    private readonly Records<UploadJob> _jobs = new();
    private readonly Records<TikTokPreset> _tikTokPresets = new();
    private readonly Records<YouTubePreset> _youTubePresets = new();
    private readonly UploadQueue _queue;
    private readonly PresetLibrary _library;
    private Func<Platform, AccountStatus> _account = Accounts.Connected;
    private VideoMetadata? _metadata = Vertical;
    private readonly NewPostPanel _panel;

    public NewPostPanelTests()
    {
        Directory.CreateDirectory(_folder);
        _video = Path.Combine(_folder, "lake-morning.mp4");
        File.WriteAllBytes(_video, new byte[1024 * 1024]);

        var time = new FixedTime(Now);
        _queue = new UploadQueue(_jobs, [new HeldUploader(Platform.TikTok), new HeldUploader(Platform.YouTube)], new NoSessions(), new StubYouTube(), new PassThroughRenderStep(), Accounts.Connected, time);
        _library = new PresetLibrary(_tikTokPresets, _youTubePresets);
        _panel = new NewPostPanel(_queue, _library, new YouTubeChoices(new StubYouTube(), Accounts.Connected), platform => _account(platform), _ => _metadata, time, TimeZoneInfo.Utc);
    }

    public async ValueTask DisposeAsync()
    {
        await _queue.DisposeAsync();
        Directory.Delete(_folder, recursive: true);
    }

    private async Task ReadyAsync()
    {
        await _panel.RefreshAsync();
        await _panel.ChooseVideoAsync(_video);
    }

    private void ChooseEverything()
    {
        _panel.PrivacyId = "Private";
        _panel.MadeForKidsId = YouTubeChoices.NoId;
    }

    [Fact]
    public async Task Shows_what_is_in_the_video()
    {
        await ReadyAsync();

        Assert.Equal("lake-morning.mp4", _panel.FileName);
        Assert.Contains("1080 × 1920", _panel.MetadataText);
        Assert.Contains("0:47", _panel.MetadataText);
        Assert.Contains("30 fps", _panel.MetadataText);
        Assert.Contains("avc1", _panel.MetadataText);
        Assert.Contains("Short", _panel.ShortsText);
    }

    [Fact]
    public async Task Says_so_when_a_video_cant_be_read()
    {
        _metadata = null;
        await ReadyAsync();

        Assert.Contains("couldn't be read", _panel.MetadataText);
        Assert.Contains("Couldn't tell", _panel.ShortsText);
        Assert.NotEmpty(_panel.Warnings);
    }

    [Fact]
    public void Nothing_can_be_queued_without_a_video()
    {
        Assert.False(_panel.CanQueue);
        Assert.False(_panel.HasVideo);
    }

    [Fact]
    public async Task Privacy_and_made_for_kids_start_unchosen_and_block_queueing()
    {
        await ReadyAsync();

        Assert.False(_panel.CanQueue);
        Assert.Contains("Choose a privacy setting.", _panel.Problems);
        Assert.Contains("Choose whether the video is made for kids.", _panel.Problems);

        ChooseEverything();

        Assert.True(_panel.CanQueue);
    }

    [Fact]
    public async Task The_preview_says_exactly_what_will_be_sent()
    {
        await ReadyAsync();
        ChooseEverything();
        _panel.YouTubeTitle = "Lake at first light";
        _panel.Description = "A quiet morning.";
        _panel.Tags = "lake, morning, lake";
        _panel.Caption = "Morning #lake";

        var post = _panel.BuildPosts().OfType<YouTubePostDetails>().Single();

        Assert.Contains("Title: Lake at first light", _panel.YouTubePreview);
        Assert.Contains("Description: A quiet morning.", _panel.YouTubePreview);
        Assert.Contains("Tags: lake, morning", _panel.YouTubePreview);
        Assert.Contains("Privacy: Private", _panel.YouTubePreview);
        Assert.Contains("Made for kids: No", _panel.YouTubePreview);
        Assert.Equal(["lake", "morning"], post.Tags);
        Assert.Contains("Morning #lake", _panel.TikTokPreview);
        Assert.Contains("TikTok inbox", _panel.TikTokPreview);
    }

    [Fact]
    public async Task A_preset_fills_the_fields_and_everything_stays_editable()
    {
        await _library.SaveAsync(new YouTubePreset
        {
            Id = Guid.NewGuid(),
            Name = "Vlog",
            TitleTemplate = "{filename} | Trail Notes",
            DescriptionTemplate = "Filmed {date}.",
            Tags = ["hiking"],
            Privacy = YouTubePrivacy.Unlisted,
            MadeForKids = false,
        });
        await ReadyAsync();

        _panel.YouTubePreset = _library.YouTube.Single();

        Assert.Equal("lake-morning | Trail Notes", _panel.YouTubeTitle);
        Assert.Equal("Filmed 2026-11-01.", _panel.Description);
        Assert.Equal("hiking", _panel.Tags);
        Assert.Equal("Unlisted", _panel.PrivacyId);
        Assert.Equal(YouTubeChoices.NoId, _panel.MadeForKidsId);

        _panel.YouTubeTitle = "My own title";

        Assert.Equal("My own title", ((YouTubePostDetails)_panel.BuildPosts().Last()).Title);
    }

    [Fact]
    public async Task A_TikTok_preset_fills_the_caption()
    {
        await _library.SaveAsync(new TikTokPreset { Id = Guid.NewGuid(), Name = "Clips", CaptionTemplate = "{title}", Hashtags = ["fun"] });
        await ReadyAsync();

        _panel.TikTokPreset = _library.TikTok.Single();

        Assert.Equal("lake-morning #fun", _panel.Caption);
    }

    [Fact]
    public async Task A_presets_schedule_shows_as_a_date_and_time()
    {
        await _library.SaveAsync(new YouTubePreset
        {
            Id = Guid.NewGuid(),
            Name = "Later",
            Privacy = YouTubePrivacy.Private,
            MadeForKids = false,
            Schedule = new YouTubeSchedule(2, new TimeOnly(18, 30)),
        });
        await ReadyAsync();

        _panel.YouTubePreset = _library.YouTube.Single();

        Assert.True(_panel.Scheduled);
        Assert.Equal(new DateTime(2026, 11, 3), _panel.ScheduleDate);
        Assert.Equal("18:30", _panel.ScheduleTime);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 18, 30, 0, TimeSpan.Zero), ((YouTubePostDetails)_panel.BuildPosts().Last()).PublishAt);
    }

    [Fact]
    public async Task A_time_it_cant_read_blocks_rather_than_dropping_the_schedule()
    {
        await ReadyAsync();
        ChooseEverything();
        _panel.Scheduled = true;
        _panel.ScheduleTime = "soon";

        Assert.False(_panel.CanQueue);
        Assert.Contains(_panel.Problems, problem => problem.Contains("18:30"));
    }

    [Fact]
    public async Task A_schedule_needs_the_video_to_be_private()
    {
        await ReadyAsync();
        ChooseEverything();
        _panel.PrivacyId = "Public";
        _panel.Scheduled = true;

        Assert.False(_panel.CanQueue);
    }

    [Fact]
    public async Task A_variable_left_in_the_text_blocks_queueing()
    {
        await ReadyAsync();
        ChooseEverything();
        _panel.YouTubeTitle = "Hello {titel}";

        Assert.False(_panel.CanQueue);
    }

    [Fact]
    public async Task Choosing_neither_platform_blocks_queueing()
    {
        await ReadyAsync();
        _panel.PostToTikTok = false;
        _panel.PostToYouTube = false;

        Assert.Contains("Choose where to post this video.", _panel.Problems);
    }

    [Fact]
    public async Task Only_the_chosen_platforms_are_checked()
    {
        await ReadyAsync();
        _panel.PostToYouTube = false;

        Assert.True(_panel.CanQueue);
        Assert.Equal("", _panel.YouTubePreview);
    }

    [Fact]
    public async Task An_account_that_isnt_connected_blocks_queueing()
    {
        _account = Accounts.NotConnected;
        await ReadyAsync();
        ChooseEverything();

        Assert.False(_panel.CanQueue);
        Assert.Contains("Connect your YouTube account first.", _panel.Problems);
    }

    [Fact]
    public async Task A_playlist_without_its_permission_blocks_queueing()
    {
        _account = Accounts.ConnectedWithoutPlaylists;
        await ReadyAsync();
        ChooseEverything();
        _panel.PlaylistId = "PL1";

        Assert.False(_panel.CanQueue);
    }

    [Fact]
    public async Task Queueing_adds_a_job_for_each_platform_and_clears_the_form()
    {
        await ReadyAsync();
        ChooseEverything();
        _panel.Caption = "Hello";
        _panel.YouTubeTitle = "Title";

        await _panel.QueueAsync();

        var jobs = _queue.Jobs;
        Assert.Equal(2, jobs.Count);
        Assert.Single(jobs.Select(job => job.BatchId).Distinct());
        Assert.Equal("Hello", jobs.Select(job => job.Details).OfType<TikTokPostDetails>().Single().Caption);
        Assert.Equal(1024 * 1024, jobs[0].SourceLength);

        Assert.False(_panel.HasVideo);
        Assert.Equal("", _panel.YouTubeTitle);
        Assert.Null(_panel.PrivacyId);
        Assert.Contains("Queued", _panel.Message);
        Assert.False(_panel.IsBusy);
    }

    [Fact]
    public async Task A_refusal_at_the_last_moment_shows_why_and_keeps_the_form()
    {
        await ReadyAsync();
        ChooseEverything();
        File.Delete(_video);

        await _panel.QueueAsync();

        Assert.Empty(_queue.Jobs);
        Assert.Contains("The video file is gone.", _panel.Problems);
        Assert.True(_panel.HasVideo);
    }

    [Fact]
    public async Task Defaults_the_title_to_the_file_name()
    {
        await ReadyAsync();

        Assert.Equal("lake-morning", _panel.YouTubeTitle);
    }

    [Fact]
    public async Task The_text_follows_a_new_video_until_the_user_writes_over_it()
    {
        var other = Path.Combine(_folder, "ridge-walk.mp4");
        File.WriteAllBytes(other, new byte[10]);
        await ReadyAsync();

        await _panel.ChooseVideoAsync(other);
        Assert.Equal("ridge-walk", _panel.YouTubeTitle);

        _panel.YouTubeTitle = "My own title";
        await _panel.ChooseVideoAsync(_video);

        Assert.Equal("My own title", _panel.YouTubeTitle);
    }

    [Fact]
    public async Task Choosing_a_video_keeps_a_caption_the_user_wrote_after_picking_a_preset()
    {
        await _library.SaveAsync(new TikTokPreset { Id = Guid.NewGuid(), Name = "Clips", CaptionTemplate = "{filename}" });
        _panel.TikTokPreset = _library.TikTok.Single();
        _panel.Caption = "Written by hand";

        await ReadyAsync();

        Assert.Equal("Written by hand", _panel.Caption);
    }

    [Fact]
    public async Task Choosing_a_preset_replaces_what_was_there()
    {
        await _library.SaveAsync(new TikTokPreset { Id = Guid.NewGuid(), Name = "Clips", CaptionTemplate = "{filename}" });
        await ReadyAsync();
        _panel.Caption = "Written by hand";

        _panel.TikTokPreset = _library.TikTok.Single();

        Assert.Equal("lake-morning", _panel.Caption);
    }

    [Fact]
    public async Task The_empty_entry_in_a_list_means_nothing_is_chosen()
    {
        await ReadyAsync();
        _panel.PrivacyId = "Private";
        _panel.PlaylistId = "PL1";
        _panel.CategoryId = "19";

        _panel.PrivacyId = Choice.None;
        _panel.PlaylistId = Choice.None;
        _panel.CategoryId = Choice.None;

        var post = _panel.BuildPosts().OfType<YouTubePostDetails>().Single();
        Assert.Null(post.Privacy);
        Assert.Null(post.PlaylistId);
        Assert.Null(post.CategoryId);
        Assert.Contains("No playlist", _panel.YouTubePreview);
        Assert.Contains("People & Blogs", _panel.YouTubePreview);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

public sealed class QueuePanelTests : IAsyncDisposable
{
    private readonly Records<UploadJob> _jobs = new();
    private readonly HeldUploader _youTube = new(Platform.YouTube);
    private readonly HeldUploader _tikTok = new(Platform.TikTok);
    private readonly UploadQueue _queue;

    public QueuePanelTests()
    {
        _queue = new UploadQueue(_jobs, [_tikTok, _youTube], new NoSessions(), new StubYouTube(), new PassThroughRenderStep(), Accounts.Connected, TimeProvider.System);
    }

    public async ValueTask DisposeAsync() => await _queue.DisposeAsync();

    private static UploadJob Job(JobState state, PostDetails? details = null, string? remoteId = null) => new()
    {
        Id = Guid.NewGuid(),
        BatchId = Guid.NewGuid(),
        SourcePath = @"C:\Videos\clip.mp4",
        SourceLength = 10 * 1024 * 1024,
        TotalBytes = 10 * 1024 * 1024,
        SourceLastWriteUtc = DateTimeOffset.UnixEpoch,
        Details = details ?? new YouTubePostDetails { Title = "Title", Privacy = YouTubePrivacy.Private, MadeForKids = false },
        CreatedAt = DateTimeOffset.UnixEpoch,
        State = state,
        RemoteId = remoteId,
    };

    private JobRow RowFor(UploadJob job)
    {
        _jobs.Saved[job.Id] = job;
        _queue.Start();
        return new QueuePanel(_queue, action => action()).Rows.Single();
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "It never happened.");
            await Task.Delay(5);
        }
    }

    [Fact]
    public void Says_how_far_an_upload_has_got()
    {
        var row = new JobRow(Job(JobState.Uploading) with { BytesSent = 5 * 1024 * 1024 });

        Assert.Equal(50, row.Percent);
        Assert.Contains("5.0 MB of 10.0 MB", row.StateText);
        Assert.True(row.ShowsProgress);
        Assert.False(row.ProgressIsUnknown);
    }

    [Fact]
    public void A_job_waiting_on_the_creator_says_to_finish_in_TikTok_and_offers_the_caption()
    {
        var row = new JobRow(Job(JobState.AwaitingCreator, new TikTokPostDetails { Caption = "Hello #fun" }, "publish"));

        Assert.Contains("Open TikTok", row.StateText);
        Assert.True(row.ShowsCaption);
        Assert.Equal("Hello #fun", row.Caption);
        Assert.True(row.CanMarkPosted);
    }

    [Fact]
    public void Shows_no_caption_box_when_there_is_no_caption()
    {
        Assert.False(new JobRow(Job(JobState.AwaitingCreator, new TikTokPostDetails(), "publish")).ShowsCaption);
    }

    [Fact]
    public void Shows_the_error_on_a_failed_job()
    {
        var row = new JobRow(Job(JobState.Failed) with { LastError = "The video file is gone." });

        Assert.Equal("Failed", row.StateText);
        Assert.Equal("The video file is gone.", row.Detail);
        Assert.True(row.IsProblem);
        Assert.True(row.CanRetry);
        Assert.True(row.CanRemove);
        Assert.False(row.CanCancel);
    }

    [Fact]
    public void Shows_why_a_job_is_waiting_and_lets_it_try_now()
    {
        var row = new JobRow(Job(JobState.Waiting) with { NextAttemptAt = DateTimeOffset.Now.AddHours(1), LastError = "Out of quota." });

        Assert.StartsWith("Trying again", row.StateText);
        Assert.Equal("Out of quota.", row.Detail);
        Assert.Equal("Try now", row.RetryText);
    }

    [Fact]
    public void Before_the_platform_has_the_video_cancel_means_cancel()
    {
        var row = new JobRow(Job(JobState.Uploading));

        Assert.Equal("Cancel", row.CancelText);
    }

    [Fact]
    public void Once_the_platform_has_the_video_the_button_must_not_suggest_it_can_be_taken_back()
    {
        var row = new JobRow(Job(JobState.Processing, remoteId: "video"));

        Assert.Equal("Stop following", row.CancelText);
        Assert.Contains("stays on YouTube", row.CancelHint);
    }

    [Fact]
    public void Retrying_a_failed_video_the_platform_has_reads_as_checking_not_uploading()
    {
        Assert.Equal("Check again", new JobRow(Job(JobState.Failed, remoteId: "video")).RetryText);
        Assert.Equal("Try again", new JobRow(Job(JobState.Failed)).RetryText);
    }

    [Fact]
    public void Shows_warnings_and_playlist_problems()
    {
        var row = new JobRow(Job(JobState.Completed) with { Warning = "Stayed private.", PlaylistError = "That playlist is full." });

        Assert.Contains("Stayed private.", row.Detail);
        Assert.Contains("That playlist is full.", row.Detail);
        Assert.True(row.IsProblem);
    }

    [Fact]
    public void Links_to_YouTube_Studio_only_when_there_is_a_video()
    {
        Assert.False(new JobRow(Job(JobState.Queued)).HasStudioLink);
        Assert.Equal("https://studio.youtube.com/video/abc/edit", new JobRow(Job(JobState.Completed, remoteId: "abc")).StudioLink?.AbsoluteUri);
        Assert.False(new JobRow(Job(JobState.Completed, new TikTokPostDetails(), "abc")).HasStudioLink);
    }

    [Fact]
    public void Every_state_has_words()
    {
        // Failed and Cancelled are already the right words.
        foreach (var state in Enum.GetValues<JobState>().Except([JobState.Failed, JobState.Cancelled]))
        {
            Assert.NotEqual(state.ToString(), new JobRow(Job(state)).StateText);
        }
    }

    [Fact]
    public void A_title_falls_back_to_the_file_name()
    {
        Assert.Equal("Title", new JobRow(Job(JobState.Queued)).Title);
        Assert.Equal("clip.mp4", new JobRow(Job(JobState.Queued, new TikTokPostDetails())).Title);
    }

    [Fact]
    public async Task Shows_jobs_as_they_change_and_goes_through_the_UI_thread()
    {
        var posted = 0;
        var panel = new QueuePanel(_queue, action =>
        {
            Interlocked.Increment(ref posted);
            action();
        });
        _queue.Start();
        Assert.True(panel.IsEmpty);

        var job = Job(JobState.Failed);
        _queue.Add(Path.Combine(AppContext.BaseDirectory, "video-dispatch.dll"), null, [job.Details]);

        await UntilAsync(() => panel.Rows.Count == 1 && panel.Rows[0].State == JobState.Uploading);

        Assert.True(posted > 0);
        Assert.False(panel.IsEmpty);
    }

    [Fact]
    public async Task Cancel_and_retry_go_to_the_queue()
    {
        var row = RowFor(Job(JobState.Failed) with { LastError = "No." });
        var panel = new QueuePanel(_queue, action => action());
        row = panel.Rows.Single();

        await panel.RetryAsync(row);
        await UntilAsync(() => row.State == JobState.Uploading);

        await panel.CancelAsync(row);
        await UntilAsync(() => row.State == JobState.Cancelled);
    }

    [Fact]
    public async Task Removing_a_finished_job_takes_it_off_the_screen()
    {
        var job = Job(JobState.Completed);
        _jobs.Saved[job.Id] = job;
        _queue.Start();
        var panel = new QueuePanel(_queue, action => action());

        await panel.RemoveAsync(panel.Rows.Single());

        await UntilAsync(() => panel.IsEmpty);
    }

    [Fact]
    public async Task Telling_the_creator_posted_finishes_the_job()
    {
        var job = Job(JobState.AwaitingCreator, new TikTokPostDetails { Caption = "Hi" }, "publish");
        _jobs.Saved[job.Id] = job;
        _queue.Start();
        var panel = new QueuePanel(_queue, action => action());
        var row = panel.Rows.Single();

        await panel.MarkPostedAsync(row);
        await UntilAsync(() => row.State == JobState.Completed);
    }

    [Fact]
    public void Says_when_a_platform_is_paused_or_saved_jobs_were_damaged()
    {
        var job = Job(JobState.NeedsReconnect);
        _jobs.Saved[job.Id] = job;
        _queue.Start();
        var panel = new QueuePanel(_queue, action => action());

        Assert.Contains("YouTube uploads are paused", panel.Notice);

        panel.ReportDamaged(["broken.json"]);

        Assert.Contains("1 saved job couldn't be read", panel.Notice);
    }

    [Fact]
    public void Newest_jobs_come_first()
    {
        var older = Job(JobState.Completed) with { CreatedAt = DateTimeOffset.UnixEpoch };
        var newer = Job(JobState.Completed) with { CreatedAt = DateTimeOffset.UnixEpoch.AddDays(1) };
        _jobs.Saved[older.Id] = older;
        _jobs.Saved[newer.Id] = newer;
        _queue.Start();

        var panel = new QueuePanel(_queue, action => action());

        Assert.Equal(newer.Id, panel.Rows[0].Id);
    }
}

public sealed class PresetsPanelTests
{
    private readonly Records<TikTokPreset> _tikTokStore = new();
    private readonly Records<YouTubePreset> _youTubeStore = new();
    private readonly PresetLibrary _library;
    private readonly PresetsPanel _panel;

    public PresetsPanelTests()
    {
        _library = new PresetLibrary(_tikTokStore, _youTubeStore);
        _panel = new PresetsPanel(_library, new YouTubeChoices(new StubYouTube(), Accounts.Connected), new DateOnly(2026, 11, 1));
    }

    [Fact]
    public void Says_which_variables_are_wrong_as_the_user_types()
    {
        var editor = _panel.TikTok;

        editor.CaptionTemplate = "{title}";
        Assert.False(editor.HasVariablesProblem);

        editor.CaptionTemplate = "{titel} and {oops}";
        Assert.Contains("{titel}", editor.VariablesProblem);
        Assert.Contains("{oops}", editor.VariablesProblem);
        Assert.Contains("{title}, {filename} and {date}", editor.VariablesProblem);
    }

    [Fact]
    public void Tells_the_view_as_the_user_types()
    {
        var told = 0;
        _panel.TikTok.PropertyChanged += (_, _) => told++;

        _panel.TikTok.CaptionTemplate = "{oops}";

        Assert.True(told > 0);
    }

    [Fact]
    public void Checks_both_YouTube_templates()
    {
        _panel.YouTube.TitleTemplate = "{oops1}";
        _panel.YouTube.DescriptionTemplate = "{oops2}";

        Assert.Contains("{oops1}", _panel.YouTube.VariablesProblem);
        Assert.Contains("{oops2}", _panel.YouTube.VariablesProblem);
    }

    [Fact]
    public void A_preset_needs_a_name_and_good_templates_to_save()
    {
        var editor = _panel.TikTok;
        Assert.False(editor.CanSave);

        editor.Name = "Clips";
        Assert.True(editor.CanSave);

        editor.CaptionTemplate = "{oops}";
        Assert.False(editor.CanSave);
    }

    [Fact]
    public async Task Saves_edits_and_deletes_a_TikTok_preset()
    {
        var editor = _panel.TikTok;
        editor.Name = "Clips";
        editor.CaptionTemplate = "{title}!";
        editor.Hashtags = "#fun outdoors, fun";

        await editor.SaveAsync();

        var saved = _tikTokStore.Saved.Values.Single();
        Assert.Equal("Clips", saved.Name);
        Assert.Equal("{title}!", saved.CaptionTemplate);
        Assert.Equal(["#fun", "#outdoors"], saved.Hashtags);
        Assert.Same(saved, editor.Selected);
        Assert.Equal("Saved.", editor.Message);

        editor.Name = "Renamed";
        await editor.SaveAsync();

        Assert.Equal("Renamed", _tikTokStore.Saved.Values.Single().Name);
        Assert.Single(editor.Items);

        await editor.DeleteAsync();

        Assert.Empty(_tikTokStore.Saved);
        Assert.Empty(editor.Items);
        Assert.False(editor.HasSelection);
    }

    [Fact]
    public void Shows_what_a_template_makes_for_an_example_video()
    {
        _panel.TikTok.CaptionTemplate = "{title} from {filename}";
        _panel.TikTok.Hashtags = "fun";

        Assert.Equal("Holiday clip from holiday-clip #fun", _panel.TikTok.Example_);
    }

    [Fact]
    public async Task Saves_and_reads_back_every_YouTube_field()
    {
        var editor = _panel.YouTube;
        editor.Name = "Vlog";
        editor.TitleTemplate = "{title} | Notes";
        editor.DescriptionTemplate = "Filmed {date}.";
        editor.Tags = "a, b";
        editor.CategoryId = "19";
        editor.PrivacyId = "Unlisted";
        editor.MadeForKidsId = YouTubeChoices.NoId;
        editor.PlaylistId = "PL1";
        editor.Scheduled = true;
        editor.DaysAhead = "2";
        editor.ScheduleTime = "18:30";

        await editor.SaveAsync();

        var saved = _youTubeStore.Saved.Values.Single();
        Assert.Equal(YouTubePrivacy.Unlisted, saved.Privacy);
        Assert.False(saved.MadeForKids);
        Assert.Equal(new YouTubeSchedule(2, new TimeOnly(18, 30)), saved.Schedule);

        editor.New();
        Assert.Equal("", editor.Name);
        Assert.Null(editor.PrivacyId);

        editor.Selected = saved;

        Assert.Equal("Vlog", editor.Name);
        Assert.Equal("a, b", editor.Tags);
        Assert.Equal("19", editor.CategoryId);
        Assert.Equal("Unlisted", editor.PrivacyId);
        Assert.Equal(YouTubeChoices.NoId, editor.MadeForKidsId);
        Assert.Equal("PL1", editor.PlaylistId);
        Assert.True(editor.Scheduled);
        Assert.Equal("2", editor.DaysAhead);
        Assert.Equal("18:30", editor.ScheduleTime);
    }

    [Fact]
    public async Task Privacy_can_be_left_to_each_video()
    {
        var editor = _panel.YouTube;
        editor.Name = "Open";

        await editor.SaveAsync();

        Assert.Null(_youTubeStore.Saved.Values.Single().Privacy);
        Assert.Null(_youTubeStore.Saved.Values.Single().MadeForKids);
    }

    [Theory]
    [InlineData("x", "18:30")]
    [InlineData("-1", "18:30")]
    [InlineData("2", "soon")]
    public void A_schedule_it_cant_read_stops_the_save(string days, string time)
    {
        var editor = _panel.YouTube;
        editor.Name = "Bad";
        editor.Scheduled = true;
        editor.DaysAhead = days;
        editor.ScheduleTime = time;

        Assert.False(editor.CanSave);
        Assert.True(editor.HasExtraProblem);
    }

    [Fact]
    public async Task Loads_saved_presets_in_name_order_and_reports_damage()
    {
        var zed = new TikTokPreset { Id = Guid.NewGuid(), Name = "Zed" };
        var alpha = new TikTokPreset { Id = Guid.NewGuid(), Name = "alpha" };
        _tikTokStore.Saved[zed.Id] = zed;
        _tikTokStore.Saved[alpha.Id] = alpha;
        _tikTokStore.Damaged.Add("broken.json");

        await _library.LoadAsync();

        Assert.Equal(["alpha", "Zed"], _library.TikTok.Select(preset => preset.Name));
        Assert.Equal(["broken.json"], _library.Damaged);
    }
}

public sealed class YouTubeChoicesTests
{
    [Fact]
    public async Task Offers_the_channels_lists_once_connected_and_asks_only_once()
    {
        var api = new StubYouTube();
        var choices = new YouTubeChoices(api, Accounts.Connected);

        await choices.LoadAsync();
        await choices.LoadAsync();

        Assert.Equal(1, api.CategoryCalls);
        Assert.Contains(choices.Categories, choice => choice.Id == "19");
        Assert.Contains(choices.Playlists, choice => choice.Id == "PL1");
        Assert.Equal(Choice.None, choices.Categories[0].Id);
        Assert.Equal(Choice.None, choices.Playlists[0].Id);
    }

    [Fact]
    public void A_screen_reader_hears_the_title_not_the_data()
    {
        Assert.Equal("Private", new Choice("Private", "Private").ToString());
        Assert.Equal("Clips", new TikTokPreset { Id = Guid.NewGuid(), Name = "Clips" }.ToString());
        Assert.Equal("Vlog", new YouTubePreset { Id = Guid.NewGuid(), Name = "Vlog" }.ToString());
    }

    [Fact]
    public async Task Doesnt_ask_for_playlists_without_the_permission()
    {
        var choices = new YouTubeChoices(new StubYouTube(), Accounts.ConnectedWithoutPlaylists);

        await choices.LoadAsync();

        Assert.Single(choices.Playlists);
    }

    [Fact]
    public async Task Doesnt_ask_when_not_connected_and_asks_again_after_connecting()
    {
        var api = new StubYouTube();
        var account = Accounts.NotConnected(Platform.YouTube);
        var choices = new YouTubeChoices(api, _ => account);

        await choices.LoadAsync();
        Assert.Equal(0, api.CategoryCalls);

        account = Accounts.Connected(Platform.YouTube);
        await choices.LoadAsync();
        Assert.Equal(1, api.CategoryCalls);

        choices.Forget();
        await choices.LoadAsync();
        Assert.Equal(2, api.CategoryCalls);
    }

    [Fact]
    public async Task A_failure_is_shown_and_the_defaults_still_work()
    {
        var api = new StubYouTube { Throw = new UploadException(Platform.YouTube, UploadFailureKind.Retryable, "Couldn't reach YouTube.") };
        var choices = new YouTubeChoices(api, Accounts.Connected);

        await choices.LoadAsync();

        Assert.Equal("Couldn't reach YouTube.", choices.Problem);
        Assert.Single(choices.Categories);

        api.Throw = null;
        await choices.LoadAsync();

        Assert.Null(choices.Problem);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("Private", YouTubePrivacy.Private)]
    [InlineData("Public", YouTubePrivacy.Public)]
    public void Reads_a_privacy_choice(string? id, YouTubePrivacy? expected)
    {
        Assert.Equal(expected, YouTubeChoices.PrivacyFrom(id));
    }

    [Fact]
    public async Task The_sample_has_every_kind_of_job_to_look_at()
    {
        // Only the states the app can show at rest: it moves the others along as soon as it starts.
        var services = await SampleServices.CreateAsync();
        services.Queue.Start();

        var states = services.Queue.Jobs.Select(job => job.State).ToHashSet();

        Assert.Contains(JobState.Completed, states);
        Assert.Contains(JobState.Failed, states);
        Assert.Contains(JobState.Waiting, states);
        await services.Queue.DisposeAsync();
    }
}
