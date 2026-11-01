using VideoDispatch.Core.Model;

namespace VideoDispatch.Core.Uploads;

/// <summary>Sends one video to one platform. What differs between platforms stays behind this.</summary>
public interface IVideoUploader
{
    Platform Platform { get; }

    /// <summary>Sends the whole file. When this returns, the platform has every byte and is processing them.</summary>
    /// <param name="job">The job to upload.</param>
    /// <param name="progress">Told how many bytes of the file have been sent. It can go back down if the upload starts over.</param>
    /// <param name="cancellationToken">Cancels the upload.</param>
    /// <exception cref="UploadException">The upload failed, and its kind says what to do next.</exception>
    Task<UploadReceipt> UploadAsync(UploadJob job, IProgress<long>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks on an uploaded video until it moves past <see cref="JobState.Processing"/>, or returns
    /// <see cref="JobState.Processing"/> if it still hasn't after a long wait, so the caller can ask again later.
    /// Network problems only mean asking again. An exception means the platform's answer changes what happens next:
    /// <see cref="UploadFailureKind.Retryable"/> and <see cref="UploadFailureKind.PlatformCap"/> here mean the platform
    /// dropped the video, so it has to be uploaded again.
    /// </summary>
    Task<JobState> WaitForResultAsync(string remoteId, CancellationToken cancellationToken = default);
}

/// <param name="RemoteId">The platform's ID for the upload, used to check on it later.</param>
/// <param name="Warning">Something the user should know about a video that did arrive.</param>
public sealed record UploadReceipt(string RemoteId, string? Warning = null);

/// <param name="Blocks">True when the platform would refuse the video. False when it might be fine but can't be checked.</param>
/// <param name="Message">What to tell the user.</param>
public sealed record VideoLimitFinding(bool Blocks, string Message);

public enum UploadFailureKind
{
    /// <summary>Try again, after <see cref="UploadException.RetryAt"/> if it's set.</summary>
    Retryable,

    /// <summary>The platform's limit is reached. Trying sooner than <see cref="UploadException.RetryAt"/> is pointless.</summary>
    PlatformCap,

    /// <summary>Nothing will work until the user connects the account again.</summary>
    Reconnect,

    /// <summary>Trying again would fail the same way.</summary>
    Permanent,
}

/// <summary>Messages are shown to the user and logged, so they must never contain a token or an upload address.</summary>
public sealed class UploadException(
    Platform platform,
    UploadFailureKind kind,
    string message,
    DateTimeOffset? retryAt = null) : Exception(message)
{
    public Platform Platform { get; } = platform;
    public UploadFailureKind Kind { get; } = kind;
    public DateTimeOffset? RetryAt { get; } = retryAt;
}
