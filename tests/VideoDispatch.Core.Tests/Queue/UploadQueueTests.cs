using System.Collections.Concurrent;
using VideoDispatch.Core.Accounts;
using VideoDispatch.Core.Model;
using VideoDispatch.Core.Queue;
using VideoDispatch.Core.Tests.YouTube;
using VideoDispatch.Core.Uploads;

namespace VideoDispatch.Core.Tests.Queue;

public sealed class UploadQueueTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    // Test files stay beside the test program, in the ignored bin folder.
    private readonly string _folder = Path.Combine(AppContext.BaseDirectory, "queue-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new(Start);
    private readonly FakeUploader _tikTok = new(Platform.TikTok);
    private readonly FakeUploader _youTube = new(Platform.YouTube);
    private readonly FakeYouTubeApi _api = new();
    private readonly MemoryRecords<UploadJob> _store = new();
    private readonly MemorySessions _sessions = new();
    private readonly List<UploadQueue> _queues = [];
    private readonly ConcurrentQueue<JobState> _states = new();
    private readonly string _video;
    private UploadQueue _queue;

    public UploadQueueTests()
    {
        Directory.CreateDirectory(_folder);
        _video = Path.Combine(_folder, "clip.mp4");
        File.WriteAllBytes(_video, new byte[1000]);
        _queue = NewQueue();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var queue in _queues)
        {
            await queue.DisposeAsync();
        }

        Directory.Delete(_folder, recursive: true);
    }

    private static TikTokPostDetails TikTok() => new() { Caption = "Hello" };

    private static YouTubePostDetails YouTube(string? playlist = null) => new()
    {
        Title = "Title",
        Privacy = YouTubePrivacy.Private,
        MadeForKids = false,
        PlaylistId = playlist,
    };

    private static AccountStatus Connected(Platform platform) => new(
        platform,
        true,
        ConnectionState.Connected,
        "Sample",
        [GoogleOAuthProvider.PlaylistScope]);

    private UploadQueue NewQueue(IRenderStep? render = null)
    {
        var queue = new UploadQueue(_store, [_tikTok, _youTube], _sessions, _api, render ?? new PassThroughRenderStep(), Connected, _time, () => 0);
        queue.JobChanged += job => _states.Enqueue(job.State);
        _queues.Add(queue);
        return queue;
    }

    private UploadJob Job(Guid id) => _queue.Jobs.Single(job => job.Id == id);

    private async Task<UploadJob> UntilAsync(Guid id, Func<UploadJob, bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            var job = Job(id);
            if (condition(job))
            {
                return job;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The job is still {job.State}: {job.LastError}");
            }

            await Task.Delay(5);
        }
    }

    private Task<UploadJob> UntilStateAsync(Guid id, JobState state) => UntilAsync(id, job => job.State == state);

    private static async Task SettleAsync() => await Task.Delay(100);

    private UploadJob Add(PostDetails details) => _queue.Add(_video, null, [details]).Single();

    private UploadJob Saved(JobState state, PostDetails? details = null, string? remoteId = null) => new()
    {
        Id = Guid.NewGuid(),
        BatchId = Guid.NewGuid(),
        SourcePath = _video,
        SourceLength = 1000,
        SourceLastWriteUtc = File.GetLastWriteTimeUtc(_video),
        Details = details ?? YouTube(),
        CreatedAt = Start,
        State = state,
        RemoteId = remoteId,
        UploadedAt = remoteId is null ? null : Start,
        NextAttemptAt = null,
    };

    private void SetUploadToFail(FakeUploader uploader, UploadFailureKind kind, DateTimeOffset? retryAt = null) =>
        uploader.Upload = (job, _, _) => throw new UploadException(job.Platform, kind, "From the platform.", retryAt);

    private void SetCheckToFail(FakeUploader uploader, UploadFailureKind kind, DateTimeOffset? retryAt = null) =>
        uploader.Check = (_, _) => throw new UploadException(uploader.Platform, kind, "From the platform.", retryAt);

    [Fact]
    public async Task A_YouTube_job_goes_through_every_state_to_completed()
    {
        _queue.Start();
        var job = Add(YouTube());

        await UntilStateAsync(job.Id, JobState.Completed);

        Assert.Equal(
            [JobState.Queued, JobState.Rendering, JobState.Uploading, JobState.Processing, JobState.Completed],
            _states.Where(state => true).Distinct());
        Assert.Equal(job.Id, _youTube.Uploads.Single());
    }

    [Fact]
    public async Task A_TikTok_job_waits_for_the_creator_and_finishes_when_they_post()
    {
        _tikTok.Check = (_, _) => Task.FromResult(JobState.AwaitingCreator);
        _queue.Start();
        var job = Add(TikTok());

        await UntilStateAsync(job.Id, JobState.AwaitingCreator);

        _tikTok.Check = (_, _) => Task.FromResult(JobState.Completed);
        await UntilAsync(job.Id, current => current.NextAttemptAt is not null);
        _time.Advance(TimeSpan.FromMinutes(15));

        await UntilStateAsync(job.Id, JobState.Completed);
    }

    [Fact]
    public async Task The_creator_can_say_they_posted_it()
    {
        _tikTok.Check = (_, _) => Task.FromResult(JobState.AwaitingCreator);
        _queue.Start();
        var job = Add(TikTok());
        await UntilStateAsync(job.Id, JobState.AwaitingCreator);

        _queue.MarkPosted(job.Id);

        Assert.Equal(JobState.Completed, Job(job.Id).State);
    }

    [Fact]
    public async Task Stops_asking_about_a_draft_after_a_week_but_keeps_the_job()
    {
        _tikTok.Check = (_, _) => Task.FromResult(JobState.AwaitingCreator);
        _queue.Start();
        var job = Add(TikTok());
        await UntilAsync(job.Id, current => current is { State: JobState.AwaitingCreator, NextAttemptAt: not null });

        _time.Advance(TimeSpan.FromDays(7));
        var given = await UntilAsync(job.Id, current => current.NextAttemptAt is null);

        Assert.Equal(JobState.AwaitingCreator, given.State);
    }

    [Fact]
    public async Task Records_the_receipt_on_the_job()
    {
        _youTube.Upload = (_, _, _) => Task.FromResult(new UploadReceipt("video-9", "Stayed private."));
        _youTube.Check = (_, _) => Task.FromResult(JobState.Processing);
        _queue.Start();
        var job = Add(YouTube());

        var processing = await UntilAsync(job.Id, current => current is { State: JobState.Processing, RemoteId: not null });

        Assert.Equal("video-9", processing.RemoteId);
        Assert.Equal("Stayed private.", processing.Warning);
        Assert.Equal(Start, processing.UploadedAt);
        Assert.Equal(1000, processing.BytesSent);
    }

    [Fact]
    public async Task Queueing_records_the_source_file_as_it_is()
    {
        var job = _queue.Add(_video, null, [TikTok()]).Single();

        Assert.Equal(1000, job.SourceLength);
        Assert.Equal(File.GetLastWriteTimeUtc(_video), job.SourceLastWriteUtc.UtcDateTime);
        Assert.Equal(1000, job.TotalBytes);
    }

    [Fact]
    public void A_video_for_both_platforms_is_two_jobs_in_one_batch()
    {
        var jobs = _queue.Add(_video, null, [TikTok(), YouTube()]);

        Assert.Equal(2, jobs.Count);
        Assert.Single(jobs.Select(job => job.BatchId).Distinct());
    }

    [Fact]
    public async Task One_platform_being_stuck_doesnt_hold_up_the_other()
    {
        var release = new TaskCompletionSource<UploadReceipt>();
        _tikTok.Upload = (_, _, _) => release.Task;
        _queue.Start();
        var jobs = _queue.Add(_video, null, [TikTok(), YouTube()]);

        await UntilStateAsync(jobs[1].Id, JobState.Completed);

        Assert.Equal(JobState.Uploading, Job(jobs[0].Id).State);
        release.SetResult(new UploadReceipt("tiktok-1"));
    }

    [Fact]
    public async Task Each_platform_sends_one_video_at_a_time_oldest_first()
    {
        var release = new TaskCompletionSource<UploadReceipt>();
        _youTube.Upload = (_, _, _) => release.Task;
        _queue.Start();

        var first = Add(YouTube());
        await UntilStateAsync(first.Id, JobState.Uploading);
        _time.Advance(TimeSpan.FromSeconds(1));
        var second = Add(YouTube());

        await SettleAsync();
        Assert.Equal(JobState.Queued, Job(second.Id).State);

        release.SetResult(new UploadReceipt("video-1"));
        await UntilStateAsync(first.Id, JobState.Completed);
        await UntilStateAsync(second.Id, JobState.Completed);
    }

    [Fact]
    public async Task A_slow_video_being_processed_doesnt_hold_up_the_next_upload()
    {
        var release = new TaskCompletionSource<JobState>();
        _youTube.Check = (_, _) => release.Task;
        _queue.Start();

        var first = Add(YouTube());
        await UntilStateAsync(first.Id, JobState.Processing);
        var second = Add(YouTube());
        await UntilStateAsync(second.Id, JobState.Processing);

        release.SetResult(JobState.Completed);
        await UntilStateAsync(first.Id, JobState.Completed);
    }

    [Fact]
    public async Task Retryable_failure_waits_with_growing_backoff_then_gives_up()
    {
        SetUploadToFail(_youTube, UploadFailureKind.Retryable);
        _queue.Start();
        var job = Add(YouTube());

        foreach (var (attempt, minutes) in new[] { (1, 1), (2, 2), (3, 4), (4, 8) })
        {
            var waiting = await UntilAsync(job.Id, current => current.State == JobState.Waiting && current.Attempts == attempt);
            Assert.Equal(_time.GetUtcNow().AddMinutes(minutes), waiting.NextAttemptAt);
            Assert.Equal("From the platform.", waiting.LastError);
            _time.Advance(TimeSpan.FromMinutes(minutes));
        }

        var failed = await UntilStateAsync(job.Id, JobState.Failed);

        Assert.Equal(5, failed.Attempts);
        Assert.Equal(5, _youTube.Uploads.Count);
    }

    [Fact]
    public async Task TikTok_gets_fewer_tries_because_each_is_a_new_init()
    {
        SetUploadToFail(_tikTok, UploadFailureKind.Retryable);
        _queue.Start();
        var job = Add(TikTok());

        await UntilAsync(job.Id, current => current.Attempts == 1);
        _time.Advance(TimeSpan.FromMinutes(1));
        await UntilAsync(job.Id, current => current.Attempts == 2);
        _time.Advance(TimeSpan.FromMinutes(2));

        await UntilStateAsync(job.Id, JobState.Failed);

        Assert.Equal(3, _tikTok.Uploads.Count);
    }

    [Fact]
    public async Task Backoff_is_spread_out_by_jitter()
    {
        await using var queue = new UploadQueue(_store, [_youTube], _sessions, _api, new PassThroughRenderStep(), Connected, _time, () => 1);
        SetUploadToFail(_youTube, UploadFailureKind.Retryable);
        queue.Start();
        var job = queue.Add(_video, null, [YouTube()]).Single();

        var deadline = DateTime.UtcNow + Patience;
        while (queue.Jobs.Single(current => current.Id == job.Id).State != JobState.Waiting && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        Assert.Equal(Start.AddSeconds(75), queue.Jobs.Single(current => current.Id == job.Id).NextAttemptAt);
    }

    [Fact]
    public async Task A_long_Retry_After_beats_the_backoff()
    {
        SetUploadToFail(_youTube, UploadFailureKind.Retryable, Start.AddHours(3));
        _queue.Start();
        var job = Add(YouTube());

        var waiting = await UntilStateAsync(job.Id, JobState.Waiting);

        Assert.Equal(Start.AddHours(3), waiting.NextAttemptAt);
    }

    [Fact]
    public async Task A_platform_cap_waits_holds_the_lane_and_costs_no_attempt()
    {
        var capUntil = Start.AddHours(10);
        SetUploadToFail(_youTube, UploadFailureKind.PlatformCap, capUntil);
        _queue.Start();
        var capped = Add(YouTube());

        var waiting = await UntilStateAsync(capped.Id, JobState.Waiting);
        Assert.Equal(capUntil, waiting.NextAttemptAt);
        Assert.Equal(0, waiting.Attempts);

        _time.Advance(TimeSpan.FromSeconds(1));
        var next = Add(YouTube());
        await SettleAsync();

        Assert.Equal(JobState.Queued, Job(next.Id).State);
        Assert.Single(_youTube.Uploads);

        _youTube.Upload = (job, _, _) => Task.FromResult(new UploadReceipt("video-" + job.Id));
        _time.Advance(TimeSpan.FromHours(10));

        await UntilStateAsync(capped.Id, JobState.Completed);
        await UntilStateAsync(next.Id, JobState.Completed);
    }

    [Fact]
    public async Task A_cap_on_one_platform_leaves_the_other_running()
    {
        SetUploadToFail(_tikTok, UploadFailureKind.PlatformCap, Start.AddHours(24));
        _queue.Start();
        var jobs = _queue.Add(_video, null, [TikTok(), YouTube()]);

        await UntilStateAsync(jobs[0].Id, JobState.Waiting);
        await UntilStateAsync(jobs[1].Id, JobState.Completed);
    }

    [Fact]
    public async Task A_permanent_failure_ends_the_job_and_forgets_the_session()
    {
        SetUploadToFail(_youTube, UploadFailureKind.Permanent);
        _queue.Start();
        var job = Add(YouTube());
        _sessions.Save(job.Id, "https://www.googleapis.com/session");

        var failed = await UntilStateAsync(job.Id, JobState.Failed);

        Assert.Equal("From the platform.", failed.LastError);
        Assert.Empty(_sessions.Saved);
        Assert.Single(_youTube.Uploads);
    }

    [Fact]
    public async Task Running_out_of_tries_keeps_the_session_so_a_lost_answer_cant_become_a_second_copy()
    {
        SetUploadToFail(_tikTok, UploadFailureKind.Retryable);
        _queue.Start();
        var job = Add(TikTok());
        _sessions.Save(job.Id, "https://upload.test/session");

        await UntilAsync(job.Id, current => current.Attempts == 1);
        _time.Advance(TimeSpan.FromMinutes(1));
        await UntilAsync(job.Id, current => current.Attempts == 2);
        _time.Advance(TimeSpan.FromMinutes(2));
        await UntilStateAsync(job.Id, JobState.Failed);

        Assert.True(_sessions.Saved.ContainsKey(job.Id));
    }

    [Fact]
    public async Task A_lost_sign_in_pauses_the_platform_until_reconnected()
    {
        SetUploadToFail(_youTube, UploadFailureKind.Reconnect);
        _queue.Start();
        var first = Add(YouTube());
        await UntilStateAsync(first.Id, JobState.NeedsReconnect);

        _time.Advance(TimeSpan.FromSeconds(1));
        var second = Add(YouTube());
        await SettleAsync();

        Assert.True(_queue.NeedsReconnect(Platform.YouTube));
        Assert.Equal(JobState.Queued, Job(second.Id).State);
        Assert.Single(_youTube.Uploads);

        _youTube.Upload = (job, _, _) => Task.FromResult(new UploadReceipt("video-" + job.Id));
        _queue.AccountReconnected(Platform.YouTube);

        await UntilStateAsync(first.Id, JobState.Completed);
        await UntilStateAsync(second.Id, JobState.Completed);
        Assert.False(_queue.NeedsReconnect(Platform.YouTube));
    }

    [Fact]
    public async Task A_lost_sign_in_on_one_platform_leaves_the_other_running()
    {
        SetUploadToFail(_tikTok, UploadFailureKind.Reconnect);
        _queue.Start();
        var jobs = _queue.Add(_video, null, [TikTok(), YouTube()]);

        await UntilStateAsync(jobs[0].Id, JobState.NeedsReconnect);
        await UntilStateAsync(jobs[1].Id, JobState.Completed);
    }

    [Fact]
    public async Task The_sign_in_ending_while_checking_doesnt_upload_again_after_reconnecting()
    {
        SetCheckToFail(_youTube, UploadFailureKind.Reconnect);
        _queue.Start();
        var job = Add(YouTube());
        await UntilStateAsync(job.Id, JobState.NeedsReconnect);

        _youTube.Check = (_, _) => Task.FromResult(JobState.Completed);
        _queue.AccountReconnected(Platform.YouTube);

        await UntilStateAsync(job.Id, JobState.Completed);
        Assert.Single(_youTube.Uploads);
    }

    [Theory]
    [InlineData(UploadFailureKind.Retryable)]
    [InlineData(UploadFailureKind.PlatformCap)]
    public async Task A_video_the_platform_dropped_is_uploaded_again(UploadFailureKind kind)
    {
        SetCheckToFail(_tikTok, kind, Start.AddMinutes(30));
        _queue.Start();
        var job = Add(TikTok());

        var waiting = await UntilStateAsync(job.Id, JobState.Waiting);
        Assert.Null(waiting.RemoteId);

        _tikTok.Check = (_, _) => Task.FromResult(JobState.Completed);
        _time.Advance(TimeSpan.FromMinutes(30));

        await UntilStateAsync(job.Id, JobState.Completed);
        Assert.Equal(2, _tikTok.Uploads.Count);
    }

    [Fact]
    public async Task A_video_the_platform_wont_keep_fails_without_uploading_again()
    {
        SetCheckToFail(_youTube, UploadFailureKind.Permanent);
        _queue.Start();
        var job = Add(YouTube());

        var failed = await UntilStateAsync(job.Id, JobState.Failed);

        Assert.NotNull(failed.RemoteId);
        Assert.Single(_youTube.Uploads);
    }

    [Fact]
    public async Task A_video_still_processing_is_asked_about_again_and_given_up_on_after_days()
    {
        _youTube.Check = (_, _) => Task.FromResult(JobState.Processing);
        _queue.Start();
        var job = Add(YouTube());

        var processing = await UntilAsync(job.Id, current => current.State == JobState.Processing && current.NextAttemptAt > Start);
        Assert.Equal(Start.AddMinutes(5), processing.NextAttemptAt);

        _time.Advance(TimeSpan.FromDays(3));
        var failed = await UntilStateAsync(job.Id, JobState.Failed);

        Assert.Contains("3 days", failed.LastError);
    }

    [Fact]
    public async Task Retrying_a_failed_upload_sends_it_again_from_scratch()
    {
        SetUploadToFail(_youTube, UploadFailureKind.Permanent);
        _queue.Start();
        var job = Add(YouTube());
        await UntilStateAsync(job.Id, JobState.Failed);

        _youTube.Upload = (current, _, _) => Task.FromResult(new UploadReceipt("video-1"));
        _queue.Retry(job.Id);

        var done = await UntilStateAsync(job.Id, JobState.Completed);
        Assert.Null(done.LastError);
        Assert.Equal(2, _youTube.Uploads.Count);
    }

    [Fact]
    public async Task Retrying_a_video_the_platform_already_has_checks_on_it_rather_than_uploading_again()
    {
        SetCheckToFail(_youTube, UploadFailureKind.Permanent);
        _queue.Start();
        var job = Add(YouTube());
        await UntilStateAsync(job.Id, JobState.Failed);

        _youTube.Check = (_, _) => Task.FromResult(JobState.Completed);
        _queue.Retry(job.Id);

        await UntilStateAsync(job.Id, JobState.Completed);
        Assert.Single(_youTube.Uploads);
    }

    [Fact]
    public async Task Retrying_a_waiting_job_tries_now()
    {
        SetUploadToFail(_youTube, UploadFailureKind.PlatformCap, Start.AddDays(1));
        _queue.Start();
        var job = Add(YouTube());
        await UntilStateAsync(job.Id, JobState.Waiting);

        _youTube.Upload = (_, _, _) => Task.FromResult(new UploadReceipt("video-1"));
        _queue.Retry(job.Id);

        await UntilStateAsync(job.Id, JobState.Completed);
    }

    [Fact]
    public async Task Cancelling_a_queued_job_means_it_never_starts()
    {
        var job = Add(YouTube());

        _queue.Cancel(job.Id);
        _queue.Start();
        await SettleAsync();

        Assert.Equal(JobState.Cancelled, Job(job.Id).State);
        Assert.Empty(_youTube.Uploads);
    }

    [Fact]
    public async Task Cancelling_during_an_upload_stops_it_and_forgets_the_session()
    {
        _youTube.Upload = async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new UploadReceipt("never");
        };
        _queue.Start();
        var job = Add(YouTube());
        await UntilStateAsync(job.Id, JobState.Uploading);
        _sessions.Save(job.Id, "https://www.googleapis.com/session");

        _queue.Cancel(job.Id);

        await UntilStateAsync(job.Id, JobState.Cancelled);
        Assert.Empty(_sessions.Saved);

        var next = Add(YouTube());
        _youTube.Upload = (_, _, _) => Task.FromResult(new UploadReceipt("video-2"));
        await UntilStateAsync(next.Id, JobState.Completed);
    }

    [Fact]
    public async Task A_video_that_arrived_as_the_cancel_came_is_still_followed()
    {
        var uploading = new TaskCompletionSource();
        var release = new TaskCompletionSource<UploadReceipt>();
        _youTube.Upload = (_, _, _) =>
        {
            uploading.SetResult();
            return release.Task;
        };
        _youTube.Check = (_, _) => Task.FromResult(JobState.Processing);
        _queue.Start();
        var job = Add(YouTube());
        await uploading.Task;

        _queue.Cancel(job.Id);
        release.SetResult(new UploadReceipt("video-1"));

        var followed = await UntilAsync(job.Id, current => current.RemoteId is not null);
        Assert.Equal(JobState.Processing, followed.State);
    }

    [Fact]
    public async Task Cancelling_while_processing_stops_following_the_video()
    {
        _youTube.Check = async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return JobState.Completed;
        };
        _queue.Start();
        var job = Add(YouTube());
        await UntilStateAsync(job.Id, JobState.Processing);

        _queue.Cancel(job.Id);

        var cancelled = await UntilStateAsync(job.Id, JobState.Cancelled);
        Assert.NotNull(cancelled.RemoteId);
    }

    [Theory]
    [InlineData(JobState.Waiting)]
    [InlineData(JobState.NeedsReconnect)]
    [InlineData(JobState.AwaitingCreator)]
    public async Task Cancelling_works_from_any_paused_state(JobState state)
    {
        var job = Saved(state, TikTok(), state == JobState.AwaitingCreator ? "publish" : null) with { NextAttemptAt = Start.AddDays(1) };
        _store.Saved[job.Id] = job;
        _tikTok.Check = (_, _) => Task.FromResult(JobState.AwaitingCreator);
        _queue.Start();
        await SettleAsync();

        _queue.Cancel(job.Id);

        await UntilStateAsync(job.Id, JobState.Cancelled);
    }

    [Theory]
    [InlineData(JobState.Completed)]
    [InlineData(JobState.Failed)]
    [InlineData(JobState.Cancelled)]
    public async Task Cancelling_a_finished_job_changes_nothing(JobState state)
    {
        var job = Saved(state);
        _store.Saved[job.Id] = job;
        _queue.Start();

        _queue.Cancel(job.Id);

        Assert.Equal(state, Job(job.Id).State);
        await SettleAsync();
        Assert.Empty(_youTube.Uploads);
    }

    [Fact]
    public async Task A_finished_job_can_be_removed_and_a_running_one_cant()
    {
        var release = new TaskCompletionSource<UploadReceipt>();
        _youTube.Upload = (_, _, _) => release.Task;
        _queue.Start();
        var job = Add(YouTube());
        await UntilStateAsync(job.Id, JobState.Uploading);

        _queue.Remove(job.Id);
        Assert.Single(_queue.Jobs);

        release.SetResult(new UploadReceipt("video-1"));
        await UntilStateAsync(job.Id, JobState.Completed);

        _queue.Remove(job.Id);
        Assert.Empty(_queue.Jobs);
        Assert.Empty(_store.Saved);
    }

    [Fact]
    public void Refuses_a_video_that_fails_the_checks_and_queues_nothing()
    {
        var refused = Assert.Throws<QueueRefusedException>(() => _queue.Add(_video, null, [YouTube() with { Privacy = null }]));

        Assert.Contains("Choose a privacy setting.", refused.Problems);
        Assert.Empty(_queue.Jobs);
    }

    [Fact]
    public void One_bad_post_stops_the_whole_batch()
    {
        Assert.Throws<QueueRefusedException>(() => _queue.Add(_video, null, [TikTok(), YouTube() with { Title = "" }]));

        Assert.Empty(_queue.Jobs);
    }

    [Fact]
    public void Refuses_a_missing_file_and_an_empty_choice()
    {
        Assert.Throws<QueueRefusedException>(() => _queue.Add(Path.Combine(_folder, "gone.mp4"), null, [TikTok()]));
        Assert.Throws<QueueRefusedException>(() => _queue.Add(_video, null, []));
    }

    [Fact]
    public async Task The_playlist_step_runs_once_and_is_recorded()
    {
        _queue.Start();
        var job = Add(YouTube("PL1"));

        var done = await UntilStateAsync(job.Id, JobState.Completed);

        Assert.True(done.PlaylistAdded);
        Assert.Equal(("PL1", done.RemoteId!), _api.Added.Single());
        Assert.Single(_youTube.Uploads);
    }

    [Fact]
    public async Task A_job_with_no_playlist_skips_the_step()
    {
        _queue.Start();
        var job = Add(YouTube());

        await UntilStateAsync(job.Id, JobState.Completed);

        Assert.Empty(_api.Added);
    }

    [Fact]
    public async Task A_failed_playlist_step_is_noted_and_never_uploads_again()
    {
        _api.Add = () => throw new UploadException(Platform.YouTube, UploadFailureKind.Permanent, "That playlist is full.");
        _queue.Start();
        var job = Add(YouTube("PL1"));

        var done = await UntilStateAsync(job.Id, JobState.Completed);

        Assert.Equal("That playlist is full.", done.PlaylistError);
        Assert.False(done.PlaylistAdded);
        Assert.Single(_youTube.Uploads);
    }

    [Fact]
    public async Task A_playlist_step_that_cant_reach_YouTube_tries_again_then_gives_up_without_uploading()
    {
        _api.Add = () => throw new UploadException(Platform.YouTube, UploadFailureKind.Retryable, "Can't find the video yet.");
        _queue.Start();
        var job = Add(YouTube("PL1"));

        for (var attempt = 1; attempt < 5; attempt++)
        {
            var number = attempt;
            await UntilAsync(job.Id, current => current.PlaylistAttempts == number && current.NextAttemptAt > _time.GetUtcNow());
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        var done = await UntilStateAsync(job.Id, JobState.Completed);

        Assert.Equal("Can't find the video yet.", done.PlaylistError);
        Assert.Equal(5, _api.Added.Count);
        Assert.Single(_youTube.Uploads);
    }

    [Fact]
    public async Task A_playlist_step_out_of_quota_waits_without_using_up_tries()
    {
        var reset = Start.AddHours(6);
        _api.Add = () => throw new UploadException(Platform.YouTube, UploadFailureKind.PlatformCap, "Out of quota.", reset);
        _queue.Start();
        var job = Add(YouTube("PL1"));

        var waiting = await UntilAsync(job.Id, current => current.NextAttemptAt == reset);
        Assert.Equal(0, waiting.PlaylistAttempts);
        Assert.Equal(JobState.Processing, waiting.State);

        _api.Add = () => Task.CompletedTask;
        _time.Advance(TimeSpan.FromHours(6));

        var done = await UntilStateAsync(job.Id, JobState.Completed);
        Assert.True(done.PlaylistAdded);
    }

    [Fact]
    public async Task The_playlist_step_survives_a_restart_without_uploading_again()
    {
        var job = Saved(JobState.Processing, YouTube("PL1"), "video-1");
        _store.Saved[job.Id] = job;
        _queue.Start();

        var done = await UntilStateAsync(job.Id, JobState.Completed);

        Assert.True(done.PlaylistAdded);
        Assert.Empty(_youTube.Uploads);
    }

    [Fact]
    public async Task A_step_already_done_isnt_repeated_after_a_restart()
    {
        var job = Saved(JobState.Processing, YouTube("PL1"), "video-1") with { PlaylistAdded = true };
        _store.Saved[job.Id] = job;
        _queue.Start();

        await UntilStateAsync(job.Id, JobState.Completed);

        Assert.Empty(_api.Added);
    }

    [Fact]
    public async Task Restart_while_queued_carries_on()
    {
        var job = Saved(JobState.Queued);
        _store.Saved[job.Id] = job;
        _queue.Start();

        await UntilStateAsync(job.Id, JobState.Completed);
    }

    [Theory]
    [InlineData(JobState.Rendering)]
    [InlineData(JobState.Uploading)]
    public async Task Restart_during_an_upload_sends_it_again_and_counts_the_try(JobState state)
    {
        var job = Saved(state) with { Attempts = 1, BytesSent = 500 };
        _store.Saved[job.Id] = job;
        var seen = new TaskCompletionSource<UploadJob>();
        _youTube.Upload = (current, _, _) =>
        {
            seen.TrySetResult(current);
            return Task.FromResult(new UploadReceipt("video-1"));
        };
        _queue.Start();

        var uploaded = await seen.Task;

        Assert.Equal(2, uploaded.Attempts);
        Assert.Equal(0, uploaded.BytesSent);
        await UntilStateAsync(job.Id, JobState.Completed);
    }

    [Fact]
    public async Task Restart_while_processing_checks_again_and_doesnt_upload()
    {
        var job = Saved(JobState.Processing, remoteId: "video-1");
        _store.Saved[job.Id] = job;
        _queue.Start();

        await UntilStateAsync(job.Id, JobState.Completed);

        Assert.Equal("video-1", _youTube.Checks.Single());
        Assert.Empty(_youTube.Uploads);
    }

    [Fact]
    public async Task Restart_while_processing_with_no_id_sends_it_again()
    {
        var job = Saved(JobState.Processing);
        _store.Saved[job.Id] = job;
        _queue.Start();

        await UntilStateAsync(job.Id, JobState.Completed);

        Assert.Single(_youTube.Uploads);
    }

    [Fact]
    public async Task Restart_while_awaiting_the_creator_checks_again()
    {
        var job = Saved(JobState.AwaitingCreator, TikTok(), "publish-1");
        _store.Saved[job.Id] = job;
        _queue.Start();

        await UntilStateAsync(job.Id, JobState.Completed);

        Assert.Empty(_tikTok.Uploads);
    }

    [Fact]
    public async Task Restart_while_waiting_keeps_waiting_until_its_time()
    {
        var job = Saved(JobState.Waiting) with { NextAttemptAt = Start.AddHours(1) };
        _store.Saved[job.Id] = job;
        _queue.Start();
        await SettleAsync();

        Assert.Empty(_youTube.Uploads);

        _time.Advance(TimeSpan.FromHours(1));

        await UntilStateAsync(job.Id, JobState.Completed);
    }

    [Fact]
    public async Task Restart_while_needing_reconnect_keeps_the_platform_paused()
    {
        var paused = Saved(JobState.NeedsReconnect);
        var next = Saved(JobState.Queued);
        _store.Saved[paused.Id] = paused;
        _store.Saved[next.Id] = next;
        _queue.Start();
        await SettleAsync();

        Assert.True(_queue.NeedsReconnect(Platform.YouTube));
        Assert.Empty(_youTube.Uploads);

        _queue.AccountReconnected(Platform.YouTube);

        await UntilStateAsync(paused.Id, JobState.Completed);
        await UntilStateAsync(next.Id, JobState.Completed);
    }

    [Theory]
    [InlineData(JobState.Completed)]
    [InlineData(JobState.Failed)]
    [InlineData(JobState.Cancelled)]
    public async Task Restart_leaves_finished_jobs_alone(JobState state)
    {
        var job = Saved(state);
        _store.Saved[job.Id] = job;
        _queue.Start();
        await SettleAsync();

        Assert.Equal(state, Job(job.Id).State);
        Assert.Empty(_youTube.Uploads);
        Assert.Empty(_youTube.Checks);
    }

    [Fact]
    public async Task Closing_the_app_during_an_upload_leaves_it_to_resume_not_cancelled()
    {
        _youTube.Upload = async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new UploadReceipt("never");
        };
        _queue.Start();
        var job = Add(YouTube());
        await UntilStateAsync(job.Id, JobState.Uploading);
        _sessions.Save(job.Id, "https://www.googleapis.com/session");

        await _queue.DisposeAsync();
        _queues.Remove(_queue);

        Assert.Equal(JobState.Uploading, _store.Saved[job.Id].State);
        Assert.True(_sessions.Saved.ContainsKey(job.Id));

        _youTube.Upload = (_, _, _) => Task.FromResult(new UploadReceipt("video-1"));
        _queue = NewQueue();
        _queue.Start();

        await UntilStateAsync(job.Id, JobState.Completed);
    }

    [Fact]
    public void Reports_damaged_files_and_keeps_the_good_jobs()
    {
        var job = Saved(JobState.Completed);
        _store.Saved[job.Id] = job;
        _store.Damaged.Add("broken.json");

        var damaged = _queue.Start();

        Assert.Equal(["broken.json"], damaged);
        Assert.Single(_queue.Jobs);
    }

    [Fact]
    public async Task Saves_every_state_change()
    {
        _queue.Start();
        var job = Add(YouTube());

        await UntilStateAsync(job.Id, JobState.Completed);

        Assert.Equal(JobState.Completed, _store.Saved[job.Id].State);
    }

    [Fact]
    public async Task A_save_that_fails_is_reported_and_the_queue_carries_on()
    {
        var problems = new ConcurrentQueue<string>();
        _queue.SaveFailed += problems.Enqueue;
        _store.SaveThrows = new IOException("The disk is full.");
        _queue.Start();
        var job = Add(YouTube());

        await UntilStateAsync(job.Id, JobState.Completed);

        Assert.Contains("The disk is full.", problems);
    }

    [Fact]
    public async Task An_unexpected_exception_doesnt_leave_a_job_looking_busy_or_show_its_text()
    {
        _youTube.Upload = (_, _, _) => throw new InvalidOperationException("https://upload.test/secret");
        _queue.Start();
        var job = Add(YouTube());

        var waiting = await UntilStateAsync(job.Id, JobState.Waiting);

        Assert.DoesNotContain("secret", waiting.LastError);
    }

    [Fact]
    public async Task The_render_step_runs_in_its_own_state_and_fills_the_path()
    {
        var render = new RecordingRender(_video + ".rendered.mp4");
        _queue = NewQueue(render);
        var seen = new TaskCompletionSource<UploadJob>();
        _youTube.Upload = (job, _, _) =>
        {
            seen.TrySetResult(job);
            return Task.FromResult(new UploadReceipt("video-1"));
        };
        _queue.Start();
        var job = Add(YouTube());

        var uploaded = await seen.Task;

        Assert.Equal([JobState.Rendering], render.StatesSeen);
        Assert.Equal(_video + ".rendered.mp4", uploaded.PublishPath);
        await UntilStateAsync(job.Id, JobState.Completed);
    }

    [Fact]
    public async Task Pass_through_rendering_publishes_the_source()
    {
        var seen = new TaskCompletionSource<UploadJob>();
        _youTube.Upload = (job, _, _) =>
        {
            seen.TrySetResult(job);
            return Task.FromResult(new UploadReceipt("video-1"));
        };
        _queue.Start();
        Add(YouTube());

        Assert.Equal(_video, (await seen.Task).PublishPath);
    }

    [Fact]
    public async Task Progress_shows_on_the_job_but_isnt_saved()
    {
        var reported = new TaskCompletionSource();
        var release = new TaskCompletionSource<UploadReceipt>();
        _youTube.Upload = (_, progress, _) =>
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            progress!.Report(400);
            reported.SetResult();
            return release.Task;
        };
        _queue.Start();
        var job = Add(YouTube());
        await reported.Task;

        Assert.Equal(400, Job(job.Id).BytesSent);
        Assert.Equal(0, _store.Saved[job.Id].BytesSent);

        release.SetResult(new UploadReceipt("video-1"));
        await UntilStateAsync(job.Id, JobState.Completed);
    }

    [Fact]
    public async Task A_scheduled_video_that_waited_past_its_time_never_starts_with_the_real_client()
    {
        var server = new FakeYouTube
        {
            OnStart = _ => FakeYouTube.Error(System.Net.HttpStatusCode.Forbidden, "quotaExceeded"),
        };
        var uploader = new VideoDispatch.Core.YouTube.YouTubeUploader(
            new HttpClient(server) { Timeout = Timeout.InfiniteTimeSpan },
            new TikTok.FakeTokens(Platform.YouTube),
            _sessions,
            _time);
        var queue = new UploadQueue(_store, [uploader], _sessions, _api, new PassThroughRenderStep(), Connected, _time);
        _queues.Add(queue);
        queue.Start();

        var job = queue.Add(_video, null, [YouTube() with { PublishAt = Start.AddHours(1) }]).Single();

        async Task<UploadJob> UntilAsync(JobState state)
        {
            var deadline = DateTime.UtcNow + Patience;
            while (queue.Jobs.Single().State != state)
            {
                Assert.True(DateTime.UtcNow < deadline, $"Still {queue.Jobs.Single().State}");
                await Task.Delay(5);
            }

            return queue.Jobs.Single();
        }

        var waiting = await UntilAsync(JobState.Waiting);
        Assert.Equal(job.Id, waiting.Id);
        Assert.Single(server.Starts);

        // Quota comes back at midnight Pacific, well after the hour the video was scheduled for.
        server.OnStart = null;
        _time.Advance(waiting.NextAttemptAt!.Value - _time.GetUtcNow());

        var failed = await UntilAsync(JobState.Failed);

        Assert.Contains("scheduled time", failed.LastError);
        Assert.Single(server.Starts);
    }

    [Fact]
    public void A_queue_can_only_start_once()
    {
        _queue.Start();

        Assert.Throws<InvalidOperationException>(() => _queue.Start());
    }
}
