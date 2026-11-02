using System.Buffers.Binary;
using VideoDispatch.Core.Reencoding;

namespace VideoDispatch.Core.Editing;

/// <summary>A stretch of one file's sound, from <paramref name="From"/> to <paramref name="To"/> of its own time, heard from <paramref name="At"/> in the edit.</summary>
/// <param name="Gain">How loud it is at a time into the stretch, from 0 for silent to 1 as it was.</param>
public sealed record SoundPiece(string SourcePath, TimeSpan From, TimeSpan To, TimeSpan At, Func<TimeSpan, double> Gain)
{
    public TimeSpan Length => To - From;
}

/// <summary>
/// Makes all of an edit's sound as one file: each clip's own sound and each sound laid over it, at their volumes, with
/// their fades. Both engines use it, so an edit sounds the same whichever saves it. Windows' tools can only set one
/// volume for a whole clip, which is why fades and crossfades of sound are done here.
/// </summary>
public static class SoundMixer
{
    public const int Rate = 48000;
    public const int Channels = 2;
    private const int BytesPerFrame = Channels * 2;
    private const int BlockFrames = Rate / 10;

    /// <summary>The stretches of sound in <paramref name="project"/>. Anything silent throughout is left out.</summary>
    public static IReadOnlyList<SoundPiece> PiecesOf(EditProject project)
    {
        var pieces = new List<SoundPiece>();
        for (var index = 0; index < project.Clips.Count; index++)
        {
            var clip = project.Clips[index];
            if (clip.Volume > 0)
            {
                var each = index;
                pieces.Add(new SoundPiece(clip.SourcePath, clip.Start, clip.End, project.StartOf(index), offset => project.ClipGain(each, offset)));
            }
        }

        for (var index = 0; index < project.Sounds.Count; index++)
        {
            var sound = project.Sounds[index];
            var heard = project.HeardLengthOf(index);
            if (sound.Volume > 0 && heard > TimeSpan.Zero)
            {
                var each = index;
                pieces.Add(new SoundPiece(sound.SourcePath, sound.Start, sound.Start + heard, sound.At, offset => project.SoundGain(each, offset)));
            }
        }

        return pieces;
    }

    /// <summary>
    /// Writes the edit's sound to <paramref name="output"/> as a WAV exactly as long as the edit, after asking for each
    /// piece's sound with <paramref name="read"/>.
    /// </summary>
    /// <param name="folder">Where each piece's sound is put while mixing.</param>
    /// <param name="read">
    /// Writes a piece's sound to a path, as 48 kHz 16-bit stereo WAV, and says whether there was any. A video with no
    /// sound is silence, not an error.
    /// </param>
    public static async Task MixAsync(EditProject project, string folder, string output, Func<SoundPiece, string, CancellationToken, Task<bool>> read, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(folder);
        var placed = new List<(SoundPiece Piece, string Path)>();
        var pieces = PiecesOf(project);
        for (var index = 0; index < pieces.Count; index++)
        {
            var path = Path.Combine(folder, $"sound-{index}.wav");
            if (await read(pieces[index], path, cancellationToken))
            {
                placed.Add((pieces[index], path));
            }
        }

        await Task.Run(() => Mix(placed, project.Length, output, cancellationToken), cancellationToken);
    }

    /// <summary>Lays each piece's sound where it goes, and writes them as one, no longer than <paramref name="length"/>.</summary>
    public static void Mix(IReadOnlyList<(SoundPiece Piece, string Path)> pieces, TimeSpan length, string output, CancellationToken cancellationToken = default)
    {
        var total = FramesIn(length);
        var sources = new List<Source>();
        try
        {
            foreach (var (piece, path) in pieces)
            {
                var stream = File.OpenRead(path);
                try
                {
                    var (dataAt, frames) = ReadHeader(stream, path);
                    sources.Add(new Source(stream, dataAt, FramesIn(piece.At), Math.Min(frames, FramesIn(piece.Length)), piece.Gain));
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }

            using var file = File.Create(output);
            WriteHeader(file, total);
            var mixed = new float[BlockFrames * Channels];
            var bytes = new byte[BlockFrames * BytesPerFrame];
            var read = new byte[BlockFrames * BytesPerFrame];

            for (var block = 0L; block < total; block += BlockFrames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(BlockFrames, total - block);
                Array.Clear(mixed);

                foreach (var source in sources)
                {
                    var from = Math.Max(block, source.Start);
                    var to = Math.Min(block + count, source.Start + source.Frames);
                    if (to <= from)
                    {
                        continue;
                    }

                    var want = (int)(to - from);
                    source.Stream.Position = source.DataAt + ((from - source.Start) * BytesPerFrame);
                    source.Stream.ReadExactly(read, 0, want * BytesPerFrame);

                    for (var frame = 0; frame < want; frame++)
                    {
                        var gain = (float)source.Gain(TimeSpan.FromSeconds((double)(from - source.Start + frame) / Rate));
                        var into = (int)(from - block + frame) * Channels;
                        for (var channel = 0; channel < Channels; channel++)
                        {
                            mixed[into + channel] += gain * BinaryPrimitives.ReadInt16LittleEndian(read.AsSpan(((frame * Channels) + channel) * 2));
                        }
                    }
                }

                for (var sample = 0; sample < count * Channels; sample++)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(sample * 2), (short)Math.Clamp(MathF.Round(mixed[sample]), short.MinValue, short.MaxValue));
                }

                file.Write(bytes, 0, count * BytesPerFrame);
            }
        }
        finally
        {
            foreach (var source in sources)
            {
                source.Stream.Dispose();
            }
        }
    }

    /// <summary>Whole frames in <paramref name="time"/>, rounded down, so the sound is never longer than the picture.</summary>
    public static long FramesIn(TimeSpan time) => Math.Max(0, (long)Math.Floor(time.TotalSeconds * Rate));

    public static void WriteHeader(Stream stream, long frames)
    {
        var data = frames * BytesPerFrame;
        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)(36 + data));
        "WAVEfmt "u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], Channels);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], Rate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], Rate * BytesPerFrame);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], BytesPerFrame);
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], (uint)data);
        stream.Write(header);
    }

    /// <summary>Where the sound starts in a WAV, and how many frames it has. Anything but 48 kHz 16-bit stereo is refused.</summary>
    private static (long DataAt, long Frames) ReadHeader(Stream stream, string path)
    {
        Span<byte> chunk = stackalloc byte[12];
        stream.ReadExactly(chunk);
        if (!chunk[..4].SequenceEqual("RIFF"u8) || !chunk[8..12].SequenceEqual("WAVE"u8))
        {
            throw new EncodeException($"The sound read from {Path.GetFileName(path)} wasn't a WAV file.");
        }

        var formatSeen = false;
        Span<byte> head = stackalloc byte[8];
        Span<byte> format = stackalloc byte[16];
        while (stream.Read(head) == 8)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
            if (head[..4].SequenceEqual("fmt "u8))
            {
                stream.ReadExactly(format);
                var kind = BinaryPrimitives.ReadInt16LittleEndian(format);
                var channels = BinaryPrimitives.ReadInt16LittleEndian(format[2..]);
                var rate = BinaryPrimitives.ReadInt32LittleEndian(format[4..]);
                var bits = BinaryPrimitives.ReadInt16LittleEndian(format[14..]);

                // WAVE_FORMAT_EXTENSIBLE (0xFFFE) carries plain PCM too.
                if (kind is not (1 or unchecked((short)0xFFFE)) || channels != Channels || rate != Rate || bits != 16)
                {
                    throw new EncodeException($"The sound read from {Path.GetFileName(path)} came out as {rate} Hz, {channels} channels, {bits} bits, which can't be mixed.");
                }

                formatSeen = true;
                stream.Position += size - 16 + (size % 2);
            }
            else if (head[..4].SequenceEqual("data"u8))
            {
                if (!formatSeen)
                {
                    break;
                }

                var dataAt = stream.Position;
                var frames = Math.Min(size, stream.Length - dataAt) / BytesPerFrame;
                return (dataAt, frames);
            }
            else
            {
                stream.Position += size + (size % 2);
            }
        }

        throw new EncodeException($"The sound read from {Path.GetFileName(path)} had no sound in it that could be read.");
    }

    private sealed record Source(FileStream Stream, long DataAt, long Start, long Frames, Func<TimeSpan, double> Gain);
}
