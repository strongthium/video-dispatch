using VideoDispatch.Core.Media;
using VideoDispatch.Core.Uploads;

namespace VideoDispatch.Core.YouTube;

public enum ShortsPrediction
{
    Unknown,
    Short,
    LongForm,
}

public static class YouTubeVideoLimits
{
    public const long MaxVideoSize = 256L * 1024 * 1024 * 1024;

    private static readonly TimeSpan LongestShort = TimeSpan.FromMinutes(3);

    /// <summary>YouTube also takes a generic type, for any file this list doesn't name.</summary>
    public static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        ".avi" => "video/x-msvideo",
        ".mkv" => "video/x-matroska",
        ".wmv" => "video/x-ms-wmv",
        ".mpg" or ".mpeg" => "video/mpeg",
        ".3gp" => "video/3gpp",
        _ => "application/octet-stream",
    };

    public static IReadOnlyList<VideoLimitFinding> Check(long size)
    {
        if (size <= 0)
        {
            return [new VideoLimitFinding(true, "The file is empty.")];
        }

        return size > MaxVideoSize ? [new VideoLimitFinding(true, "YouTube takes videos up to 256 GB.")] : [];
    }

    /// <summary>
    /// YouTube decides this itself and the API has no setting for it. Its help page says square or vertical videos
    /// up to three minutes become Shorts, so this is a prediction based on the size viewers see, after rotation.
    /// </summary>
    public static ShortsPrediction PredictShort(VideoMetadata? metadata)
    {
        if (metadata is null || metadata.DisplayWidth <= 0 || metadata.DisplayHeight <= 0)
        {
            return ShortsPrediction.Unknown;
        }

        if (metadata.DisplayWidth > metadata.DisplayHeight)
        {
            return ShortsPrediction.LongForm;
        }

        return metadata.Duration switch
        {
            null => ShortsPrediction.Unknown,
            { } duration when duration <= LongestShort => ShortsPrediction.Short,
            _ => ShortsPrediction.LongForm,
        };
    }
}
