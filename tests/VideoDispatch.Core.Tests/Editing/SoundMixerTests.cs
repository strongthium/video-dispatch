using System.Buffers.Binary;
using VideoDispatch.Core.Editing;
using VideoDispatch.Core.Reencoding;

namespace VideoDispatch.Core.Tests.Editing;

/// <summary>Mixing WAV files made here, each one steady level, so what comes out says how each was placed and faded.</summary>
public sealed class SoundMixerTests : IDisposable
{
    private readonly string _folder = Path.Combine(AppContext.BaseDirectory, "mix-" + Guid.NewGuid().ToString("N"));

    public SoundMixerTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static TimeSpan Seconds(double seconds) => TimeSpan.FromSeconds(seconds);

    private string PathOf(string name) => Path.Combine(_folder, name);

    /// <summary>A WAV of <paramref name="seconds"/> at one level throughout.</summary>
    private string Steady(string name, double seconds, short level)
    {
        var path = PathOf(name);
        var frames = SoundMixer.FramesIn(Seconds(seconds));
        using var file = File.Create(path);
        SoundMixer.WriteHeader(file, frames);
        var frame = new byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(frame, level);
        BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(2), level);
        for (var each = 0L; each < frames; each++)
        {
            file.Write(frame);
        }

        return path;
    }

    /// <summary>The left channel's level at a moment of a mixed WAV, and how long it is.</summary>
    private static (Func<double, short> At, TimeSpan Length) Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.Equal("RIFF"u8.ToArray(), bytes[..4]);
        var frames = (bytes.Length - 44) / 4;
        return (seconds => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44 + ((int)(seconds * SoundMixer.Rate) * 4))), TimeSpan.FromSeconds(frames / (double)SoundMixer.Rate));
    }

    [Fact]
    public void Pieces_are_placed_where_they_go_and_added_together_for_exactly_as_long_as_the_edit()
    {
        var output = PathOf("mix.wav");
        SoundMixer.Mix(
            [
                (new SoundPiece("a", Seconds(0), Seconds(2), Seconds(0), _ => 1), Steady("a.wav", 2, 1000)),
                (new SoundPiece("b", Seconds(0), Seconds(5), Seconds(1.5), _ => 0.5), Steady("b.wav", 5, 2000)),
            ],
            Seconds(3),
            output);

        var (at, length) = Read(output);
        Assert.Equal(Seconds(3), length);
        Assert.Equal(1000, at(1));
        Assert.Equal(2000, at(1.75));
        Assert.Equal(1000, at(2.5));
    }

    [Fact]
    public void Each_moment_of_a_piece_is_at_its_gain_then()
    {
        var output = PathOf("mix.wav");
        SoundMixer.Mix([(new SoundPiece("a", Seconds(0), Seconds(2), Seconds(0), offset => offset.TotalSeconds / 2), Steady("a.wav", 2, 10000))], Seconds(2), output);

        var (at, _) = Read(output);
        Assert.Equal(0, at(0));
        Assert.Equal(5000, at(1));
        Assert.Equal(7500, at(1.5));
    }

    [Fact]
    public void Too_loud_together_is_held_at_the_loudest_there_is_rather_than_wrapping_round()
    {
        var output = PathOf("mix.wav");
        SoundMixer.Mix(
            [
                (new SoundPiece("a", Seconds(0), Seconds(1), Seconds(0), _ => 1), Steady("a.wav", 1, 30000)),
                (new SoundPiece("b", Seconds(0), Seconds(1), Seconds(0), _ => 1), Steady("b.wav", 1, 30000)),
            ],
            Seconds(1),
            output);

        Assert.Equal(short.MaxValue, Read(output).At(0.5));
    }

    [Fact]
    public void A_piece_with_less_sound_than_it_should_have_is_silence_after()
    {
        var output = PathOf("mix.wav");
        SoundMixer.Mix([(new SoundPiece("a", Seconds(0), Seconds(3), Seconds(0), _ => 1), Steady("a.wav", 1, 1000))], Seconds(3), output);

        var (at, length) = Read(output);
        Assert.Equal(Seconds(3), length);
        Assert.Equal(1000, at(0.5));
        Assert.Equal(0, at(2));
    }

    [Fact]
    public void Sound_in_another_form_is_refused_with_its_name()
    {
        var path = PathOf("mono.wav");
        using (var file = File.Create(path))
        {
            SoundMixer.WriteHeader(file, 10);
        }

        // Make it say one channel.
        var bytes = File.ReadAllBytes(path);
        bytes[22] = 1;
        File.WriteAllBytes(path, bytes);

        var problem = Assert.Throws<EncodeException>(() =>
            SoundMixer.Mix([(new SoundPiece("a", Seconds(0), Seconds(1), Seconds(0), _ => 1), path)], Seconds(1), PathOf("mix.wav")));

        Assert.Contains("mono.wav", problem.Message);
    }

    [Fact]
    public void An_edits_pieces_are_its_clips_and_sounds_with_silent_ones_left_out()
    {
        var project = EditProject.Empty
            .Insert(EditClip.Whole("lake.mp4", Seconds(10)))
            .Insert(EditClip.Whole("quiet.mp4", Seconds(4)) with { Volume = 0 })
            .Insert(EditClip.Whole("ridge.mp4", Seconds(4)))
            .AddSound(new EditSound("song.mp3", Seconds(60), Seconds(10), Seconds(60), Seconds(15)))
            .AddSound(new EditSound("late.mp3", Seconds(60), Seconds(0), Seconds(60), Seconds(30)))
            .AddSound(new EditSound("muted.mp3", Seconds(60), Seconds(0), Seconds(60), Seconds(0)) { Volume = 0 });

        var pieces = SoundMixer.PiecesOf(project);

        Assert.Equal(["lake.mp4", "ridge.mp4", "song.mp3"], pieces.Select(piece => piece.SourcePath));
        Assert.Equal(Seconds(14), pieces[1].At);

        // Only as much of the song as there's edit left for it.
        Assert.Equal((Seconds(10), Seconds(13)), (pieces[2].From, pieces[2].To));
    }

    [Fact]
    public async Task Mixing_an_edit_reads_each_piece_and_takes_no_sound_as_silence()
    {
        var project = EditProject.Empty
            .Insert(EditClip.Whole("lake.mp4", Seconds(1)))
            .Insert(EditClip.Whole("quiet.mp4", Seconds(1)));
        var steady = Steady("steady.wav", 1, 1200);
        var asked = new List<string>();

        await SoundMixer.MixAsync(project, PathOf("pieces"), PathOf("mix.wav"), (piece, path, _) =>
        {
            asked.Add(piece.SourcePath);
            if (piece.SourcePath == "quiet.mp4")
            {
                return Task.FromResult(false);
            }

            File.Copy(steady, path);
            return Task.FromResult(true);
        });

        var (at, length) = Read(PathOf("mix.wav"));
        Assert.Equal(["lake.mp4", "quiet.mp4"], asked);
        Assert.Equal(Seconds(2), length);
        Assert.Equal(1200, at(0.5));
        Assert.Equal(0, at(1.5));
    }
}
