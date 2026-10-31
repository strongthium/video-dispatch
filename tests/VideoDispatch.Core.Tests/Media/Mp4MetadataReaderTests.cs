using VideoDispatch.Core.Media;
using static VideoDispatch.Core.Tests.Media.Mp4Builder;

namespace VideoDispatch.Core.Tests.Media;

public class Mp4MetadataReaderTests
{
    private static VideoMetadata? Read(byte[] file) => Mp4MetadataReader.Read(new MemoryStream(file));

    private static byte[] SimpleFile(params byte[][] moovChildren) =>
        Join(Ftyp(), Box("moov", moovChildren), Box("mdat", new byte[16]));

    [Fact]
    public void Reads_size_duration_frame_rate_and_codec()
    {
        var metadata = Read(SimpleFile(Mvhd(1000, 10000), VideoTrack()));

        Assert.NotNull(metadata);
        Assert.Equal(1920, metadata.DisplayWidth);
        Assert.Equal(1080, metadata.DisplayHeight);
        Assert.Equal(0, metadata.RotationDegrees);
        Assert.Equal(TimeSpan.FromSeconds(10), metadata.Duration);
        Assert.Equal(30, metadata.FramesPerSecond!.Value, 3);
        Assert.Equal("avc1", metadata.CodecTag);
    }

    [Fact]
    public void Reads_a_fractional_frame_rate()
    {
        var file = SimpleFile(Mvhd(1000, 10000), VideoTrack(timescale: 30000, timing: [(300, 1001)]));

        Assert.Equal(29.97, Read(file)!.FramesPerSecond!.Value, 2);
    }

    [Fact]
    public void Sums_variable_frame_timing()
    {
        var file = SimpleFile(Mvhd(1000, 3000), VideoTrack(timescale: 1000, timing: [(30, 33), (60, 17)]));

        Assert.Equal(90.0 * 1000 / ((30 * 33) + (60 * 17)), Read(file)!.FramesPerSecond!.Value, 6);
    }

    public static TheoryData<int[], int, int, int> Rotations => new()
    {
        { Rotate0, 0, 1920, 1080 },
        { Rotate90, 90, 1080, 1920 },
        { Rotate180, 180, 1920, 1080 },
        { Rotate270, 270, 1080, 1920 },
    };

    [Theory]
    [MemberData(nameof(Rotations))]
    public void Applies_rotation_to_the_display_size(int[] rotation, int degrees, int displayWidth, int displayHeight)
    {
        var metadata = Read(SimpleFile(Mvhd(1000, 10000), VideoTrack(rotation: rotation)));

        Assert.NotNull(metadata);
        Assert.Equal(degrees, metadata.RotationDegrees);
        Assert.Equal(1920, metadata.StoredWidth);
        Assert.Equal(1080, metadata.StoredHeight);
        Assert.Equal(displayWidth, metadata.DisplayWidth);
        Assert.Equal(displayHeight, metadata.DisplayHeight);
    }

    [Fact]
    public void Reads_version_1_headers()
    {
        var file = SimpleFile(
            MvhdV1(1000, 7_200_000),
            VideoTrack(tkhd: TkhdV1(1280, 720, Rotate90)));

        var metadata = Read(file);

        Assert.NotNull(metadata);
        Assert.Equal(TimeSpan.FromHours(2), metadata.Duration);
        Assert.Equal(720, metadata.DisplayWidth);
        Assert.Equal(1280, metadata.DisplayHeight);
    }

    [Fact]
    public void Finds_moov_after_mdat()
    {
        var metadata = Read(Join(Ftyp(), Box("mdat", new byte[64]), Box("moov", Mvhd(1000, 5000), VideoTrack())));

        Assert.Equal(TimeSpan.FromSeconds(5), metadata!.Duration);
    }

    [Fact]
    public void Reads_a_file_without_ftyp()
    {
        Assert.NotNull(Read(Join(Box("moov", Mvhd(600, 6000), VideoTrack()), Box("mdat", new byte[8]))));
    }

    [Fact]
    public void Seeks_past_a_large_mdat_with_a_64_bit_size()
    {
        const long mdatSize = 6L * 1024 * 1024 * 1024;
        var before = Join(Ftyp(), U32(1), "mdat"u8.ToArray(), U64(mdatSize));
        var after = Box("moov", Mvhd(1000, 10000), VideoTrack());

        using var stream = new GapStream(before, mdatSize - 16, after);
        var metadata = Mp4MetadataReader.Read(stream);

        Assert.Equal(TimeSpan.FromSeconds(10), metadata!.Duration);
        Assert.True(stream.BytesRead < 64 * 1024, $"Read {stream.BytesRead} bytes.");
    }

    [Fact]
    public void Treats_an_mdat_with_size_zero_as_running_to_the_end()
    {
        var file = Join(Ftyp(), Box("moov", Mvhd(1000, 10000), VideoTrack()), U32(0), "mdat"u8.ToArray(), new byte[32]);

        Assert.NotNull(Read(file));
    }

    [Fact]
    public void Falls_back_to_track_duration_when_the_movie_duration_is_unknown()
    {
        var metadata = Read(SimpleFile(Mvhd(1000, uint.MaxValue), VideoTrack(timescale: 30000, duration: 600000)));

        Assert.Equal(TimeSpan.FromSeconds(20), metadata!.Duration);
    }

    [Fact]
    public void Reads_fragmented_duration_from_mehd()
    {
        var mehd = Box("mehd", U32(0), U32(12000));
        var track = VideoTrack(duration: 0, timing: []);

        var metadata = Read(SimpleFile(Mvhd(1000, 0), Box("mvex", mehd), track));

        Assert.NotNull(metadata);
        Assert.Equal(TimeSpan.FromSeconds(12), metadata.Duration);
        Assert.Null(metadata.FramesPerSecond);
    }

    [Fact]
    public void Reports_null_duration_when_no_box_states_one()
    {
        var metadata = Read(SimpleFile(Mvhd(1000, 0), VideoTrack(duration: 0, timing: [])));

        Assert.NotNull(metadata);
        Assert.Null(metadata.Duration);
    }

    [Fact]
    public void Picks_the_main_video_over_cover_art_and_audio()
    {
        var coverArt = VideoTrack(width: 500, height: 500, codec: "mjpg", timing: [(1, 1000)]);
        var main = VideoTrack(width: 1080, height: 1920, codec: "hvc1");

        var metadata = Read(SimpleFile(Mvhd(1000, 10000), AudioTrack(), coverArt, main));

        Assert.NotNull(metadata);
        Assert.Equal(1080, metadata.DisplayWidth);
        Assert.Equal("hvc1", metadata.CodecTag);
    }

    [Fact]
    public void Ignores_the_QuickTime_data_handler_inside_minf()
    {
        var track = Box("trak",
            Tkhd(1920, 1080, Rotate90),
            Box("mdia",
                Mdhd(30000, 300000),
                Hdlr("vide"),
                Box("minf",
                    Hdlr("alis"),
                    Box("stbl", Stsd("avc1"), Stts((300, 1000))))));

        var metadata = Read(SimpleFile(Mvhd(600, 6000), track));

        Assert.NotNull(metadata);
        Assert.Equal(1080, metadata.DisplayWidth);
        Assert.Equal(1920, metadata.DisplayHeight);
    }

    [Fact]
    public void Returns_null_for_a_file_with_only_audio()
    {
        Assert.Null(Read(SimpleFile(Mvhd(1000, 10000), AudioTrack())));
    }

    [Fact]
    public void Returns_null_for_an_empty_stream() => Assert.Null(Read([]));

    [Fact]
    public void Returns_null_for_other_file_types()
    {
        // The start of a WebM file, whose first bytes also can't pass as a box.
        Assert.Null(Read([0x1A, 0x45, 0xDF, 0xA3, 0x9F, 0x42, 0x86, 0x81, 0x01, 0x42, 0xF7, 0x81]));
        Assert.Null(Read("this is a plain text file, not a video"u8.ToArray()));
    }

    [Fact]
    public void Survives_a_file_cut_off_at_any_point()
    {
        var file = SimpleFile(Mvhd(1000, 10000), AudioTrack(), VideoTrack());

        for (var length = 0; length < file.Length; length++)
        {
            Read(file[..length]);
        }
    }

    [Fact]
    public void Survives_corrupted_bytes()
    {
        var original = SimpleFile(Mvhd(1000, 10000), AudioTrack(), VideoTrack());
        var random = new Random(20261031);

        for (var round = 0; round < 5000; round++)
        {
            var file = (byte[])original.Clone();
            for (var flip = 0; flip < 4; flip++)
            {
                file[random.Next(file.Length)] = (byte)random.Next(256);
            }

            Read(file);
        }
    }

    [Fact]
    public void A_corrupt_sample_count_cannot_read_past_its_box()
    {
        var stts = Box("stts", U32(0), U32(uint.MaxValue), U32(300), U32(1000));
        var track = Box("trak",
            Tkhd(1920, 1080, Rotate0),
            Box("mdia", Mdhd(30000, 300000), Hdlr("vide"), Box("minf", Box("stbl", Stsd("avc1"), stts))));

        var metadata = Read(SimpleFile(Mvhd(1000, 10000), track));

        Assert.Equal(30, metadata!.FramesPerSecond!.Value, 3);
    }

    [Fact]
    public void Rejects_a_stream_that_cannot_seek()
    {
        Assert.Throws<ArgumentException>(() => Mp4MetadataReader.Read(new NonSeekableStream()));
    }

    private sealed class NonSeekableStream : MemoryStream
    {
        public override bool CanSeek => false;
    }

    /// <summary>A stream with a huge virtual run of zeros in the middle, so no large file is needed.</summary>
    private sealed class GapStream(byte[] before, long gapLength, byte[] after) : Stream
    {
        private long _position;

        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => before.Length + gapLength + after.Length;

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = 0;
            while (read < count && _position < Length)
            {
                buffer[offset + read] = ByteAt(_position);
                _position++;
                read++;
            }

            BytesRead += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => Length + offset,
            };
            return _position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private byte ByteAt(long position)
        {
            if (position < before.Length)
            {
                return before[position];
            }

            var afterStart = before.Length + gapLength;
            return position >= afterStart ? after[position - afterStart] : (byte)0;
        }
    }
}
