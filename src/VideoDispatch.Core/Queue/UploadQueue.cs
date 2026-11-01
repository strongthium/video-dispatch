using VideoDispatch.Core.Accounts;
using VideoDispatch.Core.Accounts.Secrets;
using VideoDispatch.Core.Media;
using VideoDispatch.Core.Model;
using VideoDispatch.Core.Uploads;
using VideoDispatch.Core.YouTube;

namespace VideoDispatch.Core.Queue;

/// <summary>A video the queue wouldn't take, and why.</summary>
public sealed class QueueRefusedException(IReadOnlyList<string> problems) : Exception(string.Join(" ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>
/// Runs jobs through their states, saving each change, so the app can close at any moment and carry on. Each platform
/// has its own lane and sends one video at a time, so one platform being stuck can't hold up the other. Checking on
/// videos already sent runs beside the uploads, because a video can take a long time to process.
/// </summary>
public sealed class UploadQueue : IAsyncDisposable
{
    private const int MaxAttempts = 5;

    // Every new try at TikTok is a new init, which may count against the creator's 5 pending shares.
    private const int MaxTikTokAttempts = 3;
    private const int MaxPlaylistAttempts = 5;

    private static readonly TimeSpan FirstBackoff = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LongestBackoff = TimeSpan.FromHours(1);
    private static readonly TimeSpan DefaultCapWait = TimeSpan.FromHours(1);
    private static readonly TimeSpan LongestSleep = TimeSpan.FromDays(1);
    private static readonly TimeSpan ProcessingRecheck = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PlaylistRetry = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ProcessingGiveUp = TimeSpan.FromDays(3);
    private static readonly TimeSpan CreatorRecheck = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan CreatorGiveUp = TimeSpan.FromDays(7);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

    private readonly object _lock = new();
    private readonly object _saveLock = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<Guid, UploadJob> _jobs = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _running = [];
    private readonly HashSet<Guid> _cancelling = [];
    private readonly List<Task> _work = [];
    private readonly Dictionary<Platform, Lane> _lanes;
    private readonly IRecordStore<UploadJob> _store;
    private readonly IUploadSessionStore _sessions;
    private readonly IYouTubeApi? _youTube;
    private readonly IRenderStep _render;
    private readonly Func<Platform, AccountStatus> _accountOf;
    private readonly TimeProvider _time;
    private readonly Func<double> _random;
    private readonly CancellationTokenSource _stop = new();
    private bool _started;

    /// <param name="store">Keeps the jobs.</param>
    /// <param name="uploaders">One for each platform.</param>
    /// <param name="sessions">Where uploaders keep unfinished sessions, cleared when a job ends.</param>
    /// <param name="youTube">Does the playlist step.</param>
    /// <param name="render">Runs before each upload.</param>
    /// <param name="accountOf">Says what state a platform's account is in.</param>
    /// <param name="time">The clock used for waits.</param>
    /// <param name="random">Spreads out retries. Returns a number from 0 to 1.</param>
    public UploadQueue(
        IRecordStore<UploadJob> store,
        IEnumerable<IVideoUploader> uploaders,
        IUploadSessionStore sessions,
        IYouTubeApi? youTube,
        IRenderStep render,
        Func<Platform, AccountStatus> accountOf,
        TimeProvider time,
        Func<double>? random = null)
    {
        _store = store;
        _sessions = sessions;
        _youTube = youTube;
        _render = render;
        _accountOf = accountOf;
        _time = time;
        _random = random ?? Random.Shared.NextDouble;
        _lanes = uploaders.ToDictionary(uploader => uploader.Platform, uploader => new Lane(uploader));
    }

    /// <summary>A job changed, or was removed if it's no longer in <see cref="Jobs"/>. This can come from any thread.</summary>
    public event Action<UploadJob>? JobChanged;

    /// <summary>A change couldn't be saved. The queue carries on, but it may not survive the app closing.</summary>
    public event Action<string>? SaveFailed;

    public IReadOnlyList<UploadJob> Jobs
    {
        get
        {
            lock (_lock)
            {
                return _jobs.Values.OrderBy(job => job.CreatedAt).ToList();
            }
        }
    }

    /// <summary>Whether a platform's lane is stopped until its account is connected again.</summary>
    public bool NeedsReconnect(Platform platform)
    {
        lock (_lock)
        {
            return _lanes.TryGetValue(platform, out var lane) && lane.NeedsReconnect;
        }
    }

    /// <summary>Reads saved jobs, works out where each one stands, and starts. Returns the names of any damaged files.</summary>
    public IReadOnlyList<string> Start()
    {
        if (_started)
        {
            throw new InvalidOperationException("The queue has already started.");
        }

        _started = true;
        var load = _store.LoadAll();

        foreach (var saved in load.Items)
        {
            var job = Recover(saved);
            lock (_lock)
            {
                _jobs[job.Id] = job;
                if (job.State == JobState.NeedsReconnect && _lanes.TryGetValue(job.Platform, out var pausedLane))
                {
                    pausedLane.NeedsReconnect = true;
                }
            }

            if (job != saved)
            {
                Save(job);
            }
        }

        _render.Tidy(load.Items.Where(job => job.RenderedPath is not null && job.RemoteId is null && job.State != JobState.Completed).Select(job => job.RenderedPath!).ToList());

        _stop.Token.Register(() => _stopped.TrySetResult());
        lock (_lock)
        {
            foreach (var lane in _lanes.Values)
            {
                _work.Add(Task.Run(() => RunLaneAsync(lane)));
            }
        }

        return load.Damaged;
    }

    /// <summary>Queues one video for one or more platforms, as jobs sharing a batch, or refuses it.</summary>
    /// <exception cref="QueueRefusedException">Something must be fixed first. Nothing is queued.</exception>
    public IReadOnlyList<UploadJob> Add(string path, VideoMetadata? metadata, IReadOnlyList<PostDetails> posts)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new QueueRefusedException(["The video file is gone."]);
        }

        var now = _time.GetUtcNow();
        var problems = posts
            .SelectMany(post => QueueChecks.Check(post, path, file.Length, metadata, _accountOf(post.Platform), now).Problems)
            .Distinct()
            .ToList();

        if (posts.Count == 0)
        {
            problems.Add("Choose where to post this video.");
        }

        if (problems.Count > 0)
        {
            throw new QueueRefusedException(problems);
        }

        var batch = Guid.NewGuid();
        var jobs = posts.Select(post => new UploadJob
        {
            Id = Guid.NewGuid(),
            BatchId = batch,
            SourcePath = path,
            SourceLength = file.Length,
            SourceLastWriteUtc = file.LastWriteTimeUtc,
            Details = post,
            CreatedAt = now,
            TotalBytes = file.Length,
        }).ToList();

        foreach (var job in jobs)
        {
            lock (_lock)
            {
                _jobs[job.Id] = job;
            }

            Save(job);
            JobChanged?.Invoke(job);
            Wake(job.Platform);
        }

        return jobs;
    }

    /// <summary>Tries a job again now. A video the platform already has is checked on, never uploaded again.</summary>
    public void Retry(Guid id)
    {
        var job = Change(id, current => current.State switch
        {
            JobState.Failed or JobState.Cancelled when current.RemoteId is not null => current with
            {
                State = JobState.Processing,
                NextAttemptAt = _time.GetUtcNow(),
                LastError = null,
            },
            JobState.Failed or JobState.Cancelled => current with
            {
                State = JobState.Queued,
                Attempts = 0,
                NextAttemptAt = null,
                LastError = null,
                BytesSent = 0,
            },
            JobState.Waiting or JobState.Processing or JobState.AwaitingCreator => current with { NextAttemptAt = _time.GetUtcNow() },
            _ => current,
        });

        if (job is not null)
        {
            lock (_lock)
            {
                // Asking for it by hand means trying it, even though the rest of the lane waits out a limit.
                if (_lanes.TryGetValue(job.Platform, out var lane))
                {
                    lane.PausedUntil = null;
                }
            }

            Wake(job.Platform);
        }
    }

    /// <summary>
    /// Stops a job. A video the platform already has stays there: this only stops following it.
    /// </summary>
    public void Cancel(Guid id)
    {
        CancellationTokenSource? running;
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out var current) || IsFinished(current.State))
            {
                return;
            }

            if (_running.TryGetValue(id, out running))
            {
                _cancelling.Add(id);
            }
        }

        if (running is not null)
        {
            running.Cancel();
            return;
        }

        EndCancelled(id);
    }

    /// <summary>The creator says they finished the post in TikTok.</summary>
    public void MarkPosted(Guid id)
    {
        Change(id, job => job.State == JobState.AwaitingCreator
            ? job with { State = JobState.Completed, NextAttemptAt = null }
            : job);
    }

    /// <summary>Forgets a job that has ended.</summary>
    public void Remove(Guid id)
    {
        UploadJob? removed;
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out removed) || !IsFinished(removed.State))
            {
                return;
            }

            _jobs.Remove(id);
        }

        try
        {
            _sessions.Delete(id);
            lock (_saveLock)
            {
                _store.Delete(id);
            }
        }
        catch (Exception exception) when (IsStorageProblem(exception))
        {
            SaveFailed?.Invoke(exception.Message);
        }

        ReleaseRendered(removed);
        JobChanged?.Invoke(removed);
    }

    /// <summary>The account works again, so its lane carries on, and paused jobs pick up from where they were.</summary>
    public void AccountReconnected(Platform platform)
    {
        List<Guid> paused;
        lock (_lock)
        {
            if (!_lanes.TryGetValue(platform, out var lane))
            {
                return;
            }

            lane.NeedsReconnect = false;
            lane.PausedUntil = null;
            paused = _jobs.Values
                .Where(job => job.Platform == platform && job.State == JobState.NeedsReconnect)
                .Select(job => job.Id)
                .ToList();
        }

        foreach (var id in paused)
        {
            Change(id, job => job.RemoteId is not null
                ? job with { State = JobState.Processing, NextAttemptAt = _time.GetUtcNow(), LastError = null }
                : job with { State = JobState.Queued, NextAttemptAt = null, LastError = null });
        }

        Wake(platform);
    }

    /// <summary>Stops everything. Jobs stay as they are on disk, for the next start to pick up.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();

        List<Task> work;
        lock (_lock)
        {
            foreach (var lane in _lanes.Values)
            {
                lane.WakeUp();
            }

            work = [.. _work];
        }

        await Task.WhenAll(work);
        _stop.Dispose();
    }

    private static bool IsFinished(JobState state) => state is JobState.Completed or JobState.Failed or JobState.Cancelled;

    private static bool IsStorageProblem(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecretStoreException;

    private static int MaxAttemptsFor(Platform platform) => platform == Platform.TikTok ? MaxTikTokAttempts : MaxAttempts;

    /// <summary>What a saved job means now that the app has started again.</summary>
    private UploadJob Recover(UploadJob job) => job.State switch
    {
        // YouTube picks up its saved session. TikTok can't, so this counts as a try, since it's a new init.
        JobState.Uploading or JobState.Rendering => job with { State = JobState.Queued, Attempts = job.Attempts + 1, BytesSent = 0 },
        JobState.Processing or JobState.AwaitingCreator when job.RemoteId is null => job with { State = JobState.Queued, BytesSent = 0 },
        JobState.Processing => job with { NextAttemptAt = _time.GetUtcNow() },
        JobState.AwaitingCreator when !HasGivenUpOnCreator(job) => job with { NextAttemptAt = _time.GetUtcNow() },
        _ => job,
    };

    private bool HasGivenUpOnCreator(UploadJob job) =>
        job.UploadedAt is { } uploaded && _time.GetUtcNow() - uploaded >= CreatorGiveUp;

    private async Task RunLaneAsync(Lane lane)
    {
        while (!_stop.IsCancellationRequested)
        {
            var wake = lane.NextWake();
            var next = Dispatch(lane);

            if (next is not { } due)
            {
                await Task.WhenAny(wake, _stopped.Task);
                continue;
            }

            var delay = due - _time.GetUtcNow();
            if (delay <= TimeSpan.Zero)
            {
                continue;
            }

            using var sleep = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            await Task.WhenAny(wake, Task.Delay(delay > LongestSleep ? LongestSleep : delay, _time, sleep.Token));
            await sleep.CancelAsync();
        }
    }

    /// <summary>Starts whatever is due, and says when to look again. Null means nothing is coming up.</summary>
    private DateTimeOffset? Dispatch(Lane lane)
    {
        var now = _time.GetUtcNow();
        DateTimeOffset? next = null;

        void Consider(DateTimeOffset when) => next = next is { } soonest && soonest <= when ? soonest : when;

        lock (_lock)
        {
            if (_stop.IsCancellationRequested)
            {
                return null;
            }

            var jobs = _jobs.Values
                .Where(job => job.Platform == lane.Uploader.Platform && !_running.ContainsKey(job.Id))
                .OrderBy(job => job.CreatedAt)
                .ToList();

            foreach (var job in jobs.Where(job => job.State is JobState.Processing or JobState.AwaitingCreator && job.NextAttemptAt is not null))
            {
                if (lane.NeedsReconnect)
                {
                    continue;
                }

                if (job.NextAttemptAt <= now)
                {
                    StartLocked(lane, job, RunCheckAsync);
                }
                else
                {
                    Consider(job.NextAttemptAt!.Value);
                }
            }

            if (lane.PausedUntil <= now)
            {
                lane.PausedUntil = null;
            }

            if (lane.PausedUntil is { } until)
            {
                Consider(until);
            }

            if (lane.Uploading || lane.NeedsReconnect || lane.PausedUntil is not null)
            {
                return next;
            }

            foreach (var job in jobs.Where(job => job.State is JobState.Queued or JobState.Waiting))
            {
                if (job.NextAttemptAt is { } at && at > now)
                {
                    Consider(at);
                    continue;
                }

                lane.Uploading = true;
                StartLocked(lane, job, RunUploadAsync);
                return next;
            }

            return next;
        }
    }

    private void StartLocked(Lane lane, UploadJob job, Func<Lane, UploadJob, CancellationToken, Task> work)
    {
        var running = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        _running[job.Id] = running;
        _work.RemoveAll(task => task.IsCompleted);
        _work.Add(Task.Run(async () =>
        {
            try
            {
                await work(lane, job, running.Token);
            }
            catch (Exception exception)
            {
                // A job must never be left looking busy. The exception's own text could hold an address, so it's left out.
                OnUploadFailed(lane, job.Id, new UploadException(
                    lane.Uploader.Platform,
                    UploadFailureKind.Retryable,
                    $"Something unexpected went wrong ({exception.GetType().Name})."));
            }
            finally
            {
                bool cancelled;
                lock (_lock)
                {
                    _running.Remove(job.Id);
                    cancelled = _cancelling.Remove(job.Id);
                }

                running.Dispose();
                if (cancelled)
                {
                    EndCancelled(job.Id);
                }

                lane.WakeUp();
            }
        }));
    }

    private async Task RunUploadAsync(Lane lane, UploadJob queued, CancellationToken cancellationToken)
    {
        try
        {
            var job = queued;

            // A rendered file that's gone has to be made again, and a session for the old one can't carry on with new bytes.
            if (job.RenderedPath is { } made && !File.Exists(made))
            {
                ForgetSession(job.Id);
                job = Change(job.Id, current => current with { RenderedPath = null }) ?? job;
            }

            if (job.RenderedPath is null && !job.KeptSource)
            {
                job = Change(job.Id, current => current with { State = JobState.Rendering, NextAttemptAt = null, BytesSent = 0 }) ?? job;
                var result = await _render.RenderAsync(job, new RenderProgress(new ProgressReporter(this, job.Id), job.TotalBytes), cancellationToken);
                var renderedSize = result.Path is { } path && File.Exists(path) ? new FileInfo(path).Length : (long?)null;
                job = Change(job.Id, current => current with
                {
                    RenderedPath = result.Path,
                    KeptSource = result.Path is null,
                    Note = result.Note,
                    TotalBytes = renderedSize ?? current.TotalBytes,
                }) ?? job;
            }

            job = Change(job.Id, current => current with { State = JobState.Uploading, NextAttemptAt = null, BytesSent = 0 }) ?? job;

            var receipt = await lane.Uploader.UploadAsync(job, new ProgressReporter(this, job.Id), cancellationToken);
            var now = _time.GetUtcNow();

            job = Change(job.Id, current => current with
            {
                State = JobState.Processing,
                RemoteId = receipt.RemoteId,
                Warning = receipt.Warning,
                UploadedAt = now,
                NextAttemptAt = now,
                BytesSent = current.TotalBytes,
                LastError = null,
            }) ?? job;

            // The video has arrived, so stopping it now can only mean stopping following it.
            lock (_lock)
            {
                _cancelling.Remove(job.Id);
            }

            ReleaseRendered(job);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping leaves the job as it is on disk. A user's cancel is finished off when this returns.
        }
        catch (UploadException exception)
        {
            OnUploadFailed(lane, queued.Id, exception);
        }
        catch (ReconnectRequiredException exception)
        {
            OnUploadFailed(lane, queued.Id, new UploadException(lane.Uploader.Platform, UploadFailureKind.Reconnect, exception.Message));
        }
        catch (Exception exception) when (IsStorageProblem(exception))
        {
            OnUploadFailed(lane, queued.Id, new UploadException(lane.Uploader.Platform, UploadFailureKind.Retryable, exception.Message));
        }
        finally
        {
            lock (_lock)
            {
                lane.Uploading = false;
            }
        }
    }

    /// <summary>A rendered file goes once the platform has it, unless another job from the same video still wants it.</summary>
    private void ReleaseRendered(UploadJob job)
    {
        if (job.RenderedPath is not { } path)
        {
            return;
        }

        lock (_lock)
        {
            var wanted = _jobs.Values.Any(other =>
                other.Id != job.Id &&
                other.BatchId == job.BatchId &&
                other.Details.Encode == job.Details.Encode &&
                other.RemoteId is null &&
                other.State is not (JobState.Completed or JobState.Cancelled));

            if (wanted)
            {
                return;
            }
        }

        _render.Release(path);
    }

    private void OnUploadFailed(Lane lane, Guid id, UploadException failure)
    {
        var now = _time.GetUtcNow();

        switch (failure.Kind)
        {
            case UploadFailureKind.PlatformCap:
                var until = failure.RetryAt ?? now + DefaultCapWait;
                lock (_lock)
                {
                    lane.PausedUntil = until;
                }

                Change(id, job => job with
                {
                    State = JobState.Waiting,
                    NextAttemptAt = until,
                    LastError = failure.Message,
                    RemoteId = null,
                    UploadedAt = null,
                    BytesSent = 0,
                });
                break;

            case UploadFailureKind.Reconnect:
                lock (_lock)
                {
                    lane.NeedsReconnect = true;
                }

                Change(id, job => job with { State = JobState.NeedsReconnect, LastError = failure.Message, NextAttemptAt = null });
                break;

            case UploadFailureKind.Retryable:
                Change(id, job =>
                {
                    var attempts = job.Attempts + 1;

                    // The session stays: if the last answer was what got lost, trying again later must find that video, not send another.
                    if (attempts >= MaxAttemptsFor(job.Platform))
                    {
                        return job with
                        {
                            State = JobState.Failed,
                            Attempts = attempts,
                            LastError = failure.Message,
                            NextAttemptAt = null,
                            RemoteId = null,
                            UploadedAt = null,
                        };
                    }

                    var backoff = Backoff(attempts);
                    var at = failure.RetryAt is { } retryAt && retryAt > now + backoff ? retryAt : now + backoff;
                    return job with
                    {
                        State = JobState.Waiting,
                        Attempts = attempts,
                        NextAttemptAt = at,
                        LastError = failure.Message,
                        RemoteId = null,
                        UploadedAt = null,
                    };
                });
                break;

            default:
                ForgetSession(id);
                Change(id, job => job with { State = JobState.Failed, LastError = failure.Message, NextAttemptAt = null });
                break;
        }
    }

    private TimeSpan Backoff(int attempts)
    {
        var ticks = Math.Min(FirstBackoff.Ticks * Math.Pow(2, attempts - 1), LongestBackoff.Ticks);
        return TimeSpan.FromTicks((long)(ticks * (1 + (_random() / 4))));
    }

    private async Task RunCheckAsync(Lane lane, UploadJob queued, CancellationToken cancellationToken)
    {
        try
        {
            var job = queued;
            DateTimeOffset? playlistRetryAt = null;
            if (job.NeedsPlaylistStep)
            {
                (job, playlistRetryAt) = await RunPlaylistStepAsync(job, cancellationToken);
            }

            var state = await lane.Uploader.WaitForResultAsync(job.RemoteId!, cancellationToken);
            var now = _time.GetUtcNow();

            switch (state)
            {
                case JobState.Completed when job.NeedsPlaylistStep:
                    Change(job.Id, current => current with { NextAttemptAt = playlistRetryAt > now ? playlistRetryAt : now + PlaylistRetry });
                    break;

                case JobState.Completed:
                    ForgetSession(job.Id);
                    Change(job.Id, current => current with { State = JobState.Completed, NextAttemptAt = null, LastError = null });
                    break;

                case JobState.AwaitingCreator:
                    Change(job.Id, current => current with
                    {
                        State = JobState.AwaitingCreator,
                        NextAttemptAt = HasGivenUpOnCreator(current) ? null : now + CreatorRecheck,
                    });
                    break;

                default:
                    Change(job.Id, current => job.UploadedAt is { } uploaded && now - uploaded >= ProcessingGiveUp
                        ? current with
                        {
                            State = JobState.Failed,
                            NextAttemptAt = null,
                            LastError = $"{current.Platform} still hasn't finished processing this video after 3 days. Check on it there.",
                        }
                        : current with { NextAttemptAt = now + ProcessingRecheck });
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (UploadException exception)
        {
            // Here, retryable or capped means the platform dropped the video, so it has to be sent again.
            OnUploadFailed(lane, queued.Id, exception);
        }
        catch (Exception exception) when (exception is ReconnectRequiredException)
        {
            OnUploadFailed(lane, queued.Id, new UploadException(lane.Uploader.Platform, UploadFailureKind.Reconnect, exception.Message));
        }
        catch (Exception exception) when (IsStorageProblem(exception))
        {
            Change(queued.Id, current => current with { NextAttemptAt = _time.GetUtcNow() + ProcessingRecheck });
        }
    }

    /// <summary>
    /// A failure here never touches the upload: the video is safely on the platform either way. Returns the job, and
    /// when to try again if it's still to do.
    /// </summary>
    private async Task<(UploadJob Job, DateTimeOffset? RetryAt)> RunPlaylistStepAsync(UploadJob job, CancellationToken cancellationToken)
    {
        var playlistId = ((YouTubePostDetails)job.Details).PlaylistId!;

        try
        {
            if (_youTube is null)
            {
                throw new UploadException(Platform.YouTube, UploadFailureKind.Permanent, "Playlists aren't available.");
            }

            await _youTube.AddToPlaylistAsync(playlistId, job.RemoteId!, cancellationToken);
            return (Change(job.Id, current => current with { PlaylistAdded = true }) ?? job, null);
        }
        catch (UploadException exception) when (exception.Kind == UploadFailureKind.PlatformCap)
        {
            return (job, exception.RetryAt);
        }
        catch (UploadException exception) when (exception.Kind == UploadFailureKind.Retryable && job.PlaylistAttempts + 1 < MaxPlaylistAttempts)
        {
            return (Change(job.Id, current => current with { PlaylistAttempts = current.PlaylistAttempts + 1 }) ?? job, exception.RetryAt);
        }
        catch (UploadException exception)
        {
            return (Change(job.Id, current => current with { PlaylistError = exception.Message }) ?? job, null);
        }
    }

    private void EndCancelled(Guid id)
    {
        ForgetSession(id);
        Change(id, job => IsFinished(job.State) ? job : job with { State = JobState.Cancelled, NextAttemptAt = null });
    }

    private void ForgetSession(Guid id)
    {
        try
        {
            _sessions.Delete(id);
        }
        catch (Exception exception) when (IsStorageProblem(exception))
        {
            SaveFailed?.Invoke(exception.Message);
        }
    }

    /// <summary>Makes a change, saves it and tells everyone. Null means the job is gone.</summary>
    private UploadJob? Change(Guid id, Func<UploadJob, UploadJob> change)
    {
        UploadJob changed;
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out var current))
            {
                return null;
            }

            changed = change(current);
            if (changed == current)
            {
                return current;
            }

            _jobs[id] = changed;
        }

        Save(changed);
        JobChanged?.Invoke(changed);
        return changed;
    }

    /// <summary>Saves the job as it is now, so changes made at the same moment can't land on disk in the wrong order.</summary>
    private void Save(UploadJob job)
    {
        try
        {
            lock (_saveLock)
            {
                UploadJob? latest;
                lock (_lock)
                {
                    _jobs.TryGetValue(job.Id, out latest);
                }

                if (latest is not null)
                {
                    _store.Save(job.Id, latest);
                }
            }
        }
        catch (Exception exception) when (IsStorageProblem(exception))
        {
            SaveFailed?.Invoke(exception.Message);
        }
    }

    private void Wake(Platform platform)
    {
        lock (_lock)
        {
            if (_lanes.TryGetValue(platform, out var lane))
            {
                lane.WakeUp();
            }
        }
    }

    /// <summary>Progress stays in memory, since a restart asks the platform anyway. It's reported often, so it's thinned out.</summary>
    private sealed class ProgressReporter(UploadQueue queue, Guid id) : IProgress<long>
    {
        private DateTimeOffset _lastReport;

        public void Report(long value)
        {
            var now = queue._time.GetUtcNow();
            if (now - _lastReport < ProgressInterval)
            {
                return;
            }

            _lastReport = now;
            UploadJob? changed = null;
            lock (queue._lock)
            {
                if (queue._jobs.TryGetValue(id, out var current))
                {
                    changed = current with { BytesSent = value };
                    queue._jobs[id] = changed;
                }
            }

            if (changed is not null)
            {
                queue.JobChanged?.Invoke(changed);
            }
        }
    }

    /// <summary>Rendering shows on the same progress bar as uploading, so percent becomes bytes of the source.</summary>
    private sealed class RenderProgress(ProgressReporter reporter, long total) : IProgress<double>
    {
        public void Report(double value) => reporter.Report((long)(total * Math.Clamp(value, 0, 100) / 100));
    }

    private sealed class Lane(IVideoUploader uploader)
    {
        private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IVideoUploader Uploader { get; } = uploader;
        public bool Uploading { get; set; }
        public bool NeedsReconnect { get; set; }
        public DateTimeOffset? PausedUntil { get; set; }

        public Task NextWake() => Volatile.Read(ref _wake).Task;

        public void WakeUp()
        {
            var old = Interlocked.Exchange(ref _wake, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            old.TrySetResult();
        }
    }
}
