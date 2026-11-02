using System.Globalization;
using VideoDispatch.Core.Media;

namespace VideoDispatch.Core.Reencoding;

public enum EncodeGoal
{
    HighQuality,
    Balanced,
    SmallFile,
    FitSize,
}

/// <summary>What to do to a video before sending it. No settings means it's sent as it is.</summary>
/// <param name="Goal">What the new file is for.</param>
/// <param name="TargetMegabytes">How big the file may be, for <see cref="EncodeGoal.FitSize"/>.</param>
public sealed record EncodeSettings(EncodeGoal Goal, int? TargetMegabytes = null);

/// <summary>
/// The numbers both engines work from, so the same choice means the same thing with either. The platforms compress
/// everything again, so these lean towards keeping quality, and never ask for more than the source has.
/// </summary>
/// <param name="Width">Width as viewers see it, or null to keep the source's when it can't be read.</param>
/// <param name="Height">Height as viewers see it, or null to keep the source's when it can't be read.</param>
/// <param name="FramesPerSecond">The source's frame rate, if known.</param>
/// <param name="VideoBitrate">Bits per second for the picture.</param>
/// <param name="AudioBitrate">Bits per second for the sound. Windows' AAC encoder takes only 96, 128, 160 and 192 kbps.</param>
/// <param name="ConstantQuality">For engines that can aim at a quality rather than a bitrate. Null means the size matters.</param>
/// <param name="Duration">The source's length, if known.</param>
/// <param name="SourceIsSideways">The source is stored turned a quarter, and marked to be shown upright, as phones do.</param>
public sealed record EncodePlan(
    int? Width,
    int? Height,
    double? FramesPerSecond,
    long VideoBitrate,
    int AudioBitrate,
    int? ConstantQuality,
    TimeSpan? Duration,
    bool SourceIsSideways = false)
{
    private const double Mebibyte = 1024 * 1024;
    private const long LowestVideoBitrate = 150_000;
    private const long HighestVideoBitrate = 60_000_000;
    private const int FitAudioBitrate = 128_000;

    // A little is held back, because encoders overshoot a target.
    private const double FitMargin = 0.97;

    /// <summary>What's left to fill in or choose, if anything.</summary>
    public static string? ProblemWith(EncodeSettings settings, VideoMetadata? metadata)
    {
        if (settings.Goal != EncodeGoal.FitSize)
        {
            return null;
        }

        if (settings.TargetMegabytes is not > 0)
        {
            return "Enter the size to fit, in megabytes.";
        }

        if (metadata?.Duration is not { TotalSeconds: > 0 } duration)
        {
            return "This video's length couldn't be read, so it can't be fitted to a size. Choose another way to re-encode it.";
        }

        var needed = (LowestVideoBitrate + FitAudioBitrate) * duration.TotalSeconds / 8 / FitMargin / Mebibyte;
        return settings.TargetMegabytes < needed
            ? string.Create(CultureInfo.InvariantCulture, $"{settings.TargetMegabytes} MB is too small for a video this long. It needs at least {Math.Ceiling(needed):0} MB.")
            : null;
    }

    public static EncodePlan For(EncodeSettings settings, VideoMetadata? metadata, long sourceBytes)
    {
        var (bitsPerPixel, longestSide, audio, quality) = settings.Goal switch
        {
            EncodeGoal.HighQuality => (0.16, (int?)null, 192_000, (int?)18),
            EncodeGoal.Balanced => (0.08, 1920, 160_000, 23),
            EncodeGoal.SmallFile => (0.04, 1280, 128_000, 28),
            _ => (0d, null, FitAudioBitrate, null),
        };

        var seconds = metadata?.Duration is { TotalSeconds: > 0 } duration ? duration.TotalSeconds : (double?)null;
        var frames = metadata?.FramesPerSecond is > 0 ? metadata.FramesPerSecond : null;

        long video;
        if (settings.Goal == EncodeGoal.FitSize)
        {
            // ProblemWith has already asked for a size and a length.
            var bits = (settings.TargetMegabytes ?? 0) * Mebibyte * 8 * FitMargin;
            video = (long)(bits / (seconds ?? 1)) - audio;
            longestSide = video switch
            {
                >= 5_000_000 => null,
                >= 2_500_000 => 1920,
                >= 1_000_000 => 1280,
                _ => 854,
            };
        }
        else
        {
            var (width, height) = SizeFor(metadata, longestSide);
            video = width is { } w && height is { } h
                ? (long)(w * h * (frames ?? 30) * bitsPerPixel)
                : settings.Goal switch { EncodeGoal.HighQuality => 8_000_000, EncodeGoal.Balanced => 4_000_000, _ => 2_000_000 };

            if (seconds is { } length)
            {
                var sourceVideo = (long)(sourceBytes * 8 / length) - audio;
                if (sourceVideo > 0)
                {
                    video = Math.Min(video, sourceVideo);
                }
            }
        }

        var (finalWidth, finalHeight) = SizeFor(metadata, longestSide);
        return new EncodePlan(
            finalWidth,
            finalHeight,
            frames,
            Math.Clamp(video, LowestVideoBitrate, HighestVideoBitrate),
            audio,
            quality,
            metadata?.Duration,
            metadata?.RotationDegrees is 90 or 270);
    }

    /// <summary>
    /// For saving an edit: the first clip's size and frame rate, at high quality, since the new file may well be
    /// re-encoded again before it's sent. Clips of another shape are fitted inside, with black bars. A first clip whose
    /// size can't be read makes a landscape 1080p video.
    /// </summary>
    public static EncodePlan ForEdit(VideoMetadata? first, TimeSpan length)
    {
        // With no source size to compare against, the quality setting alone decides the bitrate.
        var plan = For(new EncodeSettings(EncodeGoal.HighQuality), first is null ? null : first with { Duration = length }, 0);
        return plan with
        {
            Width = plan.Width ?? 1920,
            Height = plan.Height ?? 1080,
            Duration = length,
            SourceIsSideways = false,
        };
    }

    /// <summary>About how big the file will be. An engine aiming at quality may come out quite different.</summary>
    public long? EstimatedBytes => Duration is { TotalSeconds: > 0 } duration
        ? (long)((VideoBitrate + AudioBitrate) * duration.TotalSeconds / 8)
        : null;

    /// <summary>Never larger than the source, and always even, because H.264 needs that.</summary>
    private static (int? Width, int? Height) SizeFor(VideoMetadata? metadata, int? longestSide)
    {
        if (metadata is not { DisplayWidth: > 0, DisplayHeight: > 0 })
        {
            return (null, null);
        }

        double width = metadata.DisplayWidth;
        double height = metadata.DisplayHeight;
        var longest = Math.Max(width, height);
        if (longestSide is { } limit && longest > limit)
        {
            width = width * limit / longest;
            height = height * limit / longest;
        }

        return (Even(width), Even(height));
    }

    private static int Even(double side) => Math.Max(2, (int)Math.Round(side / 2) * 2);
}
