using System.ComponentModel;
using System.Globalization;
using System.Text;
using VideoDispatch.Core.Accounts;
using VideoDispatch.Core.Accounts.Secrets;
using VideoDispatch.Core.Media;
using VideoDispatch.Core.Model;
using VideoDispatch.Core.Presets;
using VideoDispatch.Core.Queue;
using VideoDispatch.Core.Templates;
using VideoDispatch.Core.YouTube;

namespace VideoDispatch.App;

/// <summary>
/// What the New post screen shows and does. Presets only fill in the fields: everything stays editable, and what's
/// checked, previewed and queued all comes from the same <see cref="BuildPosts"/>, so the preview is what gets sent.
/// </summary>
public sealed class NewPostPanel : INotifyPropertyChanged
{
    private readonly UploadQueue _queue;
    private readonly PresetLibrary _presets;
    private readonly YouTubeChoices _choices;
    private readonly Func<Platform, AccountStatus> _accountOf;
    private readonly Func<string, VideoMetadata?> _readMetadata;
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _timeZone;
    private readonly Dictionary<Platform, AccountStatus> _accounts = [];

    private VideoMetadata? _metadata;
    private long _size;
    private string? _path;
    private bool _postToTikTok = true;
    private bool _postToYouTube = true;
    private string _caption = "";
    private string _title = "";
    private string _description = "";
    private string _tags = "";
    private string? _categoryId;
    private string? _privacyId;
    private string? _madeForKidsId;
    private string? _playlistId;
    private bool _scheduled;
    private DateTime? _scheduleDate;
    private string _scheduleTime = "";
    private TikTokPreset? _tikTokPreset;
    private YouTubePreset? _youTubePreset;
    private bool _captionEdited;
    private bool _youTubeEdited;
    private QueueCheck _check = new([], []);

    public NewPostPanel(
        UploadQueue queue,
        PresetLibrary presets,
        YouTubeChoices choices,
        Func<Platform, AccountStatus> accountOf,
        Func<string, VideoMetadata?> readMetadata,
        TimeProvider time,
        TimeZoneInfo timeZone)
    {
        _queue = queue;
        _presets = presets;
        _choices = choices;
        _accountOf = accountOf;
        _readMetadata = readMetadata;
        _time = time;
        _timeZone = timeZone;
        presets.Changed += (_, _) => Refresh();
        choices.Changed += (_, _) => Refresh();
        Recheck();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool HasVideo => _path is not null;

    public string FileName => _path is null ? "No video chosen" : Path.GetFileName(_path);

    public string? MetadataText
    {
        get
        {
            if (_path is null)
            {
                return null;
            }

            var size = string.Format(CultureInfo.CurrentCulture, "{0:N1} MB", _size / (1024.0 * 1024.0));
            if (_metadata is not { } video)
            {
                return $"{size}. Its size, length and frame rate couldn't be read.";
            }

            var parts = new List<string> { size, $"{video.DisplayWidth} × {video.DisplayHeight}" };
            if (video.Duration is { } duration)
            {
                parts.Add(duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture));
            }

            if (video.FramesPerSecond is { } frames)
            {
                parts.Add(string.Format(CultureInfo.CurrentCulture, "{0:0.##} fps", frames));
            }

            if (video.CodecTag is { } codec)
            {
                parts.Add(codec);
            }

            return string.Join(", ", parts);
        }
    }

    public string? ShortsText => _path is null ? null : YouTubeVideoLimits.PredictShort(_metadata) switch
    {
        ShortsPrediction.Short => "YouTube will probably make this a Short: it's square or vertical, and three minutes or less.",
        ShortsPrediction.LongForm => "YouTube will probably make this a regular video, not a Short.",
        _ => "Couldn't tell whether YouTube will make this a Short.",
    };

    public IReadOnlyList<TikTokPreset> TikTokPresets => _presets.TikTok;

    public IReadOnlyList<YouTubePreset> YouTubePresets => _presets.YouTube;

    public IReadOnlyList<Choice> Categories => _choices.Categories;

    public IReadOnlyList<Choice> Playlists => _choices.Playlists;

    public IReadOnlyList<Choice> Privacies { get; } = YouTubeChoices.Privacies("Choose…");

    public IReadOnlyList<Choice> MadeForKidsChoices { get; } = YouTubeChoices.MadeForKids("Choose…");

    public string? ChoicesProblem => _choices.Problem;

    public bool HasChoicesProblem => _choices.Problem is not null;

    public TikTokPreset? TikTokPreset
    {
        get => _tikTokPreset;
        set
        {
            _tikTokPreset = value;
            _captionEdited = false;
            ApplyTikTokPreset();
            Recheck();
        }
    }

    public YouTubePreset? YouTubePreset
    {
        get => _youTubePreset;
        set
        {
            _youTubePreset = value;
            _youTubeEdited = false;
            ApplyYouTubePreset();
            Recheck();
        }
    }

    public bool PostToTikTok
    {
        get => _postToTikTok;
        set => Set(ref _postToTikTok, value);
    }

    public bool PostToYouTube
    {
        get => _postToYouTube;
        set => Set(ref _postToYouTube, value);
    }

    public string Caption
    {
        get => _caption;
        set
        {
            _captionEdited = true;
            Set(ref _caption, value);
        }
    }

    public string YouTubeTitle
    {
        get => _title;
        set
        {
            _youTubeEdited = true;
            Set(ref _title, value);
        }
    }

    public string Description
    {
        get => _description;
        set
        {
            _youTubeEdited = true;
            Set(ref _description, value);
        }
    }

    public string Tags
    {
        get => _tags;
        set
        {
            _youTubeEdited = true;
            Set(ref _tags, value);
        }
    }

    public string? CategoryId
    {
        get => _categoryId;
        set => Set(ref _categoryId, Choice.Clean(value));
    }

    public string? PrivacyId
    {
        get => _privacyId;
        set => Set(ref _privacyId, Choice.Clean(value));
    }

    public string? MadeForKidsId
    {
        get => _madeForKidsId;
        set => Set(ref _madeForKidsId, Choice.Clean(value));
    }

    public string? PlaylistId
    {
        get => _playlistId;
        set => Set(ref _playlistId, Choice.Clean(value));
    }

    public bool Scheduled
    {
        get => _scheduled;
        set
        {
            if (value && _scheduleDate is null)
            {
                var tomorrow = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _timeZone).Date.AddDays(1);
                _scheduleDate = tomorrow;
                _scheduleTime = "09:00";
            }

            Set(ref _scheduled, value);
        }
    }

    public DateTime? ScheduleDate
    {
        get => _scheduleDate;
        set => Set(ref _scheduleDate, value);
    }

    public string ScheduleTime
    {
        get => _scheduleTime;
        set => Set(ref _scheduleTime, value);
    }

    public IReadOnlyList<string> Problems => _check.Problems;

    public IReadOnlyList<string> Warnings => _check.Warnings;

    public bool HasWarnings => Warnings.Count > 0;

    public bool HasProblems => Problems.Count > 0;

    public bool CanQueue => _check.CanQueue && HasVideo && !IsBusy;

    public bool IsBusy { get; private set; }

    public string? Message { get; private set; }

    public bool HasMessage => Message is not null;

    public string TikTokPreview => BuildPosts().OfType<TikTokPostDetails>().FirstOrDefault() is { } post ? Describe(post) : "";

    public string YouTubePreview => BuildPosts().OfType<YouTubePostDetails>().FirstOrDefault() is { } post ? Describe(post) : "";

    /// <summary>Reads the state of the accounts again, off the UI thread, and asks YouTube for its lists if it hasn't yet.</summary>
    public async Task RefreshAsync()
    {
        try
        {
            var statuses = await Task.Run(() => Enum.GetValues<Platform>().Select(platform => _accountOf(platform)).ToList());
            foreach (var status in statuses)
            {
                _accounts[status.Platform] = status;
            }

            await _choices.LoadAsync();
        }
        catch (SecretStoreException exception)
        {
            Message = exception.Message;
        }

        Recheck();
    }

    /// <summary>Takes a video, reading what's in it off the UI thread.</summary>
    public async Task ChooseVideoAsync(string path)
    {
        var (size, metadata) = await Task.Run(() => (new FileInfo(path).Length, _readMetadata(path)));
        _path = path;
        _size = size;
        _metadata = metadata;
        Message = null;

        // The text may mention the file, so it follows the new one, unless the user has written over it.
        if (!_captionEdited)
        {
            ApplyTikTokPreset();
        }

        if (!_youTubeEdited)
        {
            ApplyYouTubePreset();
        }

        Recheck();
    }

    public async Task QueueAsync()
    {
        if (_path is not { } path)
        {
            return;
        }

        IsBusy = true;
        Message = null;
        Refresh();

        try
        {
            var posts = BuildPosts();
            var metadata = _metadata;
            await Task.Run(() => _queue.Add(path, metadata, posts));
            Clear();
            Message = "Queued. You can follow it under Queue.";
        }
        catch (QueueRefusedException exception)
        {
            _check = new QueueCheck(exception.Problems, _check.Warnings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Message = exception.Message;
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    /// <summary>What goes to the queue, built from the fields as they are now.</summary>
    public IReadOnlyList<PostDetails> BuildPosts()
    {
        var posts = new List<PostDetails>();
        if (_postToTikTok)
        {
            posts.Add(new TikTokPostDetails { Caption = _caption.Trim() });
        }

        if (_postToYouTube)
        {
            posts.Add(new YouTubePostDetails
            {
                Title = _title.Trim(),
                Description = _description.Trim(),
                Tags = PresetResolver.NormalizeTags(_tags.Split(',')),
                CategoryId = _categoryId,
                Privacy = YouTubeChoices.PrivacyFrom(_privacyId),
                MadeForKids = YouTubeChoices.MadeForKidsFrom(_madeForKidsId),
                PlaylistId = _playlistId,
                PublishAt = PublishAt(),
            });
        }

        return posts;
    }

    private static string Describe(TikTokPostDetails post)
    {
        var text = new StringBuilder();
        text.AppendLine("The video goes to your TikTok inbox as a draft. TikTok's upload can't carry a caption, so you finish the post in TikTok, and this is there to paste in:");
        text.AppendLine();
        text.Append(post.Caption.Length > 0 ? post.Caption : "(no caption)");
        return text.ToString();
    }

    private string Describe(YouTubePostDetails post)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.CurrentCulture, $"Title: {(post.Title.Length > 0 ? post.Title : "(none)")}");
        text.AppendLine(CultureInfo.CurrentCulture, $"Description: {(post.Description.Length > 0 ? post.Description : "(none)")}");
        text.AppendLine(CultureInfo.CurrentCulture, $"Tags: {(post.Tags.Count > 0 ? string.Join(", ", post.Tags) : "(none)")}");
        text.AppendLine(CultureInfo.CurrentCulture, $"Category: {_choices.Categories.FirstOrDefault(category => category.Id == (post.CategoryId ?? Choice.None))?.Title ?? post.CategoryId}");
        text.AppendLine(CultureInfo.CurrentCulture, $"Privacy: {post.Privacy?.ToString() ?? "(not chosen)"}");
        text.AppendLine(CultureInfo.CurrentCulture, $"Made for kids: {post.MadeForKids switch { true => "Yes", false => "No", null => "(not chosen)" }}");
        text.AppendLine(CultureInfo.CurrentCulture, $"Playlist: {_choices.Playlists.FirstOrDefault(playlist => playlist.Id == (post.PlaylistId ?? Choice.None))?.Title ?? post.PlaylistId}");
        text.Append(CultureInfo.CurrentCulture, $"Publishes: {(post.PublishAt is { } at ? TimeZoneInfo.ConvertTime(at, _timeZone).ToString("f", CultureInfo.CurrentCulture) : "When it's uploaded, at the privacy above")}");
        return text.ToString();
    }

    private bool TryReadScheduleTime(out TimeOnly time) =>
        TimeOnly.TryParse(_scheduleTime, CultureInfo.CurrentCulture, DateTimeStyles.None, out time);

    private DateTimeOffset? PublishAt()
    {
        if (!_scheduled || _scheduleDate is not { } date || !TryReadScheduleTime(out var time))
        {
            return null;
        }

        var now = _time.GetUtcNow();
        var daysAhead = (DateOnly.FromDateTime(date).DayNumber) - DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, _timeZone).DateTime).DayNumber;
        return PresetResolver.ResolveSchedule(new YouTubeSchedule(daysAhead, time), now, _timeZone);
    }

    private TemplateContext Context() =>
        TemplateContext.ForFile(_path ?? "video", DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _timeZone).DateTime));

    private void ApplyTikTokPreset()
    {
        if (_tikTokPreset is { } preset)
        {
            _caption = PresetResolver.Resolve(preset, Context()).Details.Caption;
        }
    }

    private void ApplyYouTubePreset()
    {
        if (_youTubePreset is not { } preset)
        {
            if (_path is not null)
            {
                _title = Path.GetFileNameWithoutExtension(_path);
            }

            return;
        }

        var details = PresetResolver.Resolve(preset, Context(), _time.GetUtcNow(), _timeZone).Details;
        _title = details.Title;
        _description = details.Description;
        _tags = string.Join(", ", details.Tags);
        _categoryId = details.CategoryId;
        _privacyId = details.Privacy?.ToString();
        _madeForKidsId = YouTubeChoices.IdOf(details.MadeForKids);
        _playlistId = details.PlaylistId;
        _scheduled = details.PublishAt is not null;

        if (details.PublishAt is { } at)
        {
            var local = TimeZoneInfo.ConvertTime(at, _timeZone);
            _scheduleDate = local.Date;
            _scheduleTime = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        }
    }

    private void Clear()
    {
        _path = null;
        _size = 0;
        _metadata = null;
        _caption = "";
        _title = "";
        _description = "";
        _tags = "";
        _privacyId = null;
        _madeForKidsId = null;
        _playlistId = null;
        _categoryId = null;
        _scheduled = false;
        _scheduleDate = null;
        _scheduleTime = "";
        _tikTokPreset = null;
        _youTubePreset = null;
        _captionEdited = false;
        _youTubeEdited = false;
        _check = new QueueCheck([], []);
        Recheck();
    }

    private void Recheck()
    {
        var problems = new List<string>();
        var warnings = new List<string>();
        var now = _time.GetUtcNow();

        if (_path is not null)
        {
            if (!_postToTikTok && !_postToYouTube)
            {
                problems.Add("Choose where to post this video.");
            }

            if (_postToYouTube && _scheduled && PublishAt() is null)
            {
                problems.Add("Enter the scheduled date, and the time like 18:30.");
            }

            foreach (var post in BuildPosts())
            {
                if (!_accounts.TryGetValue(post.Platform, out var account))
                {
                    account = new AccountStatus(post.Platform, false, ConnectionState.NotConnected, null, []);
                }

                var check = QueueChecks.Check(post, _path, _size, _metadata, account, now);
                problems.AddRange(check.Problems);
                warnings.AddRange(check.Warnings);
            }
        }

        _check = new QueueCheck(problems.Distinct().ToList(), warnings.Distinct().ToList());
        Refresh();
    }

    private void Set<T>(ref T field, T value)
    {
        field = value;
        Recheck();
    }

    private void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
}
