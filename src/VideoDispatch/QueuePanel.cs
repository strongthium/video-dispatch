using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using VideoDispatch.Core.Model;
using VideoDispatch.Core.Queue;

namespace VideoDispatch.App;

/// <summary>What the Queue screen shows and does. The queue reports from its own threads, so changes are posted to the UI's.</summary>
public sealed class QueuePanel : INotifyPropertyChanged
{
    private readonly UploadQueue _queue;
    private readonly Action<Action> _post;
    private readonly Dictionary<Guid, JobRow> _rows = [];
    private string? _saveProblem;
    private IReadOnlyList<string> _damaged = [];

    /// <param name="queue">The queue to show.</param>
    /// <param name="post">Runs something on the UI thread.</param>
    public QueuePanel(UploadQueue queue, Action<Action> post)
    {
        _queue = queue;
        _post = post;
        queue.JobChanged += job => _post(() => Show(job));
        queue.SaveFailed += problem => _post(() =>
        {
            _saveProblem = problem;
            Refresh();
        });

        foreach (var job in queue.Jobs)
        {
            Show(job);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Newest first.</summary>
    public ObservableCollection<JobRow> Rows { get; } = [];

    public bool IsEmpty => Rows.Count == 0;

    /// <summary>Anything wrong with the queue as a whole.</summary>
    public string? Notice
    {
        get
        {
            var notes = new List<string>();
            foreach (var platform in Enum.GetValues<Platform>().Where(_queue.NeedsReconnect))
            {
                notes.Add($"{platform} uploads are paused until the account is connected again, under Accounts.");
            }

            if (_saveProblem is not null)
            {
                notes.Add($"A change to the queue couldn't be saved, so it may be lost if the app closes: {_saveProblem}");
            }

            if (_damaged.Count > 0)
            {
                notes.Add($"{_damaged.Count} saved {(_damaged.Count == 1 ? "job" : "jobs")} couldn't be read, and were set aside. The rest are fine.");
            }

            return notes.Count == 0 ? null : string.Join(" ", notes);
        }
    }

    public bool HasNotice => Notice is not null;

    public void ReportDamaged(IReadOnlyList<string> damaged)
    {
        _damaged = damaged;
        Refresh();
    }

    public Task RetryAsync(JobRow row) => Task.Run(() => _queue.Retry(row.Id));

    public Task CancelAsync(JobRow row) => Task.Run(() => _queue.Cancel(row.Id));

    public Task MarkPostedAsync(JobRow row) => Task.Run(() => _queue.MarkPosted(row.Id));

    public Task RemoveAsync(JobRow row) => Task.Run(() => _queue.Remove(row.Id));

    private void Show(UploadJob job)
    {
        // A job that's gone from the queue was removed.
        if (_queue.Jobs.All(current => current.Id != job.Id))
        {
            if (_rows.Remove(job.Id, out var gone))
            {
                Rows.Remove(gone);
                Refresh();
            }

            return;
        }

        if (_rows.TryGetValue(job.Id, out var row))
        {
            row.Update(job);
        }
        else
        {
            row = new JobRow(job);
            _rows[job.Id] = row;
            Rows.Insert(0, row);
        }

        Refresh();
    }

    private void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
}

/// <summary>One job on the Queue screen, in words.</summary>
public sealed class JobRow(UploadJob job) : INotifyPropertyChanged
{
    private const double Mega = 1024 * 1024;

    private UploadJob _job = job;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id => _job.Id;

    public JobState State => _job.State;

    public string Title => _job.Details is YouTubePostDetails { Title.Length: > 0 } details
        ? details.Title
        : Path.GetFileName(_job.SourcePath);

    public string PlatformText => _job.Platform.ToString();

    public string FileText => Path.GetFileName(_job.SourcePath);

    public string StateText => _job.State switch
    {
        JobState.Queued => "Waiting its turn",
        JobState.Rendering when _job.Details.Encode is not null => string.Format(CultureInfo.CurrentCulture, "Re-encoding: {0:0}%", Percent),
        JobState.Rendering => "Getting it ready",
        JobState.Uploading => $"Uploading: {Mb(_job.BytesSent)} of {Mb(_job.TotalBytes)}",
        JobState.Processing when _job.Platform == Platform.TikTok => "TikTok is processing it",
        JobState.Processing => "YouTube is processing it",
        JobState.AwaitingCreator => "Waiting in your TikTok inbox. Open TikTok to finish the post.",
        JobState.Waiting when _job.NextAttemptAt is { } at => $"Trying again {When(at)}",
        JobState.Waiting => "Waiting to try again",
        JobState.NeedsReconnect => $"Connect your {_job.Platform} account again, under Accounts",
        JobState.Completed when _job.Platform == Platform.TikTok => "Posted",
        JobState.Completed => "Uploaded and processed",
        JobState.Failed => "Failed",
        JobState.Cancelled => _job.RemoteId is null ? "Cancelled" : $"No longer followed. The video is still on {_job.Platform}.",
        _ => _job.State.ToString(),
    };

    /// <summary>Why it's in the state it is, or anything else to know.</summary>
    public string? Detail
    {
        get
        {
            var notes = new List<string>();
            if (_job.State is JobState.Waiting or JobState.Failed or JobState.NeedsReconnect && _job.LastError is { } error)
            {
                notes.Add(error!);
            }

            if (_job.Note is { } note)
            {
                notes.Add(note);
            }

            if (_job.Warning is { } warning)
            {
                notes.Add(warning);
            }

            if (_job.PlaylistError is { } playlist)
            {
                notes.Add($"The video is fine, but it wasn't added to the playlist: {playlist}");
            }

            return notes.Count == 0 ? null : string.Join(" ", notes);
        }
    }

    public bool HasDetail => Detail is not null;

    public bool IsProblem => _job.State == JobState.Failed || _job.Warning is not null || _job.PlaylistError is not null;

    public bool ShowsProgress => _job.State is JobState.Uploading or JobState.Rendering or JobState.Processing;

    public bool ProgressIsUnknown => _job.State is not JobState.Uploading && !(_job.State == JobState.Rendering && _job.Details.Encode is not null);

    public double Percent => _job.TotalBytes > 0 ? Math.Min(100, _job.BytesSent * 100.0 / _job.TotalBytes) : 0;

    /// <summary>TikTok's upload has no caption field, so the caption is here to paste in.</summary>
    public string? Caption => _job.Details is TikTokPostDetails { Caption.Length: > 0 } details ? details.Caption : null;

    public bool ShowsCaption => Caption is not null && _job.State is JobState.AwaitingCreator;

    public bool CanMarkPosted => _job.State == JobState.AwaitingCreator;

    public bool CanRetry => _job.State is JobState.Failed or JobState.Cancelled or JobState.Waiting or JobState.Processing or JobState.AwaitingCreator;

    public string RetryText => _job.State switch
    {
        JobState.Waiting => "Try now",
        JobState.Processing or JobState.AwaitingCreator => "Check now",
        _ when _job.RemoteId is not null => "Check again",
        _ => "Try again",
    };

    public bool CanCancel => _job.State is not (JobState.Completed or JobState.Failed or JobState.Cancelled);

    /// <summary>Once the platform has the video, stopping can't take it back, and the button must not suggest it can.</summary>
    public string CancelText => _job.RemoteId is null ? "Cancel" : "Stop following";

    public string CancelHint => _job.RemoteId is null
        ? "Stops this job. Nothing has been posted."
        : $"The video stays on {_job.Platform}. This only stops following it.";

    public bool CanRemove => !CanCancel;

    public Uri? StudioLink => _job is { Platform: Platform.YouTube, RemoteId: { } id }
        ? new Uri($"https://studio.youtube.com/video/{Uri.EscapeDataString(id)}/edit")
        : null;

    public bool HasStudioLink => StudioLink is not null;

    public void Update(UploadJob job)
    {
        _job = job;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
    }

    private static string Mb(long bytes) => string.Format(CultureInfo.CurrentCulture, "{0:N1} MB", bytes / Mega);

    private static string When(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        return local.Date == DateTime.Today
            ? $"at {local.ToString("t", CultureInfo.CurrentCulture)}"
            : $"on {local.ToString("g", CultureInfo.CurrentCulture)}";
    }
}
