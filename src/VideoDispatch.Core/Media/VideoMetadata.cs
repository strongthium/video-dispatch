namespace VideoDispatch.Core.Media;

/// <param name="StoredWidth">Width as stored in the file, before rotation.</param>
/// <param name="StoredHeight">Height as stored in the file, before rotation.</param>
/// <param name="RotationDegrees">Clockwise display rotation: 0, 90, 180 or 270.</param>
/// <param name="Duration">Null when the file doesn't state a usable duration.</param>
/// <param name="FramesPerSecond">Null when the file has no sample table, as in fragmented MP4.</param>
/// <param name="CodecTag">The sample entry code, such as <c>avc1</c> or <c>hvc1</c>.</param>
public sealed record VideoMetadata(
    int StoredWidth,
    int StoredHeight,
    int RotationDegrees,
    TimeSpan? Duration,
    double? FramesPerSecond,
    string? CodecTag)
{
    private bool IsSideways => RotationDegrees is 90 or 270;

    /// <summary>Width as viewers see it. Phones often store vertical video as rotated landscape.</summary>
    public int DisplayWidth => IsSideways ? StoredHeight : StoredWidth;

    /// <summary>Height as viewers see it.</summary>
    public int DisplayHeight => IsSideways ? StoredWidth : StoredHeight;
}
