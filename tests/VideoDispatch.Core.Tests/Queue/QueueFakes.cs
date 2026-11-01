using System.Collections.Concurrent;
using VideoDispatch.Core.Model;
using VideoDispatch.Core.Queue;
using VideoDispatch.Core.Uploads;
using VideoDispatch.Core.YouTube;

namespace VideoDispatch.Core.Tests.Queue;

/// <summary>A clock that only moves when told, and fires whatever falls due.</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private readonly object _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_lock)
        {
            _now += by;
            due = _timers.Where(timer => timer.Due <= _now).ToList();
            _timers.RemoveAll(due.Contains);
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, () => callback(state));
        timer.Change(dueTime, period);
        return timer;
    }

    private sealed class ManualTimer(ManualTime time, Action fire) : ITimer
    {
        public DateTimeOffset Due { get; private set; }

        public void Fire() => ThreadPool.QueueUserWorkItem(_ => fire());

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (time._lock)
            {
                time._timers.Remove(this);
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    Due = time._now + dueTime;
                    time._timers.Add(this);
                }
            }

            return true;
        }

        public void Dispose() => Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Does what each test says, one step at a time.</summary>
internal sealed class FakeUploader(Platform platform) : IVideoUploader
{
    public Platform Platform { get; } = platform;

    public ConcurrentQueue<Guid> Uploads { get; } = new();
    public ConcurrentQueue<string> Checks { get; } = new();

    /// <summary>What an upload does. By default it succeeds at once.</summary>
    public Func<UploadJob, IProgress<long>?, CancellationToken, Task<UploadReceipt>> Upload { get; set; } =
        (job, _, _) => Task.FromResult(new UploadReceipt("remote-" + job.Id.ToString("N")));

    /// <summary>What a check does. By default the video is done.</summary>
    public Func<string, CancellationToken, Task<JobState>> Check { get; set; } =
        (_, _) => Task.FromResult(JobState.Completed);

    public async Task<UploadReceipt> UploadAsync(UploadJob job, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        Uploads.Enqueue(job.Id);
        return await Upload(job, progress, cancellationToken);
    }

    public async Task<JobState> WaitForResultAsync(string remoteId, CancellationToken cancellationToken = default)
    {
        Checks.Enqueue(remoteId);
        return await Check(remoteId, cancellationToken);
    }
}

internal sealed class FakeYouTubeApi : IYouTubeApi
{
    public ConcurrentQueue<(string Playlist, string Video)> Added { get; } = new();

    public Func<Task> Add { get; set; } = () => Task.CompletedTask;

    public Task<IReadOnlyList<YouTubePlaylist>> ListPlaylistsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<YouTubePlaylist>>([]);

    public Task<IReadOnlyList<YouTubeCategory>> ListCategoriesAsync(string regionCode, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<YouTubeCategory>>([]);

    public async Task AddToPlaylistAsync(string playlistId, string videoId, CancellationToken cancellationToken = default)
    {
        Added.Enqueue((playlistId, videoId));
        await Add();
    }
}

internal sealed class MemoryRecords<T> : IRecordStore<T>
{
    public ConcurrentDictionary<Guid, T> Saved { get; } = new();
    public List<string> Damaged { get; } = [];

    /// <summary>Set to make every save fail.</summary>
    public Exception? SaveThrows { get; set; }

    public StoreLoad<T> LoadAll() => new(Saved.Values.ToList(), Damaged);

    public void Save(Guid id, T item)
    {
        if (SaveThrows is not null)
        {
            throw SaveThrows;
        }

        Saved[id] = item;
    }

    public void Delete(Guid id) => Saved.TryRemove(id, out _);
}

internal sealed class RecordingRender(string? rendered = null) : IRenderStep
{
    public List<JobState> StatesSeen { get; } = [];

    public Task<string?> RenderAsync(UploadJob job, CancellationToken cancellationToken = default)
    {
        StatesSeen.Add(job.State);
        return Task.FromResult(rendered);
    }
}
