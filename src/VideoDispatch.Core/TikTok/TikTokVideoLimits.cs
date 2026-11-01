using VideoDispatch.Core.Media;

namespace VideoDispatch.Core.TikTok;

/// <param name="Blocks">True when TikTok would refuse the video. False when it might be fine but can't be checked.</param>
/// <param name="Message">What to tell the user.</param>
public sealed record VideoLimitFinding(bool Blocks, string Message);

/// <summary>TikTok's limits on a video. What can't be read from the file is a warning, because TikTok may still accept it.</summary>
public static class TikTokVideoLimits
{
    private const int MinSide = 360;
    private const int MaxSide = 4096;
    private const double MinFramesPerSecond = 23;
    private const double MaxFramesPerSecond = 60;
    private static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(10);

    /// <summary>Null when TikTok doesn't take this kind of file.</summary>
    public static string? ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        _ => null,
    };

    public static IReadOnlyList<VideoLimitFinding> Check(string path, long size, VideoMetadata? metadata)
    {
        var findings = new List<VideoLimitFinding>();

        if (ContentTypeFor(path) is null)
        {
            findings.Add(new VideoLimitFinding(true, "TikTok takes MP4, MOV and WebM files."));
        }

        if (size <= 0)
        {
            findings.Add(new VideoLimitFinding(true, "The file is empty."));
        }
        else if (size > TikTokChunkPlan.MaxVideoSize)
        {
            findings.Add(new VideoLimitFinding(true, "TikTok takes videos up to 4 GB."));
        }

        if (metadata is null)
        {
            findings.Add(new VideoLimitFinding(false, "Couldn't read this video's size, length or frame rate, so TikTok's limits on them weren't checked."));
            return findings;
        }

        if (metadata.DisplayWidth is < MinSide or > MaxSide || metadata.DisplayHeight is < MinSide or > MaxSide)
        {
            findings.Add(new VideoLimitFinding(true, $"TikTok takes videos from {MinSide} to {MaxSide} pixels on each side."));
        }

        if (metadata.Duration is not { } duration)
        {
            findings.Add(new VideoLimitFinding(false, "Couldn't read this video's length, so TikTok's 10 minute limit wasn't checked."));
        }
        else if (duration > MaxDuration)
        {
            findings.Add(new VideoLimitFinding(true, "TikTok takes videos up to 10 minutes long."));
        }

        if (metadata.FramesPerSecond is not { } framesPerSecond)
        {
            findings.Add(new VideoLimitFinding(false, "Couldn't read this video's frame rate, so TikTok's limit on it wasn't checked."));
        }
        else if (framesPerSecond is < MinFramesPerSecond or > MaxFramesPerSecond)
        {
            findings.Add(new VideoLimitFinding(true, "TikTok takes videos from 23 to 60 frames per second."));
        }

        return findings;
    }
}
