using System.ComponentModel;
using System.Globalization;
using VideoDispatch.Core.Editing;
using VideoDispatch.Core.Media;
using VideoDispatch.Core.Reencoding;

namespace VideoDispatch.App;

/// <summary>
/// What the Edit screen shows and does: clips in a row, cut, trimmed, put in order and saved as one new video. Every
/// change can be undone. The edit is kept between runs, and the videos in it are only ever read.
/// </summary>
public sealed class EditPanel : INotifyPropertyChanged
{
    private const int MostUndos = 100;

    private readonly EditStore _store;
    private readonly Func<string, VideoMetadata?> _readMetadata;
    private readonly Func<string, CancellationToken, Task<TimeSpan?>> _readLength;
    private readonly Func<IVideoEncoder> _encoder;
    private readonly Func<string, bool> _exists;
    private readonly Dictionary<string, int> _turns = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<EditProject> _undo = [];
    private readonly List<EditProject> _redo = [];

    private EditProject _project = EditProject.Empty;
    private int _selected = -1;
    private TimeSpan _position;
    private CancellationTokenSource? _saving;

    /// <param name="store">Keeps the edit between runs.</param>
    /// <param name="readMetadata">Reads what's in a video.</param>
    /// <param name="readLength">How long a video is, for one whose metadata can't be read.</param>
    /// <param name="encoder">The engine to save with now. It's asked each time, since the user can change it.</param>
    /// <param name="exists">Whether a video is still where it was.</param>
    public EditPanel(EditStore store, Func<string, VideoMetadata?> readMetadata, Func<string, CancellationToken, Task<TimeSpan?>> readLength, Func<IVideoEncoder> encoder, Func<string, bool> exists)
    {
        _store = store;
        _readMetadata = readMetadata;
        _readLength = readLength;
        _encoder = encoder;
        _exists = exists;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Asks for a saved video to be posted.</summary>
    public event Action<string>? UseVideo;

    public EditProject Project => _project;

    public bool HasClips => _project.Clips.Count > 0;

    public bool IsEmpty => !HasClips;

    public TimeSpan Length => _project.Length;

    /// <summary>The clip chosen on the timeline, or -1.</summary>
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            _selected = value >= 0 && value < _project.Clips.Count ? value : -1;
            Refresh();
        }
    }

    public bool HasSelection => _selected >= 0;

    /// <summary>Where the playhead is, in the edit.</summary>
    public TimeSpan Position
    {
        get => _position;
        set
        {
            _position = value < TimeSpan.Zero ? TimeSpan.Zero : value > Length ? Length : value;
            Refresh();
        }
    }

    public string TimeText => $"{Format(_position)} / {Format(Length)}";

    public string? SelectionText => HasSelection && _project.Clips[_selected] is { } clip
        ? $"{clip.Name}: {Format(clip.Start)} to {Format(clip.End)} of its {Format(clip.SourceLength)}"
        : HasClips ? "Click a clip to choose it. Drag it to move it, or drag its edges to trim it." : null;

    public bool CanSplit => !IsSaving && _project.SplitAt(_position) is not null;

    public bool CanDelete => !IsSaving && HasSelection;

    public bool CanUndo => !IsSaving && _undo.Count > 0;

    public bool CanRedo => !IsSaving && _redo.Count > 0;

    public bool CanEdit => !IsSaving;

    public bool CanSave => !IsSaving && HasClips;

    public bool IsSaving => _saving is not null;

    public double SavePercent { get; private set; }

    public string? Message { get; private set; }

    public bool MessageIsError { get; private set; }

    public bool HasMessage => Message is not null;

    /// <summary>The video saved last, ready to post.</summary>
    public string? SavedPath { get; private set; }

    public bool HasSaved => SavedPath is not null && !IsSaving;

    public string SuggestedFolder => HasClips ? Path.GetDirectoryName(_project.Clips[0].SourcePath) ?? "" : "";

    public string SuggestedName => HasClips ? Path.GetFileNameWithoutExtension(_project.Clips[0].SourcePath) + " edited.mp4" : "edited.mp4";

    /// <summary>Like 1:05.3, or 1:02:05.3 past an hour.</summary>
    public static string Format(TimeSpan time) => time.TotalHours >= 1
        ? time.ToString(@"h\:mm\:ss\.f", CultureInfo.InvariantCulture)
        : time.ToString(@"m\:ss\.f", CultureInfo.InvariantCulture);

    /// <summary>How far a video is to be turned clockwise to be shown upright, as phones mark it: 0, 90, 180 or 270.</summary>
    public int RotationOf(string path)
    {
        if (!_turns.TryGetValue(path, out var degrees))
        {
            _turns[path] = degrees = _readMetadata(path)?.RotationDegrees ?? 0;
        }

        return degrees;
    }

    /// <summary>Picks up the edit from last time, and says if any of its videos have gone.</summary>
    public async Task LoadAsync()
    {
        var (project, missing) = await Task.Run(() =>
        {
            var loaded = _store.Load();
            return (loaded, loaded.Sources.Where(source => !_exists(source)).ToList());
        });

        _project = project;
        if (missing.Count > 0)
        {
            Fail($"{string.Join(", ", missing.Select(Path.GetFileName))} can't be found. Put {(missing.Count == 1 ? "it" : "them")} back, or take {(missing.Count == 1 ? "its" : "their")} clips out, before saving.");
        }

        Refresh();
    }

    /// <summary>Adds whole videos as clips, before clip <paramref name="at"/> or at the end, as one change.</summary>
    public async Task AddVideosAsync(IReadOnlyList<string> paths, int? at = null)
    {
        if (IsSaving || paths.Count == 0)
        {
            return;
        }

        var read = new List<(string Path, TimeSpan? Length)>();
        foreach (var path in paths)
        {
            var length = await Task.Run(() => _readMetadata(path)?.Duration);
            read.Add((path, length is { TotalMilliseconds: > 0 } known ? known : await _readLength(path, CancellationToken.None)));
        }

        ClearMessage();
        var index = Math.Clamp(at ?? _project.Clips.Count, 0, _project.Clips.Count);
        var project = _project;
        var added = 0;
        foreach (var (path, length) in read)
        {
            if (length is { } known && known >= EditProject.ShortestClip)
            {
                project = project.Insert(EditClip.Whole(path, known), index + added);
                added++;
            }
        }

        var unread = read.Where(video => video.Length is not { } known || known < EditProject.ShortestClip).Select(video => Path.GetFileName(video.Path)).ToList();
        if (unread.Count > 0)
        {
            Fail($"{string.Join(", ", unread)} couldn't be read as {(unread.Count == 1 ? "a video" : "videos")}. Windows may not know {(unread.Count == 1 ? "its" : "their")} format.");
        }

        if (added > 0)
        {
            Commit(project, index);
            Position = _project.StartOf(index);
        }
    }

    /// <summary>Starts again with one video, as New post asks. Undo brings back the edit there was.</summary>
    public async Task StartWithAsync(string path)
    {
        if (IsSaving)
        {
            return;
        }

        var before = _project;
        _project = EditProject.Empty;
        await AddVideosAsync([path]);
        if (_project.Clips.Count == 0)
        {
            _project = before;
            Refresh();
            return;
        }

        // One step back, to before the new video, rather than two.
        _undo.RemoveAt(_undo.Count - 1);
        Remember(before);
        Position = TimeSpan.Zero;
    }

    /// <summary>Cuts the clip under the playhead in two, and chooses the second half.</summary>
    public void Split()
    {
        if (CanSplit && _project.SplitAt(_position) is { } split && _project.Find(_position) is { } moment)
        {
            Commit(split, moment.Index + 1);
        }
    }

    public void DeleteSelected()
    {
        if (CanDelete)
        {
            var index = _selected;
            Commit(_project.Remove(index), Math.Min(index, _project.Clips.Count - 2));
        }
    }

    /// <summary>Takes clip <paramref name="from"/> out and puts it back so it ends up at <paramref name="to"/>.</summary>
    public void Move(int from, int to)
    {
        if (CanEdit && from != to && from >= 0 && from < _project.Clips.Count)
        {
            Commit(_project.Move(from, to), Math.Clamp(to, 0, _project.Clips.Count - 1));
        }
    }

    /// <summary>Moves where clip <paramref name="index"/> starts, in its own video.</summary>
    public void TrimStart(int index, TimeSpan start)
    {
        if (CanEdit && _project.TrimStart(index, start) is var trimmed && trimmed.Clips[index] != _project.Clips[index])
        {
            Commit(trimmed, index);
        }
    }

    /// <summary>Moves where clip <paramref name="index"/> ends, in its own video.</summary>
    public void TrimEnd(int index, TimeSpan end)
    {
        if (CanEdit && _project.TrimEnd(index, end) is var trimmed && trimmed.Clips[index] != _project.Clips[index])
        {
            Commit(trimmed, index);
        }
    }

    public void Undo()
    {
        if (CanUndo)
        {
            _redo.Add(_project);
            Show(Pop(_undo));
        }
    }

    public void Redo()
    {
        if (CanRedo)
        {
            Remember(_project, clearRedo: false);
            Show(Pop(_redo));
        }
    }

    /// <summary>What's wrong with saving to <paramref name="output"/>, if anything.</summary>
    public string? ProblemSaving(string output)
    {
        if (!HasClips)
        {
            return "Add a video first.";
        }

        var full = Path.GetFullPath(output);
        if (_project.Sources.Any(source => string.Equals(Path.GetFullPath(source), full, StringComparison.OrdinalIgnoreCase)))
        {
            return "That's one of the videos in this edit. Choose another name, so it isn't written over.";
        }

        var missing = _project.Sources.Where(source => !_exists(source)).Select(Path.GetFileName).ToList();
        return missing.Count > 0
            ? $"{string.Join(", ", missing)} can't be found. Put {(missing.Count == 1 ? "it" : "them")} back, or take {(missing.Count == 1 ? "its" : "their")} clips out."
            : null;
    }

    /// <summary>Makes one new video of the clips, in order. It's written beside its final name first, so a stop leaves nothing half made.</summary>
    public async Task SaveAsync(string output)
    {
        if (IsSaving)
        {
            return;
        }

        ClearMessage();
        if (ProblemSaving(output) is { } problem)
        {
            Fail(problem);
            Refresh();
            return;
        }

        using var saving = new CancellationTokenSource();
        _saving = saving;
        SavePercent = 0;
        SavedPath = null;
        Refresh();

        var project = _project;
        var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, Path.GetFileNameWithoutExtension(output) + ".part.mp4");
        try
        {
            var plan = await Task.Run(() => EncodePlan.ForEdit(_readMetadata(project.Clips[0].SourcePath), project.Length));
            var progress = new Progress<double>(percent =>
            {
                SavePercent = percent;
                Refresh();
            });

            var encoder = _encoder();
            await Task.Run(() => encoder.RenderAsync(project, temporary, plan, progress, saving.Token));
            await Task.Run(() => File.Move(temporary, output, overwrite: true));
            SavedPath = output;
            Message = $"Saved as {Path.GetFileName(output)}.";
        }
        catch (OperationCanceledException)
        {
            Message = "Stopped. Nothing was saved.";
        }
        catch (EncodeException exception)
        {
            Fail(exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Fail(exception.Message);
        }
        finally
        {
            TryDelete(temporary);
            _saving = null;
            Refresh();
        }
    }

    public void CancelSave() => _saving?.Cancel();

    public void UseSaved()
    {
        if (SavedPath is { } path)
        {
            UseVideo?.Invoke(path);
        }
    }

    private static EditProject Pop(List<EditProject> list)
    {
        var last = list[^1];
        list.RemoveAt(list.Count - 1);
        return last;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void Remember(EditProject project, bool clearRedo = true)
    {
        _undo.Add(project);
        if (_undo.Count > MostUndos)
        {
            _undo.RemoveAt(0);
        }

        if (clearRedo)
        {
            _redo.Clear();
        }
    }

    private void Commit(EditProject project, int select)
    {
        Remember(_project);
        Show(project, select);
    }

    private void Show(EditProject project, int? select = null)
    {
        _project = project;
        _selected = select is { } index && index >= 0 && index < project.Clips.Count ? index : _selected < project.Clips.Count ? _selected : -1;
        _position = _position > project.Length ? project.Length : _position;

        try
        {
            _store.Save(project);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Fail($"The edit couldn't be kept for next time: {exception.Message}");
        }

        Refresh();
    }

    private void Fail(string message)
    {
        Message = message;
        MessageIsError = true;
    }

    private void ClearMessage()
    {
        Message = null;
        MessageIsError = false;
    }

    private void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
}
