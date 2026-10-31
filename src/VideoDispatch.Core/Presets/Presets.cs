using VideoDispatch.Core.Model;

namespace VideoDispatch.Core.Presets;

/// <summary>
/// Holds text only. TikTok's rules say privacy, Comment, Duet, Stitch and the commercial
/// content toggle must be set by the user for each post, so this type has no such fields.
/// </summary>
public sealed record TikTokPreset
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string CaptionTemplate { get; init; } = "{title}";
    public IReadOnlyList<string> Hashtags { get; init; } = [];
}

public sealed record YouTubePreset
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string TitleTemplate { get; init; } = "{title}";
    public string DescriptionTemplate { get; init; } = "";
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? CategoryId { get; init; }
    public YouTubePrivacy? Privacy { get; init; }
    public bool? MadeForKids { get; init; }
    public string? PlaylistId { get; init; }
    public YouTubeSchedule? Schedule { get; init; }
}

/// <summary>A publish time relative to the day the video is queued, in the user's time zone.</summary>
public sealed record YouTubeSchedule(int DaysAhead, TimeOnly LocalTime);
