using System.Buffers.Binary;
using System.Text;

namespace VideoDispatch.Core.Tests.Media;

/// <summary>Builds minimal MP4 structures byte by byte, following ISO/IEC 14496-12.</summary>
internal static class Mp4Builder
{
    public static readonly int[] Rotate0 = [1, 0, 0, 1];
    public static readonly int[] Rotate90 = [0, 1, -1, 0];
    public static readonly int[] Rotate180 = [-1, 0, 0, -1];
    public static readonly int[] Rotate270 = [0, -1, 1, 0];

    public static byte[] Box(string type, params byte[][] children)
    {
        var payload = children.SelectMany(child => child).ToArray();
        return [.. U32((uint)(payload.Length + 8)), .. Encoding.Latin1.GetBytes(type), .. payload];
    }

    public static byte[] Ftyp() => Box("ftyp", Encoding.ASCII.GetBytes("isom"), U32(512), Encoding.ASCII.GetBytes("isom"));

    public static byte[] Mvhd(uint timescale, uint duration) =>
        Box("mvhd", U32(0), U32(0), U32(0), U32(timescale), U32(duration), new byte[80]);

    public static byte[] MvhdV1(uint timescale, ulong duration) =>
        Box("mvhd", U32(0x01000000), U64(0), U64(0), U32(timescale), U64(duration), new byte[80]);

    public static byte[] Tkhd(int width, int height, int[] rotation) =>
        Box("tkhd", U32(0), U32(0), U32(0), U32(1), U32(0), U32(0), new byte[8], new byte[8], Matrix(rotation), Fixed(width), Fixed(height));

    public static byte[] TkhdV1(int width, int height, int[] rotation) =>
        Box("tkhd", U32(0x01000000), U64(0), U64(0), U32(1), U32(0), U64(0), new byte[8], new byte[8], Matrix(rotation), Fixed(width), Fixed(height));

    public static byte[] Mdhd(uint timescale, uint duration) =>
        Box("mdhd", U32(0), U32(0), U32(0), U32(timescale), U32(duration), new byte[4]);

    public static byte[] Hdlr(string handler) =>
        Box("hdlr", U32(0), U32(0), Encoding.ASCII.GetBytes(handler), new byte[12], [0]);

    public static byte[] Stsd(string codec) =>
        Box("stsd", U32(0), U32(1), Box(codec, new byte[78]));

    public static byte[] Stts(params (uint Count, uint Delta)[] entries) =>
        Box("stts", [U32(0), U32((uint)entries.Length), .. entries.SelectMany(entry => new[] { U32(entry.Count), U32(entry.Delta) })]);

    public static byte[] VideoTrack(
        int width = 1920,
        int height = 1080,
        int[]? rotation = null,
        uint timescale = 30000,
        uint duration = 300000,
        string codec = "avc1",
        (uint Count, uint Delta)[]? timing = null,
        byte[]? tkhd = null)
    {
        return Box("trak",
            tkhd ?? Tkhd(width, height, rotation ?? Rotate0),
            Box("mdia",
                Mdhd(timescale, duration),
                Hdlr("vide"),
                Box("minf",
                    Box("stbl",
                        Stsd(codec),
                        Stts(timing ?? [(300, 1000)])))));
    }

    public static byte[] AudioTrack() =>
        Box("trak",
            Tkhd(0, 0, Rotate0),
            Box("mdia",
                Mdhd(48000, 480000),
                Hdlr("soun"),
                Box("minf", Box("stbl", Stsd("mp4a"), Stts((10000, 1024))))));

    public static byte[] Join(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

    public static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    public static byte[] U64(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Fixed(int value) => U32((uint)value << 16);

    /// <summary>Writes a full matrix from its rotation part, in 16.16 with u, v and w as the spec sets them.</summary>
    private static byte[] Matrix(int[] rotation)
    {
        return Join(
            I32(rotation[0] << 16), I32(rotation[1] << 16), I32(0),
            I32(rotation[2] << 16), I32(rotation[3] << 16), I32(0),
            I32(0), I32(0), I32(0x40000000));
    }

    private static byte[] I32(int value) => U32(unchecked((uint)value));
}
