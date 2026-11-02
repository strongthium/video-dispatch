using System.Buffers.Binary;
using System.Runtime.InteropServices;
using VideoDispatch.Core.Reencoding;

namespace VideoDispatch.Core.Editing;

/// <summary>A stretch of one file's sound, from <paramref name="From"/> to <paramref name="To"/> of its own time, heard from <paramref name="At"/> in the edit.</summary>
/// <param name="Gain">How loud it is at a time into the stretch, in the edit: 0 is silent, 1 as it was, and more is louder.</param>
public sealed record SoundPiece(string SourcePath, TimeSpan From, TimeSpan To, TimeSpan At, Func<TimeSpan, double> Gain)
{
    /// <summary>How much of its own time it plays in each second of the edit: 0.5 is slowed to half speed, and lower.</summary>
    public double Rate { get; init; } = 1;

    /// <summary>How its loudness is evened out before its gain.</summary>
    public SoundLevel Level { get; init; }

    /// <summary>How much of its file it uses.</summary>
    public TimeSpan Span => To - From;

    /// <summary>How long it's heard for in the edit.</summary>
    public TimeSpan Length => Span / Rate;
}

/// <summary>
/// Makes all of an edit's sound as one file: each clip's own sound and each sound laid over it, at their volumes, with
/// their fades, evened out and slowed down as asked. Both engines use it, so an edit sounds the same whichever saves it.
/// Windows' tools can only set one volume for a whole clip, of no more than as it was, which is why it's all done here.
/// What comes out goes through a limiter, so sound made louder than there's room for is held down rather than clipped.
/// </summary>
public static class SoundMixer
{
    public const int Rate = 48000;
    public const int Channels = 2;
    private const int BytesPerFrame = Channels * 2;
    private const int BlockFrames = Rate / 10;

    // How loud is worked out a millisecond apart, and filled in between.
    private const int GainEvery = 48;

    // The loudest the mix may be, and how quickly the limiter lets go once it's quieter again.
    private const float Ceiling = 0.97f * short.MaxValue;
    private const double LimiterLetsGo = 0.08;

    // Levelling aims a sound's loudest moment here, but never makes anything more than this many times louder, so
    // near-silence doesn't become loud hiss.
    private const float LevelledPeak = 0.95f * short.MaxValue;
    private const float MostLift = 20;

    // Evening out turns down whatever is louder than a tenth of full, by three quarters of how much louder it is.
    private const float EvenAbove = 0.1f * short.MaxValue;
    private const float EvenRatio = 4;
    private const double EvenCatches = 0.005;
    private const double EvenLetsGo = 0.15;
    private const int HistogramSize = 4096;

    /// <summary>The stretches of sound in <paramref name="project"/>. Anything silent throughout is left out.</summary>
    public static IReadOnlyList<SoundPiece> PiecesOf(EditProject project)
    {
        var pieces = new List<SoundPiece>();
        for (var index = 0; index < project.Clips.Count; index++)
        {
            var clip = project.Clips[index];
            if (clip.Volume <= 0)
            {
                continue;
            }

            var each = index;
            Func<TimeSpan, double> gain = offset => project.ClipGain(each, offset);

            // Kept at its own speed, its sound starts with the clip and stops at the clip's end or its own.
            var stretched = !clip.HasSpeed || clip.StretchSound != false;
            pieces.Add(stretched
                ? new SoundPiece(clip.SourcePath, clip.Start, clip.End, project.StartOf(index), gain) { Rate = clip.Speed, Level = clip.Level }
                : new SoundPiece(clip.SourcePath, clip.Start, clip.Start + (clip.Span < clip.Length ? clip.Span : clip.Length), project.StartOf(index), gain) { Level = clip.Level });
        }

        for (var index = 0; index < project.Sounds.Count; index++)
        {
            var sound = project.Sounds[index];
            var heard = project.HeardLengthOf(index);
            if (sound.Volume > 0 && heard > TimeSpan.Zero)
            {
                var each = index;
                pieces.Add(new SoundPiece(sound.SourcePath, sound.Start, sound.Start + heard, sound.At, offset => project.SoundGain(each, offset)) { Level = sound.Level });
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
    /// Writes the whole of a piece's stretch of its file to a path, as 48 kHz 16-bit stereo WAV at its own speed, and
    /// says whether there was any. A video with no sound is silence, not an error.
    /// </param>
    public static async Task MixAsync(EditProject project, string folder, string output, Func<SoundPiece, string, CancellationToken, Task<bool>> read, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(folder);
        var placed = new List<(SoundPiece Piece, string Path)>();
        var pieces = PiecesOf(project);
        for (var index = 0; index < pieces.Count; index++)
        {
            var piece = pieces[index];
            var path = Path.Combine(folder, $"sound-{index}.wav");
            if (!await read(piece, path, cancellationToken))
            {
                continue;
            }

            if (piece.Level != SoundLevel.AsRecorded)
            {
                var levelled = Path.Combine(folder, $"sound-{index}-levelled.wav");
                await Task.Run(() => LevelFile(path, levelled, piece.Level, cancellationToken), cancellationToken);
                path = levelled;
            }

            placed.Add((piece, path));
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
                    var heard = Math.Min(FramesIn(piece.Length), (long)Math.Floor(frames / piece.Rate));
                    sources.Add(new Source(stream, dataAt, frames, FramesIn(piece.At), heard, piece.Rate, piece.Gain));
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
            var read = new byte[((int)(BlockFrames * EditClip.FastestSpeed) + 4) * BytesPerFrame];
            var limiter = new Limiter();

            for (var block = 0L; block < total; block += BlockFrames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(BlockFrames, total - block);
                Array.Clear(mixed);

                foreach (var source in sources)
                {
                    var from = Math.Max(block, source.Start);
                    var to = Math.Min(block + count, source.Start + source.Heard);
                    if (to > from)
                    {
                        Add(source, from - source.Start, (int)(to - from), mixed.AsSpan((int)(from - block) * Channels), read);
                    }
                }

                limiter.Hold(mixed.AsSpan(0, count * Channels));
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

    /// <summary>
    /// Evens out a WAV's loudness into another. <see cref="SoundLevel.Loudest"/> turns the whole of it up or down so its
    /// loudest moment is just under full. <see cref="SoundLevel.Evened"/> first turns its loud parts down, then turns the
    /// whole up so all but its loudest hundredth is under full, holding that hundredth down: the turning down takes a few
    /// milliseconds to catch a sudden loud start, and those few milliseconds mustn't decide how loud the rest can be.
    /// </summary>
    public static void LevelFile(string input, string output, SoundLevel level, CancellationToken cancellationToken = default)
    {
        if (level != SoundLevel.Evened)
        {
            var peak = 0f;
            Process(input, null, samples =>
            {
                foreach (var sample in samples)
                {
                    peak = Math.Max(peak, Math.Abs(sample));
                }
            }, cancellationToken);

            Lift(input, output, level == SoundLevel.Loudest ? peak : 0, null, cancellationToken);
            return;
        }

        var evened = output + ".even.wav";
        try
        {
            Process(input, evened, new Compressor().Apply, cancellationToken);

            // How loud it is, setting aside its loudest hundredth.
            var counts = new long[HistogramSize];
            var total = 0L;
            Process(evened, null, samples =>
            {
                foreach (var sample in samples)
                {
                    counts[Math.Min(HistogramSize - 1, (int)(Math.Abs(sample) / (short.MaxValue + 1f) * HistogramSize))]++;
                }

                total += samples.Length;
            }, cancellationToken);

            var seen = 0L;
            var bin = 0;
            for (; bin < HistogramSize && seen < total * 0.99; bin++)
            {
                seen += counts[bin];
            }

            Lift(evened, output, bin * (short.MaxValue + 1f) / HistogramSize, new Limiter(LevelledPeak), cancellationToken);
        }
        finally
        {
            File.Delete(evened);
        }
    }

    /// <summary>Turns a WAV up or down so <paramref name="loud"/> becomes just under full, no more than <see cref="MostLift"/> times, and through a limiter if one is given.</summary>
    private static void Lift(string input, string output, float loud, Limiter? limiter, CancellationToken cancellationToken)
    {
        var lift = loud > 0 ? Math.Min(MostLift, LevelledPeak / loud) : 1;
        Process(input, output, samples =>
        {
            for (var each = 0; each < samples.Length; each++)
            {
                samples[each] *= lift;
            }

            limiter?.Hold(samples);
        }, cancellationToken);
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

    /// <summary>
    /// Adds <paramref name="count"/> frames of a source, from <paramref name="offset"/> frames into it in the edit, into
    /// <paramref name="into"/>. A source at another speed is read faster or slower, each frame of the edit taken from
    /// between the two of the file it falls between.
    /// </summary>
    private static void Add(Source source, long offset, int count, Span<float> into, byte[] read)
    {
        var first = (long)Math.Floor(offset * source.Rate);
        var last = Math.Min(source.Frames - 1, (long)Math.Ceiling((offset + count - 1) * source.Rate) + 1);
        var frames = (int)(last - first + 1);
        if (frames <= 0)
        {
            return;
        }

        source.Stream.Position = source.DataAt + (first * BytesPerFrame);
        source.Stream.ReadExactly(read, 0, frames * BytesPerFrame);
        var samples = MemoryMarshal.Cast<byte, short>(read.AsSpan(0, frames * BytesPerFrame));

        for (var step = 0; step < count; step += GainEvery)
        {
            var length = Math.Min(GainEvery, count - step);
            var gain = (float)source.Gain(TimeSpan.FromSeconds((double)(offset + step) / Rate));
            var next = (float)source.Gain(TimeSpan.FromSeconds((double)(offset + step + length) / Rate));
            var change = (next - gain) / length;
            for (var frame = 0; frame < length; frame++, gain += change)
            {
                var at = (step + frame) * Channels;
                if (source.Rate == 1)
                {
                    into[at] += gain * samples[at];
                    into[at + 1] += gain * samples[at + 1];
                    continue;
                }

                var position = ((offset + step + frame) * source.Rate) - first;
                var before = Math.Min((int)position, frames - 1);
                var after = Math.Min(before + 1, frames - 1);
                var between = (float)(position - before);
                into[at] += gain * ((samples[before * Channels] * (1 - between)) + (samples[after * Channels] * between));
                into[at + 1] += gain * ((samples[(before * Channels) + 1] * (1 - between)) + (samples[(after * Channels) + 1] * between));
            }
        }
    }

    /// <summary>Reads a WAV a block at a time, has <paramref name="change"/> see or change each block, and writes it out if asked.</summary>
    private static void Process(string input, string? output, SpanAction change, CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(input);
        var (dataAt, frames) = ReadHeader(stream, input);
        using var file = output is null ? null : File.Create(output);
        if (file is not null)
        {
            WriteHeader(file, frames);
        }

        stream.Position = dataAt;
        var bytes = new byte[BlockFrames * BytesPerFrame];
        var samples = new float[BlockFrames * Channels];
        for (var done = 0L; done < frames; done += BlockFrames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(BlockFrames, frames - done);
            stream.ReadExactly(bytes, 0, count * BytesPerFrame);
            var read = MemoryMarshal.Cast<byte, short>(bytes.AsSpan(0, count * BytesPerFrame));
            for (var each = 0; each < read.Length; each++)
            {
                samples[each] = read[each];
            }

            change(samples.AsSpan(0, read.Length));
            if (file is not null)
            {
                for (var each = 0; each < read.Length; each++)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(each * 2), (short)Math.Clamp(MathF.Round(samples[each]), short.MinValue, short.MaxValue));
                }

                file.Write(bytes, 0, count * BytesPerFrame);
            }
        }
    }

    private static float Smoothing(double seconds) => (float)Math.Exp(-1 / (seconds * Rate));

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

    private delegate void SpanAction(Span<float> samples);

    /// <param name="Frames">How many frames the file has.</param>
    /// <param name="Start">Where it starts in the edit, in frames.</param>
    /// <param name="Heard">How many frames of the edit it's heard for.</param>
    private sealed record Source(FileStream Stream, long DataAt, long Frames, long Start, long Heard, double Rate, Func<TimeSpan, double> Gain);

    /// <summary>
    /// Holds the mix under <see cref="Ceiling"/>: it turns down at once for anything louder, so nothing gets past, and
    /// back up gradually, so the turning isn't heard as a flutter.
    /// </summary>
    private sealed class Limiter(float ceiling = Ceiling)
    {
        private readonly float _letsGo = Smoothing(LimiterLetsGo);
        private float _envelope;

        public void Hold(Span<float> samples)
        {
            for (var at = 0; at < samples.Length; at += Channels)
            {
                var peak = Math.Max(Math.Abs(samples[at]), Math.Abs(samples[at + 1]));
                _envelope = Math.Max(peak, _envelope * _letsGo);
                if (_envelope > ceiling)
                {
                    var gain = ceiling / _envelope;
                    samples[at] *= gain;
                    samples[at + 1] *= gain;
                }
            }
        }
    }

    /// <summary>Turns loud parts down, following how loud the sound is over the last few milliseconds.</summary>
    private sealed class Compressor
    {
        private readonly float _catches = Smoothing(EvenCatches);
        private readonly float _letsGo = Smoothing(EvenLetsGo);
        private float _envelope;

        public void Apply(Span<float> samples)
        {
            for (var at = 0; at < samples.Length; at += Channels)
            {
                var peak = Math.Max(Math.Abs(samples[at]), Math.Abs(samples[at + 1]));
                var smoothing = peak > _envelope ? _catches : _letsGo;
                _envelope = (smoothing * _envelope) + ((1 - smoothing) * peak);
                if (_envelope > EvenAbove)
                {
                    var gain = EvenAbove * MathF.Pow(_envelope / EvenAbove, 1 / EvenRatio) / _envelope;
                    samples[at] *= gain;
                    samples[at + 1] *= gain;
                }
            }
        }
    }
}
