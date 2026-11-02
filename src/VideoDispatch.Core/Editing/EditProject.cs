namespace VideoDispatch.Core.Editing;

/// <summary>The part of a video from <paramref name="Start"/> to <paramref name="End"/>, in the video's own time.</summary>
/// <param name="SourcePath">The video. It's only ever read.</param>
/// <param name="SourceLength">How long the whole video is, so a trim can't run past its end.</param>
public sealed record EditClip(string SourcePath, TimeSpan SourceLength, TimeSpan Start, TimeSpan End)
{
    public const double MostZoom = 4;

    public TimeSpan Length => End - Start;

    public string Name => Path.GetFileName(SourcePath);

    /// <summary>How loud its sound is, from 0 for none to 1 as it was.</summary>
    public double Volume { get; init; } = 1;

    /// <summary>How long the picture takes to come up from black, and the sound from silence, as it starts.</summary>
    public TimeSpan FadeIn { get; init; }

    /// <summary>How long the picture takes to go to black, and the sound to silence, as it ends.</summary>
    public TimeSpan FadeOut { get; init; }

    /// <summary>How long it fades in over the end of the clip before, the two playing at once. The first clip has none.</summary>
    public TimeSpan Crossfade { get; init; }

    /// <summary>How much bigger the picture is shown, from 1 as it is to <see cref="MostZoom"/>.</summary>
    public double Zoom { get; init; } = 1;

    /// <summary>Where the middle of what's seen is when zoomed, across the frame from 0 to 1.</summary>
    public double ZoomX { get; init; } = 0.5;

    /// <summary>Where the middle of what's seen is when zoomed, down the frame from 0 to 1.</summary>
    public double ZoomY { get; init; } = 0.5;

    public bool IsZoomed => Zoom > 1.001;

    public static EditClip Whole(string path, TimeSpan length) => new(path, length, TimeSpan.Zero, length);

    /// <summary>
    /// The part of the frame that's seen when zoomed, as fractions of it, kept inside the frame. The picture is shown
    /// as if this part were stretched to fill the frame.
    /// </summary>
    public (double Left, double Top, double Size) ZoomedView()
    {
        var size = 1 / Math.Clamp(Zoom, 1, MostZoom);
        return (Math.Clamp(ZoomX - (size / 2), 0, 1 - size), Math.Clamp(ZoomY - (size / 2), 0, 1 - size), size);
    }
}

/// <summary>A sound laid over the edit, such as music, from <paramref name="Start"/> to <paramref name="End"/> of its own time.</summary>
/// <param name="SourcePath">The sound, or a video whose sound is used. It's only ever read.</param>
/// <param name="SourceLength">How long the whole of it is.</param>
/// <param name="At">Where it starts in the edit.</param>
public sealed record EditSound(string SourcePath, TimeSpan SourceLength, TimeSpan Start, TimeSpan End, TimeSpan At)
{
    public TimeSpan Length => End - Start;

    public TimeSpan Finish => At + Length;

    public string Name => Path.GetFileName(SourcePath);

    /// <summary>From 0 for none to 1 as it was.</summary>
    public double Volume { get; init; } = 1;

    public TimeSpan FadeIn { get; init; }

    public TimeSpan FadeOut { get; init; }
}

public enum TextPlace
{
    Top,
    Middle,
    Bottom,
}

public enum TextSize
{
    Small,
    Medium,
    Large,
}

public enum TextColour
{
    White,
    Yellow,
    Black,
}

/// <summary>Words shown over the picture from <paramref name="At"/> for <paramref name="Length"/>.</summary>
public sealed record EditText(string Text, TimeSpan At, TimeSpan Length)
{
    public TimeSpan Finish => At + Length;

    public TextPlace Place { get; init; } = TextPlace.Bottom;

    public TextSize Size { get; init; } = TextSize.Medium;

    public TextColour Colour { get; init; } = TextColour.White;

    /// <summary>A dark box behind light words, or a light one behind dark, so they can be read over anything.</summary>
    public bool Box { get; init; }
}

/// <summary>Where a moment of the edit comes from.</summary>
/// <param name="Index">Which clip.</param>
/// <param name="ClipStart">Where that clip starts in the edit.</param>
/// <param name="SourceTime">The same moment in the clip's own video.</param>
public sealed record EditMoment(int Index, TimeSpan ClipStart, TimeSpan SourceTime);

/// <summary>A clip seen at a moment, and how much of it shows: 1 is all of it, 0 none.</summary>
public sealed record EditLook(int Index, TimeSpan SourceTime, double Opacity);

/// <summary>
/// What's seen at a moment: one clip, and while one clip fades into the next, that next one over it. Then all of it is
/// darkened by <paramref name="Black"/>, from 0 for not at all to 1 for black.
/// </summary>
public sealed record EditView(EditLook Under, EditLook? Over, double Black);

/// <summary>
/// Clips played one after another, with sounds and words laid over them. A project is never changed in place: each edit
/// makes a new one, so going back is keeping the old. Times are in the edit unless they say they're in a clip's own video.
/// </summary>
public sealed record EditProject(IReadOnlyList<EditClip> Clips)
{
    /// <summary>Nothing is cut shorter than this, so every clip can still be seen and grabbed.</summary>
    public static readonly TimeSpan ShortestClip = TimeSpan.FromMilliseconds(100);

    public static EditProject Empty { get; } = new([]);

    public IReadOnlyList<EditSound> Sounds { get; init; } = [];

    public IReadOnlyList<EditText> Texts { get; init; } = [];

    public TimeSpan Length => Clips.Count == 0 ? TimeSpan.Zero : StartOf(Clips.Count - 1) + Clips[^1].Length;

    /// <summary>Every file the edit reads, each once.</summary>
    public IReadOnlyList<string> Sources => Clips.Select(clip => clip.SourcePath)
        .Concat(Sounds.Select(sound => sound.SourcePath))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>Where clip <paramref name="index"/> starts: after the clips before it, less the time it fades in over the one before.</summary>
    public TimeSpan StartOf(int index)
    {
        var start = TimeSpan.Zero;
        for (var before = 0; before < index; before++)
        {
            start += Clips[before].Length;
        }

        for (var each = 1; each <= index && each < Clips.Count; each++)
        {
            start -= OverlapBefore(each);
        }

        return start;
    }

    /// <summary>How long clip <paramref name="index"/> really fades in over the one before: never more than half of either.</summary>
    public TimeSpan OverlapBefore(int index)
    {
        if (index <= 0 || index >= Clips.Count)
        {
            return TimeSpan.Zero;
        }

        var clip = Clips[index];
        return Clamp(clip.Crossfade, TimeSpan.Zero, Min(Clips[index - 1].Length, clip.Length) / 2);
    }

    public TimeSpan OverlapAfter(int index) => OverlapBefore(index + 1);

    /// <summary>How long clip <paramref name="index"/> really comes up from black: never more than half of it.</summary>
    public TimeSpan FadeInOf(int index) => Clamp(Clips[index].FadeIn, TimeSpan.Zero, Clips[index].Length / 2);

    public TimeSpan FadeOutOf(int index) => Clamp(Clips[index].FadeOut, TimeSpan.Zero, Clips[index].Length / 2);

    /// <summary>
    /// The clip playing at <paramref name="at"/>. Where one clip fades into the next, that's the next. The very end
    /// belongs to the last clip. Null when empty.
    /// </summary>
    public EditMoment? Find(TimeSpan at)
    {
        if (Clips.Count == 0)
        {
            return null;
        }

        var index = 0;
        var start = TimeSpan.Zero;
        var next = TimeSpan.Zero;
        for (var each = 0; each < Clips.Count; each++)
        {
            if (each > 0 && at < next)
            {
                break;
            }

            index = each;
            start = next;
            next = start + Clips[each].Length - OverlapAfter(each);
        }

        var clip = Clips[index];
        var offset = Clamp(at - start, TimeSpan.Zero, clip.Length);
        return new EditMoment(index, start, clip.Start + offset);
    }

    /// <summary>What's seen at <paramref name="at"/>. Null when empty.</summary>
    public EditView? ViewAt(TimeSpan at)
    {
        if (Find(at) is not { } moment)
        {
            return null;
        }

        var index = moment.Index;
        var offset = moment.SourceTime - Clips[index].Start;
        var overlap = OverlapBefore(index);
        if (overlap > TimeSpan.Zero && offset < overlap)
        {
            // The clip before is still playing under this one.
            var before = Clips[index - 1];
            var under = new EditLook(index - 1, before.End - overlap + offset, 1);
            var over = new EditLook(index, moment.SourceTime, offset / overlap);
            return new EditView(under, over, 1 - Brightness(index - 1, before.Length - overlap + offset));
        }

        return new EditView(new EditLook(index, moment.SourceTime, 1), null, 1 - Brightness(index, offset));
    }

    /// <summary>How bright clip <paramref name="index"/> is at <paramref name="offset"/> into it, from its fades: 0 is black.</summary>
    public double Brightness(int index, TimeSpan offset) =>
        Ramp(offset, FadeInOf(index)) * Ramp(Clips[index].Length - offset, FadeOutOf(index));

    /// <summary>How loud clip <paramref name="index"/>'s own sound is at <paramref name="offset"/> into it, with its volume, fades and crossfades.</summary>
    public double ClipGain(int index, TimeSpan offset) =>
        Clips[index].Volume * Brightness(index, offset) *
        Ramp(offset, OverlapBefore(index)) * Ramp(Clips[index].Length - offset, OverlapAfter(index));

    /// <summary>How loud sound <paramref name="index"/> is at <paramref name="offset"/> into it.</summary>
    public double SoundGain(int index, TimeSpan offset)
    {
        var sound = Sounds[index];
        return sound.Volume *
            Ramp(offset, Clamp(sound.FadeIn, TimeSpan.Zero, sound.Length / 2)) *
            Ramp(sound.Length - offset, Clamp(sound.FadeOut, TimeSpan.Zero, sound.Length / 2));
    }

    /// <summary>How much of sound <paramref name="index"/> is heard: none past the end of the edit, which it can't make longer.</summary>
    public TimeSpan HeardLengthOf(int index) => Clamp(Length - Sounds[index].At, TimeSpan.Zero, Sounds[index].Length);

    public IEnumerable<EditText> TextsAt(TimeSpan at) => Texts.Where(text => text.At <= at && at < text.Finish && text.At < Length);

    /// <summary>Adds a clip before <paramref name="index"/>, or at the end.</summary>
    public EditProject Insert(EditClip clip, int? index = null)
    {
        var clips = Clips.ToList();
        clips.Insert(Math.Clamp(index ?? clips.Count, 0, clips.Count), clip);
        return this with { Clips = clips };
    }

    public EditProject Remove(int index)
    {
        var clips = Clips.ToList();
        clips.RemoveAt(index);
        return this with { Clips = clips };
    }

    /// <summary>Takes the clip at <paramref name="from"/> and puts it where it then lands at <paramref name="to"/>.</summary>
    public EditProject Move(int from, int to)
    {
        var clips = Clips.ToList();
        var clip = clips[from];
        clips.RemoveAt(from);
        clips.Insert(Math.Clamp(to, 0, clips.Count), clip);
        return this with { Clips = clips };
    }

    /// <summary>
    /// Cuts the clip at <paramref name="at"/> in two, which play as the one did: the first keeps how it starts and the
    /// second how it ends. Null when that's too close to a clip's edge to leave two clips.
    /// </summary>
    public EditProject? SplitAt(TimeSpan at)
    {
        if (Find(at) is not { } moment)
        {
            return null;
        }

        var clip = Clips[moment.Index];
        if (moment.SourceTime - clip.Start < ShortestClip || clip.End - moment.SourceTime < ShortestClip)
        {
            return null;
        }

        var clips = Clips.ToList();
        clips[moment.Index] = clip with { End = moment.SourceTime, FadeOut = TimeSpan.Zero };
        clips.Insert(moment.Index + 1, clip with { Start = moment.SourceTime, FadeIn = TimeSpan.Zero, Crossfade = TimeSpan.Zero });
        return this with { Clips = clips };
    }

    /// <summary>Moves where a clip starts in its video, keeping it at least <see cref="ShortestClip"/> long.</summary>
    public EditProject TrimStart(int index, TimeSpan start)
    {
        var clip = Clips[index];
        return ChangeClip(index, clip with { Start = Clamp(start, TimeSpan.Zero, clip.End - ShortestClip) });
    }

    /// <summary>Moves where a clip ends in its video, keeping it at least <see cref="ShortestClip"/> long.</summary>
    public EditProject TrimEnd(int index, TimeSpan end)
    {
        var clip = Clips[index];
        return ChangeClip(index, clip with { End = Clamp(end, clip.Start + ShortestClip, clip.SourceLength) });
    }

    /// <summary>Puts <paramref name="clip"/> in place of clip <paramref name="index"/>, with its settings kept in range.</summary>
    public EditProject ChangeClip(int index, EditClip clip)
    {
        var clips = Clips.ToList();
        clips[index] = clip with
        {
            Volume = Math.Clamp(clip.Volume, 0, 1),
            FadeIn = Max(clip.FadeIn, TimeSpan.Zero),
            FadeOut = Max(clip.FadeOut, TimeSpan.Zero),
            Crossfade = Max(clip.Crossfade, TimeSpan.Zero),
            Zoom = Math.Clamp(clip.Zoom, 1, EditClip.MostZoom),
            ZoomX = Math.Clamp(clip.ZoomX, 0, 1),
            ZoomY = Math.Clamp(clip.ZoomY, 0, 1),
        };
        return this with { Clips = clips };
    }

    public EditProject AddSound(EditSound sound) => this with { Sounds = [.. Sounds, Tidy(sound)] };

    public EditProject RemoveSound(int index) => this with { Sounds = [.. Sounds.Where((_, each) => each != index)] };

    public EditProject ChangeSound(int index, EditSound sound) => this with { Sounds = [.. Sounds.Select((each, at) => at == index ? Tidy(sound) : each)] };

    /// <summary>Slides a sound along the edit, keeping it from starting before the edit does.</summary>
    public EditProject MoveSound(int index, TimeSpan at) => ChangeSound(index, Sounds[index] with { At = at });

    /// <summary>Moves where a sound starts in its own time. What's left of it stays where it was in the edit.</summary>
    public EditProject TrimSoundStart(int index, TimeSpan start)
    {
        var sound = Sounds[index];
        var kept = Clamp(start, Max(TimeSpan.Zero, sound.Start - sound.At), sound.End - ShortestClip);
        return ChangeSound(index, sound with { Start = kept, At = sound.At + (kept - sound.Start) });
    }

    public EditProject TrimSoundEnd(int index, TimeSpan end)
    {
        var sound = Sounds[index];
        return ChangeSound(index, sound with { End = Clamp(end, sound.Start + ShortestClip, sound.SourceLength) });
    }

    public EditProject AddText(EditText text) => this with { Texts = [.. Texts, Tidy(text)] };

    public EditProject RemoveText(int index) => this with { Texts = [.. Texts.Where((_, each) => each != index)] };

    public EditProject ChangeText(int index, EditText text) => this with { Texts = [.. Texts.Select((each, at) => at == index ? Tidy(text) : each)] };

    /// <summary>Moves where words start showing. They still stop when they did, and show for at least <see cref="ShortestClip"/>.</summary>
    public EditProject TrimTextStart(int index, TimeSpan at)
    {
        var text = Texts[index];
        var kept = Clamp(at, TimeSpan.Zero, text.Finish - ShortestClip);
        return ChangeText(index, text with { At = kept, Length = text.Finish - kept });
    }

    public EditProject TrimTextEnd(int index, TimeSpan finish)
    {
        var text = Texts[index];
        return ChangeText(index, text with { Length = Max(finish - text.At, ShortestClip) });
    }

    private static EditSound Tidy(EditSound sound) => sound with
    {
        At = Max(sound.At, TimeSpan.Zero),
        Volume = Math.Clamp(sound.Volume, 0, 1),
        FadeIn = Max(sound.FadeIn, TimeSpan.Zero),
        FadeOut = Max(sound.FadeOut, TimeSpan.Zero),
    };

    private static EditText Tidy(EditText text) => text with
    {
        At = Max(text.At, TimeSpan.Zero),
        Length = Max(text.Length, ShortestClip),
    };

    /// <summary>Rises from 0 to 1 over the first <paramref name="over"/> of <paramref name="offset"/>; 1 throughout when there's no fade.</summary>
    private static double Ramp(TimeSpan offset, TimeSpan over) =>
        over <= TimeSpan.Zero ? 1 : Math.Clamp(offset / over, 0, 1);

    private static TimeSpan Clamp(TimeSpan value, TimeSpan low, TimeSpan high) => value < low ? low : value > high ? high : value;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
