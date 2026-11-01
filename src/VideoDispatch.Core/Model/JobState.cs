namespace VideoDispatch.Core.Model;

public enum JobState
{
    Queued,

    /// <summary>The render step is running. For now it does nothing, so this passes at once.</summary>
    Rendering,

    Uploading,

    /// <summary>The bytes are sent and the platform is processing them.</summary>
    Processing,

    /// <summary>The draft is in the creator's TikTok inbox, and they must finish the post in TikTok.</summary>
    AwaitingCreator,

    /// <summary>Paused until <see cref="UploadJob.NextAttemptAt"/>, after a retryable error or a platform cap.</summary>
    Waiting,

    /// <summary>Paused until the user reconnects the account.</summary>
    NeedsReconnect,

    Completed,
    Failed,
    Cancelled,
}
