using VideoDispatch.Core.Media;
using VideoDispatch.Core.TikTok;

namespace VideoDispatch.Core.Tests.TikTok;

public class TikTokChunkPlanTests
{
    private const long MiB = 1024 * 1024;
    private const long Chunk = TikTokChunkPlan.StandardChunkSize;
    private const long Max = TikTokChunkPlan.MaxVideoSize;

    [Theory]
    [InlineData(1, 1, 1)]                            // the smallest file
    [InlineData(5 * MiB - 1, 5 * MiB - 1, 1)]        // under TikTok's 5 MB minimum, so one request
    [InlineData(Chunk - 1, Chunk - 1, 1)]            // just under one chunk
    [InlineData(Chunk, Chunk, 1)]                    // exactly one chunk
    [InlineData(Chunk + 1, Chunk, 1)]                // one byte over: the count rounds down
    [InlineData(2 * Chunk - 1, Chunk, 1)]            // just under two chunks
    [InlineData(2 * Chunk, Chunk, 2)]                // exactly two chunks
    [InlineData(Max, Chunk, 256)]                    // the largest file
    public void Splits_at_the_boundaries(long size, long chunkSize, int count)
    {
        var plan = TikTokChunkPlan.For(size);

        Assert.Equal(size, plan.VideoSize);
        Assert.Equal(chunkSize, plan.ChunkSize);
        Assert.Equal(count, plan.ChunkCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(Max + 1)]
    public void Refuses_sizes_TikTok_would_refuse(long size)
    {
        Assert.False(TikTokChunkPlan.CanUpload(size));
        Assert.Throws<ArgumentOutOfRangeException>(() => TikTokChunkPlan.For(size));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5 * MiB)]
    [InlineData(Chunk - 1)]
    [InlineData(Chunk)]
    [InlineData(Chunk + 1)]
    [InlineData(2 * Chunk - 1)]
    [InlineData(3 * Chunk + 123)]
    [InlineData(1000 * MiB + 7)]
    [InlineData(Max - 1)]
    [InlineData(Max)]
    public void Chunks_cover_the_file_exactly_and_keep_to_TikToks_rules(long size)
    {
        var plan = TikTokChunkPlan.For(size);
        long next = 0;

        for (var chunk = 0; chunk < plan.ChunkCount; chunk++)
        {
            var (first, last) = plan.RangeOf(chunk);
            Assert.Equal(next, first);
            next = last + 1;

            var length = last - first + 1;
            if (chunk == plan.ChunkCount - 1)
            {
                // TikTok's final chunk may reach 128 MB, read here as decimal to be safe.
                Assert.InRange(length, 1, 128_000_000);
            }
            else
            {
                Assert.Equal(plan.ChunkSize, length);
            }
        }

        Assert.Equal(size, next);
        Assert.InRange(plan.ChunkCount, 1, 1000);

        // Over 5 MB, the declared size must be inside 5-64 MB by either reading of MB.
        if (size >= 5 * MiB)
        {
            Assert.InRange(plan.ChunkSize, 5 * MiB, 64_000_000);
        }
    }

    [Fact]
    public void Refuses_a_chunk_outside_the_plan()
    {
        var plan = TikTokChunkPlan.For(Chunk);

        Assert.Throws<ArgumentOutOfRangeException>(() => plan.RangeOf(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => plan.RangeOf(-1));
    }

    private static VideoMetadata Good => new(1080, 1920, 0, TimeSpan.FromMinutes(1), 30, "avc1");

    [Theory]
    [InlineData("clip.mp4", "video/mp4")]
    [InlineData("CLIP.MP4", "video/mp4")]
    [InlineData("clip.mov", "video/quicktime")]
    [InlineData("clip.webm", "video/webm")]
    [InlineData("clip.avi", null)]
    [InlineData("clip", null)]
    public void Knows_the_file_types_TikTok_takes(string path, string? expected)
    {
        Assert.Equal(expected, TikTokVideoLimits.ContentTypeFor(path));
    }

    [Fact]
    public void A_good_video_has_no_findings()
    {
        Assert.Empty(TikTokVideoLimits.Check("clip.mp4", 1000, Good));
    }

    [Fact]
    public void Exactly_at_the_limits_is_fine()
    {
        Assert.Empty(TikTokVideoLimits.Check("clip.mp4", Max, new VideoMetadata(360, 4096, 0, TimeSpan.FromMinutes(10), 23, null)));
        Assert.Empty(TikTokVideoLimits.Check("clip.mp4", 1, new VideoMetadata(4096, 360, 0, TimeSpan.Zero, 60, null)));
    }

    public static TheoryData<string, long, VideoMetadata> Refused => new()
    {
        { "clip.avi", 1000, Good },
        { "clip.mp4", 0, Good },
        { "clip.mp4", Max + 1, Good },
        { "clip.mp4", 1000, Good with { StoredWidth = 359 } },
        { "clip.mp4", 1000, Good with { StoredHeight = 4097 } },
        { "clip.mp4", 1000, Good with { Duration = TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1) } },
        { "clip.mp4", 1000, Good with { FramesPerSecond = 22.9 } },
        { "clip.mp4", 1000, Good with { FramesPerSecond = 60.1 } },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void Blocks_what_TikTok_would_refuse(string path, long size, VideoMetadata metadata)
    {
        Assert.Contains(TikTokVideoLimits.Check(path, size, metadata), finding => finding.Blocks);
    }

    [Fact]
    public void A_common_frame_rate_just_above_the_minimum_is_fine()
    {
        Assert.Empty(TikTokVideoLimits.Check("clip.mp4", 1000, Good with { FramesPerSecond = 23.976 }));
    }

    [Fact]
    public void A_rotated_video_is_measured_as_viewers_see_it()
    {
        // Both sides are in range either way, so this checks only that rotation can't invent a problem.
        Assert.Empty(TikTokVideoLimits.Check("clip.mp4", 1000, new VideoMetadata(1920, 1080, 90, TimeSpan.FromSeconds(9), 30, "avc1")));
    }

    [Fact]
    public void Metadata_that_cannot_be_read_warns_and_does_not_block()
    {
        var findings = TikTokVideoLimits.Check("clip.webm", 1000, null);

        Assert.Single(findings);
        Assert.False(findings[0].Blocks);
    }

    [Fact]
    public void An_unknown_length_or_frame_rate_warns_and_does_not_block()
    {
        var findings = TikTokVideoLimits.Check("clip.mp4", 1000, Good with { Duration = null, FramesPerSecond = null });

        Assert.Equal(2, findings.Count);
        Assert.All(findings, finding => Assert.False(finding.Blocks));
    }
}
