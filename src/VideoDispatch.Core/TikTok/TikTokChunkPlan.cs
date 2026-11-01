namespace VideoDispatch.Core.TikTok;

/// <summary>
/// How a file is split for TikTok. Each chunk must be 5-64 MB, and 16 MiB is inside that whether MB means decimal
/// or binary. The count rounds down and the last chunk takes the remainder, which keeps it under TikTok's 128 MB limit.
/// </summary>
public sealed record TikTokChunkPlan(long VideoSize, long ChunkSize, int ChunkCount)
{
    public const long StandardChunkSize = 16L * 1024 * 1024;

    /// <summary>TikTok says 4 GB. Its media guide counts in binary units, and its own answer settles anything near this.</summary>
    public const long MaxVideoSize = 4L * 1024 * 1024 * 1024;

    public static bool CanUpload(long videoSize) => videoSize is > 0 and <= MaxVideoSize;

    public static TikTokChunkPlan For(long videoSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(videoSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(videoSize, MaxVideoSize);

        return videoSize < StandardChunkSize
            ? new TikTokChunkPlan(videoSize, videoSize, 1)
            : new TikTokChunkPlan(videoSize, StandardChunkSize, (int)(videoSize / StandardChunkSize));
    }

    /// <summary>The first and last byte of a chunk, counted from zero. The last chunk runs to the end of the file.</summary>
    public (long First, long Last) RangeOf(int chunk)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(chunk);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(chunk, ChunkCount);

        var first = chunk * ChunkSize;
        return (first, chunk == ChunkCount - 1 ? VideoSize - 1 : first + ChunkSize - 1);
    }
}
