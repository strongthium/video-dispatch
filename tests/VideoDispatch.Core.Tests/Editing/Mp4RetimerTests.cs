using System.Buffers.Binary;
using System.Text;
using VideoDispatch.Core.Editing;

namespace VideoDispatch.Core.Tests.Editing;

/// <summary>Retiming a small MP4 built here, box by box, so each number it should change can be read back.</summary>
public sealed class Mp4RetimerTests : IDisposable
{
    private readonly string _folder = Path.Combine(AppContext.BaseDirectory, "retime-" + Guid.NewGuid().ToString("N"));

    public Mp4RetimerTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string PathOf(string name) => Path.Combine(_folder, name);

    private static byte[] Box(string type, params byte[][] content)
    {
        var body = content.SelectMany(part => part).ToArray();
        var box = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        body.CopyTo(box, 8);
        return box;
    }

    private static byte[] Numbers(params uint[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (var each = 0; each < values.Length; each++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(each * 4), values[each]);
        }

        return bytes;
    }

    private static byte[] Track(string handler, uint timescale) => Box("trak",
        Box("tkhd", Numbers(0, 0, 0, 1, 0, 10_000), new byte[60]),
        Box("edts", Box("elst", Numbers(0, 1, 10_000, 0, 0x0001_0000))),
        Box("mdia",
            Box("mdhd", Numbers(0, 0, 0, timescale, timescale * 10, 0)),
            Box("hdlr", Numbers(0, 0), Encoding.ASCII.GetBytes(handler), new byte[13])));

    /// <summary>Ten seconds, in a movie counted in thousandths, with a video track counted in 30,000ths and a sound track.</summary>
    private string Movie()
    {
        var path = PathOf("movie.mp4");
        File.WriteAllBytes(path, [
            .. Box("ftyp", Encoding.ASCII.GetBytes("isom"), new byte[4]),
            .. Box("moov", Box("mvhd", Numbers(0, 0, 0, 1000, 10_000), new byte[80]), Track("vide", 30_000), Track("soun", 48_000)),
            .. Box("mdat", Encoding.ASCII.GetBytes("frames")),
        ]);
        return path;
    }

    private static uint At(byte[] file, string type, int offset, int which = 0)
    {
        var text = Encoding.ASCII.GetString(file);
        var at = -1;
        for (var each = 0; each <= which; each++)
        {
            at = text.IndexOf(type, at + 1, StringComparison.Ordinal);
        }

        return BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at - 4 + offset));
    }

    [Fact]
    public void Half_speed_makes_each_frame_last_twice_as_long_and_the_movie_twice_as_long()
    {
        var output = PathOf("slow.mp4");

        var speed = Mp4Retimer.Retime(Movie(), output, 0.5);

        var file = File.ReadAllBytes(output);
        Assert.Equal(0.5, speed);
        Assert.Equal(15_000u, At(file, "mdhd", 20));
        Assert.Equal(300_000u, At(file, "mdhd", 24));
        Assert.Equal(20_000u, At(file, "mvhd", 24));
        Assert.Equal(20_000u, At(file, "tkhd", 28));
        Assert.Equal(20_000u, At(file, "elst", 16));
        Assert.Equal(0u, At(file, "elst", 20));
    }

    [Fact]
    public void Double_speed_makes_it_half_as_long()
    {
        var output = PathOf("fast.mp4");

        Mp4Retimer.Retime(Movie(), output, 2);

        var file = File.ReadAllBytes(output);
        Assert.Equal(60_000u, At(file, "mdhd", 20));
        Assert.Equal(5_000u, At(file, "mvhd", 24));
    }

    [Fact]
    public void The_sound_track_is_left_out_and_the_rest_of_the_file_is_kept()
    {
        var source = Movie();
        var output = PathOf("slow.mp4");

        Mp4Retimer.Retime(source, output, 0.5);

        var text = Encoding.ASCII.GetString(File.ReadAllBytes(output));
        Assert.Equal(1, text.Split("trak").Length - 1);
        Assert.Contains("free", text);
        Assert.Contains("soun", text);
        Assert.EndsWith("mdatframes", text);
        Assert.Equal(new FileInfo(source).Length, new FileInfo(output).Length);
    }

    [Fact]
    public void A_speed_the_unit_cant_be_exact_for_says_the_speed_it_got()
    {
        var speed = Mp4Retimer.Retime(Movie(), PathOf("odd.mp4"), 1 / 3.0);

        Assert.Equal(10_000 / 30_000.0, speed, 9);
    }

    [Fact]
    public void Something_that_isnt_an_MP4_is_refused_and_no_copy_is_left()
    {
        var notMovie = PathOf("notes.txt");
        File.WriteAllText(notMovie, "These aren't boxes at all, just words.");

        Assert.Throws<InvalidDataException>(() => Mp4Retimer.Retime(notMovie, PathOf("out.mp4"), 0.5));
        Assert.False(File.Exists(PathOf("out.mp4")));
    }

    [Fact]
    public void A_movie_with_only_sound_is_refused()
    {
        var path = PathOf("sound.m4a");
        File.WriteAllBytes(path, Box("moov", Box("mvhd", Numbers(0, 0, 0, 1000, 10_000), new byte[80]), Track("soun", 48_000)));

        Assert.Throws<InvalidDataException>(() => Mp4Retimer.Retime(path, PathOf("out.mp4"), 0.5));
    }
}
