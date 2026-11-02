using System.ComponentModel;
using System.Globalization;
using VideoDispatch.Core.Editing;
using VideoDispatch.Core.Media;
using VideoDispatch.Core.Reencoding;

namespace VideoDispatch.App;

/// <summary>What can be chosen on the timeline.</summary>
public enum EditItem
{
    None,
    Clip,
    Sound,
    Text,
}

/// <summary>
/// What the Edit screen shows and does: clips in a row, cut, trimmed, faded, zoomed and put in order, with sounds and
/// words laid over them, saved as one new video. Every change can be undone. The edit is kept between runs, and the
/// files in it are only ever read.
/// </summary>
public sealed class EditPanel : INotifyPropertyChanged
{
    private const int MostUndos = 100;
    private static readonly TimeSpan NewTextLength = TimeSpan.FromSeconds(3);

    private readonly EditStore _store;
    private readonly Func<string, VideoMetadata?> _readMetadata;
    private readonly Func<string, CancellationToken, Task<TimeSpan?>> _readLength;
    private readonly Func<IVideoEncoder> _encoder;
    private readonly Func<string, bool> _exists;
    private readonly Func<EditText, int, int, byte[]> _drawText;
    private readonly Dictionary<string, int> _turns = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<EditProject> _undo = [];
    private readonly List<EditProject> _redo = [];

    private EditProject _project = EditProject.Empty;
    private EditItem _chosen;
    private int _chosenIndex = -1;
    private TimeSpan _position;
    private CancellationTokenSource? _saving;
    private string? _lastChange;
    private (string? Path, int Width, int Height) _frame;

    /// <param name="store">Keeps the edit between runs.</param>
    /// <param name="readMetadata">Reads what's in a video.</param>
    /// <param name="readLength">How long a video or sound is, for one whose metadata can't be read.</param>
    /// <param name="encoder">The engine to save with now. It's asked each time, since the user can change it.</param>
    /// <param name="exists">Whether a file is still where it was.</param>
    /// <param name="drawText">Draws words as a PNG of a frame's size. <see cref="TextPicture.Png"/> unless a test says otherwise.</param>
    public EditPanel(EditStore store, Func<string, VideoMetadata?> readMetadata, Func<string, CancellationToken, Task<TimeSpan?>> readLength, Func<IVideoEncoder> encoder, Func<string, bool> exists, Func<EditText, int, int, byte[]>? drawText = null)
    {
        _store = store;
        _readMetadata = readMetadata;
        _readLength = readLength;
        _encoder = encoder;
        _exists = exists;
        _drawText = drawText ?? TextPicture.Png;
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
        get => _chosen == EditItem.Clip ? _chosenIndex : -1;
        set => Choose(EditItem.Clip, value);
    }

    /// <summary>The sound chosen on the timeline, or -1.</summary>
    public int SelectedSound
    {
        get => _chosen == EditItem.Sound ? _chosenIndex : -1;
        set => Choose(EditItem.Sound, value);
    }

    /// <summary>The words chosen on the timeline, or -1.</summary>
    public int SelectedText
    {
        get => _chosen == EditItem.Text ? _chosenIndex : -1;
        set => Choose(EditItem.Text, value);
    }

    public EditItem Chosen => _chosen;

    public bool HasSelection => _chosen != EditItem.None;

    public bool IsNothingChosen => !HasSelection;

    public bool HasClipChosen => _chosen == EditItem.Clip;

    public bool HasSoundChosen => _chosen == EditItem.Sound;

    public bool HasTextChosen => _chosen == EditItem.Text;

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

    public string? SelectionText => _chosen switch
    {
        EditItem.Clip when Clip is { } clip => $"{clip.Name}: {Format(clip.Start)} to {Format(clip.End)} of its {Format(clip.SourceLength)}",
        EditItem.Sound when Sound is { } sound => $"{sound.Name}: {Format(sound.Start)} to {Format(sound.End)} of it, from {Format(sound.At)} in the edit",
        EditItem.Text when Text is { } text => $"Words from {Format(text.At)} to {Format(text.Finish)}",
        _ => HasClips ? "Click a clip, sound or words to choose it. Drag to move it, or drag its edges to trim it." : null,
    };

    /// <summary>The name of what's chosen, over its settings.</summary>
    public string ChosenName => _chosen switch
    {
        EditItem.Clip when Clip is { } clip => clip.Name,
        EditItem.Sound when Sound is { } sound => sound.Name,
        EditItem.Text => "Words",
        _ => "Nothing chosen",
    };

    public bool CanSplit => !IsSaving && _project.SplitAt(_position) is not null;

    public bool CanDelete => !IsSaving && HasSelection;

    public bool CanUndo => !IsSaving && _undo.Count > 0;

    public bool CanRedo => !IsSaving && _redo.Count > 0;

    public bool CanEdit => !IsSaving;

    public bool CanAddText => !IsSaving && HasClips;

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

    /// <summary>The size the edit is saved at, which is the first clip's, as viewers see it.</summary>
    public (int Width, int Height) FrameSize
    {
        get
        {
            var first = HasClips ? _project.Clips[0].SourcePath : null;
            if (_frame.Path != first || _frame.Width == 0)
            {
                var plan = EncodePlan.ForEdit(first is null ? null : _readMetadata(first), Length);
                _frame = (first, plan.Width ?? 1920, plan.Height ?? 1080);
            }

            return (_frame.Width, _frame.Height);
        }
    }

    // The chosen clip's settings, as the sliders show them.

    public double ClipVolume
    {
        get => (Clip?.Volume ?? 1) * 100;
        set => ChangeClip("volume", clip => clip with { Volume = value / 100 });
    }

    public string ClipVolumeText => Percent(ClipVolume);

    public double ClipFadeIn
    {
        get => Clip?.FadeIn.TotalSeconds ?? 0;
        set => ChangeClip("fade-in", clip => clip with { FadeIn = Seconds(value) });
    }

    public string ClipFadeInText => SecondsText(ClipFadeIn);

    public double ClipFadeOut
    {
        get => Clip?.FadeOut.TotalSeconds ?? 0;
        set => ChangeClip("fade-out", clip => clip with { FadeOut = Seconds(value) });
    }

    public string ClipFadeOutText => SecondsText(ClipFadeOut);

    public double ClipCrossfade
    {
        get => Clip?.Crossfade.TotalSeconds ?? 0;
        set => ChangeClip("crossfade", clip => clip with { Crossfade = Seconds(value) });
    }

    public string ClipCrossfadeText => SecondsText(ClipCrossfade);

    /// <summary>Only a clip with one before it can fade into it.</summary>
    public bool CanCrossfade => CanEdit && SelectedIndex > 0;

    public double ClipZoom
    {
        get => (Clip?.Zoom ?? 1) * 100;
        set => ChangeClip("zoom", clip => clip with { Zoom = value / 100 });
    }

    public string ClipZoomText => Percent(ClipZoom);

    public bool ClipIsZoomed => Clip?.IsZoomed == true;

    // The chosen sound's settings.

    public double SoundVolume
    {
        get => (Sound?.Volume ?? 1) * 100;
        set => ChangeSound("volume", sound => sound with { Volume = value / 100 });
    }

    public string SoundVolumeText => Percent(SoundVolume);

    public double SoundFadeIn
    {
        get => Sound?.FadeIn.TotalSeconds ?? 0;
        set => ChangeSound("fade-in", sound => sound with { FadeIn = Seconds(value) });
    }

    public string SoundFadeInText => SecondsText(SoundFadeIn);

    public double SoundFadeOut
    {
        get => Sound?.FadeOut.TotalSeconds ?? 0;
        set => ChangeSound("fade-out", sound => sound with { FadeOut = Seconds(value) });
    }

    public string SoundFadeOutText => SecondsText(SoundFadeOut);

    // The chosen words' settings.

    public string TextContent
    {
        get => Text?.Text ?? "";
        set => ChangeText("words", text => text with { Text = value });
    }

    public int TextPlaceIndex
    {
        get => (int)(Text?.Place ?? TextPlace.Bottom);
        set => ChangeText("place", text => Enum.IsDefined((TextPlace)value) ? text with { Place = (TextPlace)value } : text);
    }

    public int TextSizeIndex
    {
        get => (int)(Text?.Size ?? TextSize.Medium);
        set => ChangeText("size", text => Enum.IsDefined((TextSize)value) ? text with { Size = (TextSize)value } : text);
    }

    public int TextColourIndex
    {
        get => (int)(Text?.Colour ?? TextColour.White);
        set => ChangeText("colour", text => Enum.IsDefined((TextColour)value) ? text with { Colour = (TextColour)value } : text);
    }

    public bool TextHasBox
    {
        get => Text?.Box ?? false;
        set => ChangeText("box", text => text with { Box = value });
    }

    public double TextSeconds
    {
        get => Text?.Length.TotalSeconds ?? NewTextLength.TotalSeconds;
        set => ChangeText("length", text => text with { Length = Seconds(value) });
    }

    public string TextSecondsText => SecondsText(TextSeconds);

    private EditClip? Clip => SelectedIndex >= 0 ? _project.Clips[SelectedIndex] : null;

    private EditSound? Sound => SelectedSound >= 0 ? _project.Sounds[SelectedSound] : null;

    private EditText? Text => SelectedText >= 0 ? _project.Texts[SelectedText] : null;

    /// <summary>Like 1:05.3, or 1:02:05.3 past an hour.</summary>
    public static string Format(TimeSpan time) => time.TotalHours >= 1
        ? time.ToString(@"h\:mm\:ss\.f", CultureInfo.InvariantCulture)
        : time.ToString(@"m\:ss\.f", CultureInfo.InvariantCulture);

    /// <summary>Whether a file is better added as a sound than as a clip, from its name.</summary>
    public static bool IsSoundFile(string path) => Path.GetExtension(path).ToLowerInvariant() is ".mp3" or ".wav" or ".m4a" or ".aac" or ".wma" or ".flac";

    /// <summary>How far a video is to be turned clockwise to be shown upright, as phones mark it: 0, 90, 180 or 270.</summary>
    public int RotationOf(string path)
    {
        if (!_turns.TryGetValue(path, out var degrees))
        {
            _turns[path] = degrees = _readMetadata(path)?.RotationDegrees ?? 0;
        }

        return degrees;
    }

    /// <summary>Picks up the edit from last time, and says if any of its files have gone.</summary>
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
            Fail($"{string.Join(", ", missing.Select(Path.GetFileName))} can't be found. Put {(missing.Count == 1 ? "it" : "them")} back, or take {(missing.Count == 1 ? "it" : "them")} out, before saving.");
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

        var read = await ReadLengthsAsync(paths);
        ClearMessage();
        var index = Math.Clamp(at ?? _project.Clips.Count, 0, _project.Clips.Count);
        var project = _project;
        var added = 0;
        foreach (var (path, length) in read)
        {
            if (length is { } known)
            {
                project = project.Insert(EditClip.Whole(path, known), index + added);
                added++;
            }
        }

        FailUnread(read, "a video", "videos");
        if (added > 0)
        {
            Commit(project, EditItem.Clip, index);
            Position = _project.StartOf(index);
        }
    }

    /// <summary>Lays sounds over the edit from <paramref name="at"/>, or the playhead, one after another, as one change.</summary>
    public async Task AddSoundsAsync(IReadOnlyList<string> paths, TimeSpan? at = null)
    {
        if (IsSaving || paths.Count == 0)
        {
            return;
        }

        var read = await ReadLengthsAsync(paths);
        ClearMessage();
        var start = at ?? (_position >= Length ? TimeSpan.Zero : _position);
        var project = _project;
        foreach (var (path, length) in read)
        {
            if (length is { } known)
            {
                project = project.AddSound(new EditSound(path, known, TimeSpan.Zero, known, start));
                start += known;
            }
        }

        FailUnread(read, "a sound", "sounds");
        if (project.Sounds.Count > _project.Sounds.Count)
        {
            Commit(project, EditItem.Sound, project.Sounds.Count - 1);
        }
    }

    /// <summary>Puts new words over the picture at the playhead, and chooses them, ready to be typed over.</summary>
    public void AddText()
    {
        if (!CanAddText)
        {
            return;
        }

        var at = _position + NewTextLength > Length ? Max(TimeSpan.Zero, Length - NewTextLength) : _position;
        var project = _project.AddText(new EditText("Your words here", at, NewTextLength));
        Commit(project, EditItem.Text, project.Texts.Count - 1);
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
            Commit(split, EditItem.Clip, moment.Index + 1);
        }
    }

    /// <summary>Takes out whatever is chosen: a clip, a sound or words.</summary>
    public void DeleteSelected()
    {
        if (!CanDelete)
        {
            return;
        }

        var index = _chosenIndex;
        switch (_chosen)
        {
            case EditItem.Clip:
                Commit(_project.Remove(index), EditItem.Clip, Math.Min(index, _project.Clips.Count - 2));
                break;
            case EditItem.Sound:
                Commit(_project.RemoveSound(index), EditItem.None, -1);
                break;
            case EditItem.Text:
                Commit(_project.RemoveText(index), EditItem.None, -1);
                break;
        }
    }

    /// <summary>Takes clip <paramref name="from"/> out and puts it back so it ends up at <paramref name="to"/>.</summary>
    public void Move(int from, int to)
    {
        if (CanEdit && from != to && from >= 0 && from < _project.Clips.Count)
        {
            Commit(_project.Move(from, to), EditItem.Clip, Math.Clamp(to, 0, _project.Clips.Count - 1));
        }
    }

    /// <summary>Moves where clip <paramref name="index"/> starts, in its own video.</summary>
    public void TrimStart(int index, TimeSpan start) => CommitIfChanged(_project.TrimStart(index, start), EditItem.Clip, index);

    /// <summary>Moves where clip <paramref name="index"/> ends, in its own video.</summary>
    public void TrimEnd(int index, TimeSpan end) => CommitIfChanged(_project.TrimEnd(index, end), EditItem.Clip, index);

    public void MoveSound(int index, TimeSpan at) => CommitIfChanged(_project.MoveSound(index, at), EditItem.Sound, index);

    public void TrimSoundStart(int index, TimeSpan start) => CommitIfChanged(_project.TrimSoundStart(index, start), EditItem.Sound, index);

    public void TrimSoundEnd(int index, TimeSpan end) => CommitIfChanged(_project.TrimSoundEnd(index, end), EditItem.Sound, index);

    public void MoveText(int index, TimeSpan at) => CommitIfChanged(_project.ChangeText(index, _project.Texts[index] with { At = at }), EditItem.Text, index);

    public void TrimTextStart(int index, TimeSpan at) => CommitIfChanged(_project.TrimTextStart(index, at), EditItem.Text, index);

    public void TrimTextEnd(int index, TimeSpan finish) => CommitIfChanged(_project.TrimTextEnd(index, finish), EditItem.Text, index);

    /// <summary>Moves what's in view of the chosen, zoomed clip, as fractions across and down the frame.</summary>
    public void SetZoomCentre(double x, double y) => ChangeClip("zoom-centre", clip => clip with { ZoomX = x, ZoomY = y });

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
            return "That's one of the files in this edit. Choose another name, so it isn't written over.";
        }

        var missing = _project.Sources.Where(source => !_exists(source)).Select(Path.GetFileName).ToList();
        return missing.Count > 0
            ? $"{string.Join(", ", missing)} can't be found. Put {(missing.Count == 1 ? "it" : "them")} back, or take {(missing.Count == 1 ? "it" : "them")} out."
            : null;
    }

    /// <summary>
    /// Makes one new video of the edit. It's written beside its final name first, so a stop leaves nothing half made,
    /// and what it needs while it works goes in a folder beside that, removed afterwards.
    /// </summary>
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
        var folder = Path.GetDirectoryName(Path.GetFullPath(output))!;
        var name = Path.GetFileNameWithoutExtension(output);
        var temporary = Path.Combine(folder, name + ".part.mp4");
        var workFolder = Path.Combine(folder, name + ".part-files");
        try
        {
            var plan = await Task.Run(() => EncodePlan.ForEdit(_readMetadata(project.Clips[0].SourcePath), project.Length));
            var (width, height) = (plan.Width ?? 1920, plan.Height ?? 1080);

            // Words are drawn here, on the window's thread, as the preview draws them.
            Directory.CreateDirectory(workFolder);
            var pictures = new List<string>();
            for (var index = 0; index < project.Texts.Count; index++)
            {
                var path = Path.Combine(workFolder, $"words-{index}.png");
                await File.WriteAllBytesAsync(path, _drawText(project.Texts[index], width, height), saving.Token);
                pictures.Add(path);
            }

            var progress = new Progress<double>(percent =>
            {
                SavePercent = percent;
                Refresh();
            });

            var encoder = _encoder();
            var work = new EditWork(workFolder, pictures);
            await Task.Run(() => encoder.RenderAsync(project, work, temporary, plan, progress, saving.Token));
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
            TryDeleteFolder(workFolder);
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

    private static TimeSpan Seconds(double value) => TimeSpan.FromSeconds(Math.Round(Math.Max(0, value), 2));

    private static string SecondsText(double seconds) => seconds <= 0 ? "Off" : seconds.ToString("0.0 's'", CultureInfo.InvariantCulture);

    private static string Percent(double value) => Math.Round(value).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

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

    private static void TryDeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task<List<(string Path, TimeSpan? Length)>> ReadLengthsAsync(IReadOnlyList<string> paths)
    {
        var read = new List<(string Path, TimeSpan? Length)>();
        foreach (var path in paths)
        {
            var length = await Task.Run(() => _readMetadata(path)?.Duration);
            length = length is { TotalMilliseconds: > 0 } known ? known : await _readLength(path, CancellationToken.None);
            read.Add((path, length is { } usable && usable >= EditProject.ShortestClip ? usable : null));
        }

        return read;
    }

    private void FailUnread(List<(string Path, TimeSpan? Length)> read, string one, string many)
    {
        var unread = read.Where(file => file.Length is null).Select(file => Path.GetFileName(file.Path)).ToList();
        if (unread.Count > 0)
        {
            Fail($"{string.Join(", ", unread)} couldn't be read as {(unread.Count == 1 ? one : many)}. Windows may not know {(unread.Count == 1 ? "its" : "their")} format.");
        }
    }

    private void Choose(EditItem item, int index)
    {
        var count = item switch
        {
            EditItem.Clip => _project.Clips.Count,
            EditItem.Sound => _project.Sounds.Count,
            EditItem.Text => _project.Texts.Count,
            _ => 0,
        };

        (_chosen, _chosenIndex) = index >= 0 && index < count ? (item, index) : (EditItem.None, -1);
        _lastChange = null;
        Refresh();
    }

    private void ChangeClip(string setting, Func<EditClip, EditClip> change)
    {
        if (CanEdit && Clip is { } clip)
        {
            Change($"clip {_chosenIndex} {setting}", _project.ChangeClip(_chosenIndex, change(clip)), EditItem.Clip);
        }
    }

    private void ChangeSound(string setting, Func<EditSound, EditSound> change)
    {
        if (CanEdit && Sound is { } sound)
        {
            Change($"sound {_chosenIndex} {setting}", _project.ChangeSound(_chosenIndex, change(sound)), EditItem.Sound);
        }
    }

    private void ChangeText(string setting, Func<EditText, EditText> change)
    {
        if (CanEdit && Text is { } text)
        {
            Change($"text {_chosenIndex} {setting}", _project.ChangeText(_chosenIndex, change(text)), EditItem.Text);
        }
    }

    /// <summary>
    /// A change to one setting. Dragging a slider or typing makes many in a row, and those are one step to undo, so
    /// going back doesn't take a step per pixel or letter.
    /// </summary>
    private void Change(string setting, EditProject project, EditItem item)
    {
        if (project == _project || ItemOf(project, item) == ItemOf(_project, item))
        {
            return;
        }

        var index = _chosenIndex;
        if (_lastChange == setting && _undo.Count > 0)
        {
            Show(project, item, index);
        }
        else
        {
            Commit(project, item, index);
        }

        _lastChange = setting;
    }

    private object? ItemOf(EditProject project, EditItem item) => item switch
    {
        EditItem.Clip => project.Clips[_chosenIndex],
        EditItem.Sound => project.Sounds[_chosenIndex],
        EditItem.Text => project.Texts[_chosenIndex],
        _ => null,
    };

    private void CommitIfChanged(EditProject project, EditItem item, int index)
    {
        if (!CanEdit || ReferenceEquals(project, _project))
        {
            return;
        }

        var changed = item switch
        {
            EditItem.Clip => project.Clips[index] != _project.Clips[index],
            EditItem.Sound => project.Sounds[index] != _project.Sounds[index],
            EditItem.Text => project.Texts[index] != _project.Texts[index],
            _ => false,
        };

        if (changed)
        {
            Commit(project, item, index);
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

    private void Commit(EditProject project, EditItem item, int index)
    {
        Remember(_project);
        _lastChange = null;
        Show(project, item, index);
    }

    private void Show(EditProject project, EditItem? item = null, int index = -1)
    {
        _project = project;
        if (item is { } chosen)
        {
            Choose(chosen, index);
        }
        else
        {
            // After undo or redo, keep what was chosen if it's still there.
            Choose(_chosen, _chosenIndex);
        }

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
