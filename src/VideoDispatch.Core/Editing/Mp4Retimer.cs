using System.Buffers.Binary;
using System.Text;

namespace VideoDispatch.Core.Editing;

/// <summary>
/// Makes a copy of an MP4 or MOV whose picture plays at another speed, with nothing re-encoded. Every frame's time in a
/// track is counted in that track's own unit, so changing the unit stretches or squeezes them all alike: at half speed,
/// each frame lasts twice as long, and a player showing 30 frames a second shows each one twice. The copy's sound tracks
/// are left out, since the editor mixes sound itself.
/// </summary>
public static class Mp4Retimer
{
    /// <summary>Copies <paramref name="source"/> to <paramref name="output"/> playing at <paramref name="speed"/>, and says the speed it really got.</summary>
    /// <param name="speed">1 as it was, 0.5 for twice as long, 2 for half as long.</param>
    /// <exception cref="InvalidDataException">It isn't an MP4 or MOV this can change.</exception>
    public static double Retime(string source, string output, double speed)
    {
        File.Copy(source, output, overwrite: true);
        try
        {
            using var file = new FileStream(output, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var (moovAt, moovSize) = FindTopLevel(file, "moov") ?? throw new InvalidDataException("There's no movie header in it.");
            if (moovSize > 64 * 1024 * 1024)
            {
                throw new InvalidDataException("Its movie header is too big.");
            }

            var moov = new byte[moovSize];
            file.Position = moovAt;
            file.ReadExactly(moov);

            var realized = Patch(moov, speed);
            file.Position = moovAt;
            file.Write(moov);
            return realized;
        }
        catch
        {
            TryDelete(output);
            throw;
        }
    }

    /// <summary>Changes the movie header in place, and says the speed the video track really got.</summary>
    private static double Patch(byte[] moov, double speed)
    {
        double? realized = null;
        var stretch = 1 / speed;

        foreach (var (type, at, size) in Children(moov, 8, moov.Length))
        {
            switch (type)
            {
                case "mvhd":
                    ScaleDuration(moov, at, shortAt: 24, longAt: 32, stretch);
                    break;

                case "mvex":
                    foreach (var (inner, innerAt, _) in Children(moov, at + 8, at + size))
                    {
                        if (inner == "mehd")
                        {
                            ScaleDuration(moov, innerAt, shortAt: 12, longAt: 12, stretch);
                        }
                    }

                    break;

                case "trak":
                    if (HandlerOf(moov, at, size) != "vide")
                    {
                        // Gone from the copy, by being renamed to empty space.
                        Encoding.ASCII.GetBytes("free").CopyTo(moov, at + 4);
                        break;
                    }

                    realized ??= PatchVideo(moov, at, size, speed, stretch);
                    break;
            }
        }

        return realized ?? throw new InvalidDataException("There's no video in it.");
    }

    private static double PatchVideo(byte[] moov, int trakAt, int trakSize, double speed, double stretch)
    {
        double realized = speed;
        foreach (var (type, at, size) in Children(moov, trakAt + 8, trakAt + trakSize))
        {
            switch (type)
            {
                case "tkhd":
                    ScaleDuration(moov, at, shortAt: 28, longAt: 36, stretch);
                    break;

                case "edts":
                    foreach (var (inner, innerAt, innerSize) in Children(moov, at + 8, at + size))
                    {
                        if (inner == "elst")
                        {
                            ScaleEditList(moov, innerAt, innerSize, stretch);
                        }
                    }

                    break;

                case "mdia":
                    foreach (var (inner, innerAt, _) in Children(moov, at + 8, at + size))
                    {
                        if (inner == "mdhd")
                        {
                            // The unit's size is how many there are in a second: fewer makes each frame last longer.
                            var version = moov[innerAt + 8];
                            var scaleAt = innerAt + (version == 1 ? 28 : 20);
                            var old = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(scaleAt));
                            var scale = (uint)Math.Max(1, Math.Round(old * speed));
                            BinaryPrimitives.WriteUInt32BigEndian(moov.AsSpan(scaleAt), scale);
                            realized = scale / (double)old;
                        }
                    }

                    break;
            }
        }

        return realized;
    }

    /// <summary>A header's duration, in the movie's own unit, made longer by <paramref name="stretch"/>.</summary>
    /// <param name="shortAt">Where it is from the box's start in a version 0 header, where it takes four bytes.</param>
    /// <param name="longAt">Where it is in a version 1 header, where it takes eight.</param>
    private static void ScaleDuration(byte[] moov, int at, int shortAt, int longAt, double stretch)
    {
        if (moov[at + 8] == 1)
        {
            var value = BinaryPrimitives.ReadUInt64BigEndian(moov.AsSpan(at + longAt));
            BinaryPrimitives.WriteUInt64BigEndian(moov.AsSpan(at + longAt), (ulong)Math.Round(value * stretch));
        }
        else
        {
            var value = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(at + shortAt));
            BinaryPrimitives.WriteUInt32BigEndian(moov.AsSpan(at + shortAt), (uint)Math.Min(uint.MaxValue, Math.Round(value * stretch)));
        }
    }

    /// <summary>Each part of an edit list lasts longer. Where each starts in the track is in the track's unit, which already changed.</summary>
    private static void ScaleEditList(byte[] moov, int at, int size, double stretch)
    {
        var version = moov[at + 8];
        var count = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(at + 12));
        var entry = at + 16;
        for (var each = 0; each < count && entry < at + size; each++)
        {
            if (version == 1)
            {
                var value = BinaryPrimitives.ReadUInt64BigEndian(moov.AsSpan(entry));
                BinaryPrimitives.WriteUInt64BigEndian(moov.AsSpan(entry), (ulong)Math.Round(value * stretch));
                entry += 20;
            }
            else
            {
                var value = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(entry));
                BinaryPrimitives.WriteUInt32BigEndian(moov.AsSpan(entry), (uint)Math.Min(uint.MaxValue, Math.Round(value * stretch)));
                entry += 12;
            }
        }
    }

    private static string? HandlerOf(byte[] moov, int trakAt, int trakSize)
    {
        foreach (var (type, at, size) in Children(moov, trakAt + 8, trakAt + trakSize))
        {
            if (type != "mdia")
            {
                continue;
            }

            foreach (var (inner, innerAt, _) in Children(moov, at + 8, at + size))
            {
                if (inner == "hdlr")
                {
                    return Encoding.ASCII.GetString(moov, innerAt + 16, 4);
                }
            }
        }

        return null;
    }

    /// <summary>The boxes from <paramref name="from"/> to <paramref name="to"/>: their names, where each starts, and how long.</summary>
    private static List<(string Type, int At, int Size)> Children(byte[] data, int from, int to)
    {
        var boxes = new List<(string, int, int)>();
        var at = from;
        while (at + 8 <= to)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));
            var type = Encoding.ASCII.GetString(data, at + 4, 4);
            if (size == 1 && at + 16 <= to)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(at + 8));
            }
            else if (size == 0)
            {
                size = to - at;
            }

            if (size < 8 || at + size > to)
            {
                throw new InvalidDataException("Its movie header is damaged.");
            }

            boxes.Add((type, at, (int)size));
            at += (int)size;
        }

        return boxes;
    }

    private static (long At, int Size)? FindTopLevel(FileStream file, string wanted)
    {
        Span<byte> head = stackalloc byte[16];
        long at = 0;
        while (at + 8 <= file.Length)
        {
            file.Position = at;
            file.ReadExactly(head[..8]);
            long size = BinaryPrimitives.ReadUInt32BigEndian(head);
            var type = Encoding.ASCII.GetString(head[4..8]);
            if (size == 1)
            {
                file.ReadExactly(head[8..16]);
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(head[8..]);
            }
            else if (size == 0)
            {
                size = file.Length - at;
            }

            if (size < 8)
            {
                return null;
            }

            if (type == wanted)
            {
                return size > int.MaxValue ? null : (at, (int)size);
            }

            at += size;
        }

        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
