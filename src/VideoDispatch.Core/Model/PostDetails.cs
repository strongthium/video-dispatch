using System.Text.Json.Serialization;
using VideoDispatch.Core.Reencoding;

namespace VideoDispatch.Core.Model;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "platform")]
[JsonDerivedType(typeof(TikTokPostDetails), "tiktok")]
[JsonDerivedType(typeof(YouTubePostDetails), "youtube")]
public abstract record PostDetails
{
    [JsonIgnore]
    public abstract Platform Platform { get; }

    /// <summary>What to do to the video before sending it. Null sends the file as it is.</summary>
    public EncodeSettings? Encode { get; init; }
}

/// <summary>
/// What the user will post on TikTok. The inbox init request has no caption field,
/// so the caption is shown for the user to copy into TikTok when they finish the post.
/// </summary>
public sealed record TikTokPostDetails : PostDetails
{
    [JsonIgnore]
    public override Platform Platform => Platform.TikTok;

    public string Caption { get; init; } = "";
}

public sealed record YouTubePostDetails : PostDetails
{
    [JsonIgnore]
    public override Platform Platform => Platform.YouTube;

    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? CategoryId { get; init; }

    /// <summary>Must be chosen. YouTube treats a missing value as public.</summary>
    public YouTubePrivacy? Privacy { get; init; }

    /// <summary>Must be chosen. This is the channel owner's own declaration.</summary>
    public bool? MadeForKids { get; init; }

    public string? PlaylistId { get; init; }
    public DateTimeOffset? PublishAt { get; init; }
}

public enum YouTubePrivacy
{
    Private,
    Unlisted,
    Public,
}
