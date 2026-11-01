using System.Text.Json.Serialization;

namespace VideoDispatch.Core.Model;

/// <summary>
/// One video going to one platform. A video sent to both platforms is two jobs
/// sharing a <see cref="BatchId"/>, so one platform failing can't block the other.
/// </summary>
public sealed record UploadJob
{
    public required Guid Id { get; init; }
    public required Guid BatchId { get; init; }
    public required string SourcePath { get; init; }

    /// <summary>The source file's size when queued. A file changed since then fails rather than uploading a corrupt mix.</summary>
    public required long SourceLength { get; init; }

    /// <summary>The source file's last write time when queued.</summary>
    public required DateTimeOffset SourceLastWriteUtc { get; init; }

    public required PostDetails Details { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>The output of the later render step. Null means the source is published as it is.</summary>
    public string? RenderedPath { get; init; }

    public JobState State { get; init; } = JobState.Queued;
    public int Attempts { get; init; }
    public DateTimeOffset? NextAttemptAt { get; init; }
    public string? LastError { get; init; }

    /// <summary>Something the user should know about a video that did arrive.</summary>
    public string? Warning { get; init; }

    public long BytesSent { get; init; }
    public long TotalBytes { get; init; }

    /// <summary>The TikTok publish ID or YouTube video ID once the platform has assigned one.</summary>
    public string? RemoteId { get; init; }

    /// <summary>When the platform got the whole file.</summary>
    public DateTimeOffset? UploadedAt { get; init; }

    /// <summary>The video is in the playlist it was meant for. This step never uploads the video again.</summary>
    public bool PlaylistAdded { get; init; }

    public int PlaylistAttempts { get; init; }

    /// <summary>Why the video couldn't be added to its playlist, after giving up. The video itself is fine.</summary>
    public string? PlaylistError { get; init; }

    [JsonIgnore]
    public Platform Platform => Details.Platform;

    [JsonIgnore]
    public string PublishPath => RenderedPath ?? SourcePath;

    /// <summary>The video still has to go in a playlist.</summary>
    [JsonIgnore]
    public bool NeedsPlaylistStep =>
        Details is YouTubePostDetails { PlaylistId: not null } && RemoteId is not null && !PlaylistAdded && PlaylistError is null;
}
