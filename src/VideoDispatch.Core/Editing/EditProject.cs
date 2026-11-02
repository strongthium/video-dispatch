namespace VideoDispatch.Core.Editing;

/// <summary>The part of a video from <paramref name="Start"/> to <paramref name="End"/>, in the video's own time.</summary>
/// <param name="SourcePath">The video. It's only ever read.</param>
/// <param name="SourceLength">How long the whole video is, so a trim can't run past its end.</param>
public sealed record EditClip(string SourcePath, TimeSpan SourceLength, TimeSpan Start, TimeSpan End)
{
    public TimeSpan Length => End - Start;

    public string Name => Path.GetFileName(SourcePath);

    public static EditClip Whole(string path, TimeSpan length) => new(path, length, TimeSpan.Zero, length);
}

/// <summary>Where a moment of the edit comes from.</summary>
/// <param name="Index">Which clip.</param>
/// <param name="ClipStart">Where that clip starts in the edit.</param>
/// <param name="SourceTime">The same moment in the clip's own video.</param>
public sealed record EditMoment(int Index, TimeSpan ClipStart, TimeSpan SourceTime);

/// <summary>
/// Clips played one after another. A project is never changed in place: each edit makes a new one, so going back is
/// keeping the old. Times are in the edit unless they say they're in a clip's own video.
/// </summary>
public sealed record EditProject(IReadOnlyList<EditClip> Clips)
{
    /// <summary>Nothing is cut shorter than this, so every clip can still be seen and grabbed.</summary>
    public static readonly TimeSpan ShortestClip = TimeSpan.FromMilliseconds(100);

    public static EditProject Empty { get; } = new([]);

    public TimeSpan Length => Clips.Aggregate(TimeSpan.Zero, (sum, clip) => sum + clip.Length);

    public IReadOnlyList<string> Sources => Clips.Select(clip => clip.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public TimeSpan StartOf(int index) => Clips.Take(index).Aggregate(TimeSpan.Zero, (sum, clip) => sum + clip.Length);

    /// <summary>The clip playing at <paramref name="at"/>. The very end belongs to the last clip. Null when empty.</summary>
    public EditMoment? Find(TimeSpan at)
    {
        if (Clips.Count == 0)
        {
            return null;
        }

        var start = TimeSpan.Zero;
        for (var index = 0; index < Clips.Count; index++)
        {
            var clip = Clips[index];
            if (at < start + clip.Length || index == Clips.Count - 1)
            {
                var offset = at < start ? TimeSpan.Zero : at - start;
                return new EditMoment(index, start, clip.Start + (offset > clip.Length ? clip.Length : offset));
            }

            start += clip.Length;
        }

        return null;
    }

    /// <summary>Adds a clip before <paramref name="index"/>, or at the end.</summary>
    public EditProject Insert(EditClip clip, int? index = null)
    {
        var clips = Clips.ToList();
        clips.Insert(Math.Clamp(index ?? clips.Count, 0, clips.Count), clip);
        return new EditProject(clips);
    }

    public EditProject Remove(int index)
    {
        var clips = Clips.ToList();
        clips.RemoveAt(index);
        return new EditProject(clips);
    }

    /// <summary>Takes the clip at <paramref name="from"/> and puts it where it then lands at <paramref name="to"/>.</summary>
    public EditProject Move(int from, int to)
    {
        var clips = Clips.ToList();
        var clip = clips[from];
        clips.RemoveAt(from);
        clips.Insert(Math.Clamp(to, 0, clips.Count), clip);
        return new EditProject(clips);
    }

    /// <summary>Cuts the clip at <paramref name="at"/> in two. Null when that's too close to a clip's edge to leave two clips.</summary>
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
        clips[moment.Index] = clip with { End = moment.SourceTime };
        clips.Insert(moment.Index + 1, clip with { Start = moment.SourceTime });
        return new EditProject(clips);
    }

    /// <summary>Moves where a clip starts in its video, keeping it at least <see cref="ShortestClip"/> long.</summary>
    public EditProject TrimStart(int index, TimeSpan start)
    {
        var clip = Clips[index];
        var limit = clip.End - ShortestClip;
        return With(index, clip with { Start = start < TimeSpan.Zero ? TimeSpan.Zero : start > limit ? limit : start });
    }

    /// <summary>Moves where a clip ends in its video, keeping it at least <see cref="ShortestClip"/> long.</summary>
    public EditProject TrimEnd(int index, TimeSpan end)
    {
        var clip = Clips[index];
        var limit = clip.Start + ShortestClip;
        return With(index, clip with { End = end > clip.SourceLength ? clip.SourceLength : end < limit ? limit : end });
    }

    private EditProject With(int index, EditClip clip)
    {
        var clips = Clips.ToList();
        clips[index] = clip;
        return new EditProject(clips);
    }
}
