using System.Buffers.Binary;
using System.Text;

namespace VideoDispatch.Core.Media;

/// <summary>
/// Reads video properties from MP4 and MOV files without decoding anything. It seeks past
/// media data, so the file size doesn't matter, and the moov box can sit at either end.
/// </summary>
public static class Mp4MetadataReader
{
    private const int MaxDepth = 8;
    private const int MaxSmallBoxBytes = 1024;
    private const long UnknownDuration32 = uint.MaxValue;

    private static readonly HashSet<string> TopLevelTypes =
    [
        "ftyp", "moov", "mdat", "free", "skip", "wide", "pnot", "uuid", "styp", "moof", "sidx", "mfra", "meta",
    ];

    /// <summary>Returns null when the stream isn't an MP4 or MOV file with a video track.</summary>
    public static VideoMetadata? Read(Stream stream)
    {
        if (!stream.CanSeek)
        {
            throw new ArgumentException("The stream must support seeking.", nameof(stream));
        }

        try
        {
            return ReadCore(stream);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }

    public static VideoMetadata? Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Read(stream);
    }

    private static VideoMetadata? ReadCore(Stream stream)
    {
        var first = true;
        foreach (var box in EnumerateBoxes(stream, 0, stream.Length))
        {
            if (first && !TopLevelTypes.Contains(box.Type))
            {
                return null;
            }

            first = false;

            if (box.Type == "moov")
            {
                var movie = new MovieInfo();
                ReadMovie(stream, box, movie);
                return movie.ToMetadata();
            }
        }

        return null;
    }

    private static void ReadMovie(Stream stream, Box moov, MovieInfo movie)
    {
        foreach (var box in EnumerateBoxes(stream, moov.DataStart, moov.End))
        {
            switch (box.Type)
            {
                case "mvhd":
                    ReadMovieHeader(stream, box, movie);
                    break;
                case "mvex":
                    foreach (var child in EnumerateBoxes(stream, box.DataStart, box.End))
                    {
                        if (child.Type == "mehd")
                        {
                            movie.FragmentDuration = ReadFragmentDuration(stream, child);
                        }
                    }

                    break;
                case "trak":
                    var track = new TrackInfo();
                    ReadTrack(stream, box, track, 0);
                    movie.Tracks.Add(track);
                    break;
            }
        }
    }

    private static void ReadTrack(Stream stream, Box parent, TrackInfo track, int depth)
    {
        if (depth >= MaxDepth)
        {
            return;
        }

        foreach (var box in EnumerateBoxes(stream, parent.DataStart, parent.End))
        {
            switch (box.Type)
            {
                case "mdia" or "minf" or "stbl":
                    ReadTrack(stream, box, track, depth + 1);
                    break;
                case "tkhd":
                    ReadTrackHeader(stream, box, track);
                    break;
                case "mdhd":
                    ReadMediaHeader(stream, box, track);
                    break;
                // QuickTime files have a second hdlr in minf that names the data handler, not the media type.
                case "hdlr" when parent.Type == "mdia":
                    ReadHandler(stream, box, track);
                    break;
                case "stsd":
                    ReadSampleDescription(stream, box, track);
                    break;
                case "stts":
                    ReadSampleTiming(stream, box, track);
                    break;
            }
        }
    }

    private static void ReadMovieHeader(Stream stream, Box box, MovieInfo movie)
    {
        var data = ReadSmallBox(stream, box);
        if (data[0] == 1)
        {
            movie.Timescale = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20));
            movie.Duration = ToKnownDuration(BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(24)));
        }
        else
        {
            movie.Timescale = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12));
            var duration = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16));
            movie.Duration = duration == UnknownDuration32 ? null : duration;
        }
    }

    private static long? ReadFragmentDuration(Stream stream, Box box)
    {
        var data = ReadSmallBox(stream, box);
        return data[0] == 1
            ? ToKnownDuration(BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(4)))
            : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
    }

    private static void ReadTrackHeader(Stream stream, Box box, TrackInfo track)
    {
        var data = ReadSmallBox(stream, box);

        // Version 1 widens the two dates and the duration, which moves the later fields by 12 bytes.
        var matrixStart = data[0] == 1 ? 52 : 40;
        var a = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(matrixStart));
        var b = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(matrixStart + 4));
        var c = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(matrixStart + 12));
        var d = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(matrixStart + 16));
        track.RotationDegrees = RotationFromMatrix(a, b, c, d);

        // Both sizes are 16.16 fixed point.
        var sizeStart = matrixStart + 36;
        track.Width = (int)(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(sizeStart)) >> 16);
        track.Height = (int)(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(sizeStart + 4)) >> 16);
    }

    private static int RotationFromMatrix(int a, int b, int c, int d) => (Math.Sign(a), Math.Sign(b), Math.Sign(c), Math.Sign(d)) switch
    {
        (0, 1, -1, 0) => 90,
        (-1, 0, 0, -1) => 180,
        (0, -1, 1, 0) => 270,
        _ => 0,
    };

    private static void ReadMediaHeader(Stream stream, Box box, TrackInfo track)
    {
        var data = ReadSmallBox(stream, box);
        if (data[0] == 1)
        {
            track.Timescale = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20));
            track.Duration = ToKnownDuration(BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(24)));
        }
        else
        {
            track.Timescale = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12));
            var duration = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16));
            track.Duration = duration == UnknownDuration32 ? null : duration;
        }
    }

    private static void ReadHandler(Stream stream, Box box, TrackInfo track)
    {
        var data = ReadSmallBox(stream, box);
        track.IsVideo = Encoding.ASCII.GetString(data, 8, 4) == "vide";
    }

    private static void ReadSampleDescription(Stream stream, Box box, TrackInfo track)
    {
        var data = ReadSmallBox(stream, box);
        var entryCount = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
        if (entryCount > 0)
        {
            track.CodecTag = Encoding.ASCII.GetString(data, 12, 4);
        }
    }

    private static void ReadSampleTiming(Stream stream, Box box, TrackInfo track)
    {
        stream.Seek(box.DataStart, SeekOrigin.Begin);

        Span<byte> header = stackalloc byte[8];
        stream.ReadExactly(header);

        // The box size caps the loop, so a corrupt entry count can't run past the box.
        var available = (box.End - box.DataStart - 8) / 8;
        var entries = Math.Min(BinaryPrimitives.ReadUInt32BigEndian(header[4..]), available);

        Span<byte> entry = stackalloc byte[8];
        for (long i = 0; i < entries; i++)
        {
            stream.ReadExactly(entry);
            ulong count = BinaryPrimitives.ReadUInt32BigEndian(entry);
            ulong delta = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
            track.SampleCount += count;
            track.SampleTicks += count * delta;
        }
    }

    private static long? ToKnownDuration(ulong duration) =>
        duration >= long.MaxValue ? null : (long)duration;

    private static byte[] ReadSmallBox(Stream stream, Box box)
    {
        var length = (int)Math.Min(box.End - box.DataStart, MaxSmallBoxBytes);
        var data = new byte[MaxSmallBoxBytes];
        stream.Seek(box.DataStart, SeekOrigin.Begin);
        stream.ReadExactly(data, 0, length);
        return data;
    }

    private static IEnumerable<Box> EnumerateBoxes(Stream stream, long start, long end)
    {
        var position = start;
        var header = new byte[16];

        while (end - position >= 8)
        {
            stream.Seek(position, SeekOrigin.Begin);
            stream.ReadExactly(header, 0, 8);

            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Encoding.Latin1.GetString(header, 4, 4);
            var headerSize = 8L;

            if (size == 1)
            {
                if (end - position < 16)
                {
                    yield break;
                }

                stream.ReadExactly(header, 8, 8);
                var largeSize = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                size = largeSize > long.MaxValue ? -1 : (long)largeSize;
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = end - position;
            }

            // A size that doesn't fit its parent means the file is damaged, so stop here.
            if (size < headerSize || size > end - position)
            {
                yield break;
            }

            yield return new Box(type, position + headerSize, position + size);
            position += size;
        }
    }

    private readonly record struct Box(string Type, long DataStart, long End);

    private sealed class MovieInfo
    {
        public uint Timescale { get; set; }
        public long? Duration { get; set; }
        public long? FragmentDuration { get; set; }
        public List<TrackInfo> Tracks { get; } = [];

        public VideoMetadata? ToMetadata()
        {
            // Cover art is also stored as a video track, so the main video is the one with the most samples.
            var video = Tracks
                .Where(track => track.IsVideo && track.Width > 0 && track.Height > 0)
                .OrderByDescending(track => track.SampleCount)
                .FirstOrDefault();

            return video is null
                ? null
                : new VideoMetadata(
                    video.Width,
                    video.Height,
                    video.RotationDegrees,
                    ResolveDuration(video),
                    video.FramesPerSecond,
                    video.CodecTag);
        }

        private TimeSpan? ResolveDuration(TrackInfo video)
        {
            var movieTicks = Duration is > 0 ? Duration : FragmentDuration;
            return ToTimeSpan(movieTicks, Timescale) ?? ToTimeSpan(video.Duration, video.Timescale);
        }

        private static TimeSpan? ToTimeSpan(long? ticks, uint timescale)
        {
            if (ticks is not > 0 || timescale == 0)
            {
                return null;
            }

            var seconds = (double)ticks.Value / timescale;
            return seconds < TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(seconds) : null;
        }
    }

    private sealed class TrackInfo
    {
        public bool IsVideo { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int RotationDegrees { get; set; }
        public uint Timescale { get; set; }
        public long? Duration { get; set; }
        public string? CodecTag { get; set; }
        public ulong SampleCount { get; set; }
        public ulong SampleTicks { get; set; }

        public double? FramesPerSecond =>
            SampleCount > 0 && SampleTicks > 0 && Timescale > 0
                ? (double)SampleCount * Timescale / SampleTicks
                : null;
    }
}
