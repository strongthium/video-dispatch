using VideoDispatch.Core.Editing;
using VideoDispatch.Core.Media;
using VideoDispatch.Core.Reencoding;
using VideoDispatch.Editing;
using Windows.UI;

namespace VideoDispatch.Tests.Reencoding;

/// <summary>
/// Windows' own tools saving real edits of videos they first make here. Each second of a test video is its own colour,
/// so a picture from the result says which part of which video it came from. Editions of Windows without the media
/// tools skip these.
/// </summary>
public sealed class WindowsEditTests : IDisposable
{
    private static readonly Color Red = Color.FromArgb(255, 255, 0, 0);
    private static readonly Color Green = Color.FromArgb(255, 0, 255, 0);
    private static readonly Color Blue = Color.FromArgb(255, 0, 0, 255);
    private static readonly Color Yellow = Color.FromArgb(255, 255, 255, 0);
    private static readonly Color Magenta = Color.FromArgb(255, 255, 0, 255);

    private readonly string _folder = Path.Combine(AppContext.BaseDirectory, "windows-edit-" + Guid.NewGuid().ToString("N"));
    private readonly WindowsVideoEncoder _encoder = new();

    public WindowsEditTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static TimeSpan Seconds(double seconds) => TimeSpan.FromSeconds(seconds);

    private string PathOf(string name) => Path.Combine(_folder, name);

    private async Task<string> MakeAsync(string name, int width, int height, bool withSound, params Color[] colours)
    {
        await WindowsVideoEncoderTests.SkipWithoutMediaToolsAsync();
        return await TestVideos.MakeSecondsAsync(_folder, name, width, height, withSound, colours);
    }

    private EditWork Work(params string[] pictures) => new(PathOf("work-" + Guid.NewGuid().ToString("N")), pictures);

    private async Task<string> SaveAsync(EditProject project, string name = "edit.mp4", params string[] pictures)
    {
        var output = PathOf(name);
        await _encoder.RenderAsync(project, Work(pictures), output, EncodePlan.ForEdit(Mp4MetadataReader.Read(project.Clips[0].SourcePath), project.Length));
        return output;
    }

    /// <summary>The middle of a frame, as red, green and blue from 0 to 255.</summary>
    private static async Task<(int Red, int Green, int Blue)> MiddleAsync(string path, double seconds)
    {
        var frame = await WindowsVideoEncoderTests.ReadFrameAsync(path, seconds);
        var at = (((frame.Height / 2) * frame.Width) + (frame.Width / 2)) * 4;
        return (frame.Pixels[at + 2], frame.Pixels[at + 1], frame.Pixels[at]);
    }

    private static async Task<string> ColourAtAsync(string path, double seconds) =>
        (await WindowsVideoEncoderTests.ReadFrameAsync(path, seconds)).ColourInTheMiddle;

    [Fact]
    public async Task Cut_and_reordered_clips_play_in_their_new_order_for_as_long_as_they_should()
    {
        var source = await MakeAsync("colours.mp4", 640, 360, true, Red, Green, Blue, Yellow, Magenta);

        // The last two seconds, then the first.
        var whole = EditClip.Whole(source, Seconds(5));
        var project = EditProject.Empty.Insert(whole).SplitAt(Seconds(1))!.SplitAt(Seconds(3))!.Remove(1).Move(1, 0);
        Assert.Equal([Seconds(3), Seconds(0)], project.Clips.Select(clip => clip.Start));

        var output = await SaveAsync(project);

        var read = Mp4MetadataReader.Read(output)!;
        Assert.Equal(3, read.Duration!.Value.TotalSeconds, 0.2);
        Assert.Equal(640, read.DisplayWidth);
        Assert.Equal(360, read.DisplayHeight);
        Assert.Equal("yellow", await ColourAtAsync(output, 0.5));
        Assert.Equal("magenta", await ColourAtAsync(output, 1.5));
        Assert.Equal("red", await ColourAtAsync(output, 2.5));
        Assert.Equal(1, await WindowsVideoEncoderTests.AudioTracksInAsync(output));

        // One every two seconds, so players can jump about in it.
        Assert.InRange(KeyFramesIn(output), 2, 10);
    }

    /// <summary>How many key frames the video track lists. A track that lists none has nothing but key frames.</summary>
    private static int KeyFramesIn(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var lists = new List<int>();
        for (var i = 4; i < bytes.Length - 12; i++)
        {
            if (bytes[i] == 's' && bytes[i + 1] == 't' && bytes[i + 2] == 's' && bytes[i + 3] == 's')
            {
                lists.Add(System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(i + 8)));
            }
        }

        return Assert.Single(lists);
    }

    [Fact]
    public async Task Clips_from_different_videos_are_joined()
    {
        var first = await MakeAsync("first.mp4", 640, 360, true, Red, Green);
        var second = await MakeAsync("second.mp4", 640, 360, true, Blue, Yellow);
        var project = EditProject.Empty
            .Insert(new EditClip(first, Seconds(2), Seconds(1), Seconds(2)))
            .Insert(new EditClip(second, Seconds(2), Seconds(0), Seconds(1)));

        var output = await SaveAsync(project);

        Assert.Equal(2, Mp4MetadataReader.Read(output)!.Duration!.Value.TotalSeconds, 0.2);
        Assert.Equal("green", await ColourAtAsync(output, 0.5));
        Assert.Equal("blue", await ColourAtAsync(output, 1.5));
    }

    [Fact]
    public async Task A_clip_of_another_shape_fits_inside_with_black_bars()
    {
        var landscape = await MakeAsync("landscape.mp4", 640, 360, true, Red);
        var tall = await MakeAsync("tall.mp4", 360, 640, true, Blue);
        var project = EditProject.Empty.Insert(EditClip.Whole(landscape, Seconds(1))).Insert(EditClip.Whole(tall, Seconds(1)));

        var output = await SaveAsync(project);

        var frame = await WindowsVideoEncoderTests.ReadFrameAsync(output, 1.5);
        Assert.Equal(640, frame.Width);
        Assert.Equal(360, frame.Height);
        Assert.Equal("blue", frame.ColourInTheMiddle);
        Assert.Equal("black", frame.ColourAt(20, frame.Height / 2));
        Assert.Equal("black", frame.ColourAt(frame.Width - 20, frame.Height / 2));
    }

    [Fact]
    public async Task A_sideways_phone_video_stays_the_right_way_up()
    {
        // Stored landscape with red on the left, and marked to be shown turned a quarter clockwise: red on top.
        await WindowsVideoEncoderTests.SkipWithoutMediaToolsAsync();
        var source = await TestVideos.MakeAsync(_folder, "phone.mp4", 640, 360);
        WindowsVideoEncoderTests.MarkRotatedClockwise(source);

        var output = await SaveAsync(EditProject.Empty.Insert(new EditClip(source, Seconds(2), Seconds(0.5), Seconds(1.5))));

        var read = Mp4MetadataReader.Read(output)!;
        Assert.Equal(360, read.DisplayWidth);
        Assert.Equal(640, read.DisplayHeight);
        var frame = await WindowsVideoEncoderTests.ReadFrameAsync(output, 0.5);
        Assert.True(frame.IsRed(frame.Width / 2, frame.Height / 4), "The top should be red.");
        Assert.False(frame.IsRed(frame.Width / 2, frame.Height * 3 / 4), "The bottom should be blue.");
    }

    [Fact]
    public async Task A_clip_without_sound_can_go_beside_one_with_it()
    {
        var quiet = await MakeAsync("quiet.mp4", 640, 360, false, Red);
        var loud = await MakeAsync("loud.mp4", 640, 360, true, Green);
        var project = EditProject.Empty.Insert(EditClip.Whole(quiet, Seconds(1))).Insert(EditClip.Whole(loud, Seconds(1)));

        var output = await SaveAsync(project);

        Assert.Equal(2, Mp4MetadataReader.Read(output)!.Duration!.Value.TotalSeconds, 0.2);
        Assert.Equal("red", await ColourAtAsync(output, 0.5));
        Assert.Equal("green", await ColourAtAsync(output, 1.5));
    }

    [Fact]
    public async Task Says_how_far_along_it_is_and_can_be_stopped()
    {
        var source = await MakeAsync("colours.mp4", 640, 360, true, Red, Green, Blue);
        var project = EditProject.Empty.Insert(EditClip.Whole(source, Seconds(3)));
        var seen = new List<double>();

        await _encoder.RenderAsync(project, Work(), PathOf("edit.mp4"), EncodePlan.ForEdit(null, project.Length) with { Width = 640, Height = 360 }, new Reporter(seen));
        Assert.NotEmpty(seen);
        Assert.All(seen, percent => Assert.InRange(percent, 0, 100));

        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _encoder.RenderAsync(project, Work(), PathOf("stopped.mp4"), EncodePlan.ForEdit(null, project.Length), null, stop.Token));
    }

    [Fact]
    public async Task A_missing_video_says_so()
    {
        await WindowsVideoEncoderTests.SkipWithoutMediaToolsAsync();
        var project = EditProject.Empty.Insert(EditClip.Whole(PathOf("gone.mp4"), Seconds(3)));

        var problem = await Assert.ThrowsAsync<EncodeException>(() => _encoder.RenderAsync(project, Work(), PathOf("edit.mp4"), EncodePlan.ForEdit(null, project.Length)));

        Assert.Contains("missing", problem.Message);
    }

    [Fact]
    public async Task A_zoomed_clip_shows_only_the_part_in_view_filling_the_frame()
    {
        // Red on the left half, blue on the right: zoomed into the left half, it's all red.
        await WindowsVideoEncoderTests.SkipWithoutMediaToolsAsync();
        var source = await TestVideos.MakeAsync(_folder, "halves.mp4", 640, 360);
        var project = EditProject.Empty.Insert(EditClip.Whole(source, Seconds(2)) with { Zoom = 2, ZoomX = 0.25, ZoomY = 0.5 });

        var output = await SaveAsync(project);

        var frame = await WindowsVideoEncoderTests.ReadFrameAsync(output, 1);
        Assert.Equal((640, 360), (frame.Width, frame.Height));
        Assert.Equal("red", frame.ColourAt(20, frame.Height / 2));
        Assert.Equal("red", frame.ColourAt(frame.Width - 20, frame.Height / 2));
        Assert.Equal(2, Mp4MetadataReader.Read(output)!.Duration!.Value.TotalSeconds, 0.2);
    }

    [Fact]
    public async Task A_zoomed_sideways_phone_video_is_zoomed_the_right_way_up()
    {
        // Shown upright, red is the top half: zoomed into the bottom half, it's all blue.
        await WindowsVideoEncoderTests.SkipWithoutMediaToolsAsync();
        var source = await TestVideos.MakeAsync(_folder, "phone.mp4", 640, 360);
        WindowsVideoEncoderTests.MarkRotatedClockwise(source);
        var project = EditProject.Empty.Insert(EditClip.Whole(source, Seconds(2)) with { Zoom = 2, ZoomX = 0.5, ZoomY = 0.75 });

        var output = await SaveAsync(project);

        var frame = await WindowsVideoEncoderTests.ReadFrameAsync(output, 1);
        Assert.Equal((360, 640), (frame.Width, frame.Height));
        Assert.Equal("blue", frame.ColourAt(frame.Width / 2, 20));
        Assert.Equal("blue", frame.ColourAt(frame.Width / 2, frame.Height - 20));
    }

    [Fact]
    public async Task Fades_come_up_from_black_and_go_down_to_it()
    {
        var source = await MakeAsync("red.mp4", 640, 360, true, Red, Red, Red);
        var project = EditProject.Empty.Insert(EditClip.Whole(source, Seconds(3)) with { FadeIn = Seconds(1), FadeOut = Seconds(1) });

        var output = await SaveAsync(project);

        Assert.InRange((await MiddleAsync(output, 0.05)).Red, 0, 60);
        Assert.InRange((await MiddleAsync(output, 0.5)).Red, 80, 180);
        Assert.InRange((await MiddleAsync(output, 1.5)).Red, 220, 255);
        Assert.InRange((await MiddleAsync(output, 2.95)).Red, 0, 60);
        Assert.Equal(3, Mp4MetadataReader.Read(output)!.Duration!.Value.TotalSeconds, 0.2);
    }

    [Fact]
    public async Task A_crossfade_shows_both_clips_at_once_where_they_overlap_and_shortens_the_edit()
    {
        var red = await MakeAsync("red.mp4", 640, 360, true, Red, Red);
        var green = await MakeAsync("green.mp4", 640, 360, true, Green, Green);
        var project = EditProject.Empty
            .Insert(EditClip.Whole(red, Seconds(2)))
            .Insert(EditClip.Whole(green, Seconds(2)) with { Crossfade = Seconds(1) });

        var output = await SaveAsync(project);

        Assert.Equal(3, Mp4MetadataReader.Read(output)!.Duration!.Value.TotalSeconds, 0.2);
        Assert.Equal("red", await ColourAtAsync(output, 0.5));
        var middle = await MiddleAsync(output, 1.5);
        Assert.InRange(middle.Red, 70, 190);
        Assert.InRange(middle.Green, 70, 190);
        Assert.Equal("green", await ColourAtAsync(output, 2.5));
    }

    [Fact]
    public async Task Words_show_over_the_picture_only_while_they_should()
    {
        var source = await MakeAsync("blue.mp4", 640, 360, true, Blue, Blue);
        var picture = PathOf("words.png");
        WriteCorner(picture, 640, 360);
        var project = EditProject.Empty
            .Insert(EditClip.Whole(source, Seconds(2)))
            .AddText(new EditText("in the corner", Seconds(0.5), Seconds(1)));

        var output = await SaveAsync(project, "edit.mp4", picture);

        foreach (var (seconds, corner) in new[] { (0.25, "blue"), (1.0, "red"), (1.75, "blue") })
        {
            var frame = await WindowsVideoEncoderTests.ReadFrameAsync(output, seconds);
            Assert.Equal(corner, frame.ColourAt(40, 40));
            Assert.Equal("blue", frame.ColourInTheMiddle);
        }
    }

    [Fact]
    public async Task A_muted_clips_sound_is_gone_and_a_sound_laid_over_it_is_heard_where_it_was_put()
    {
        await WindowsVideoEncoderTests.SkipWithoutMediaToolsAsync();
        var clicking = await MakeClickingAsync("clicking.mp4", clickAt: 1.0, seconds: 3);
        var song = PathOf("song.wav");
        WriteClick(song, clickAt: 0.5, seconds: 2);
        var project = EditProject.Empty
            .Insert(EditClip.Whole(clicking, Seconds(3)) with { Volume = 0 })
            .AddSound(new EditSound(song, Seconds(2), TimeSpan.Zero, Seconds(2), Seconds(1.25)));

        var output = await SaveAsync(project);

        var (loudestAt, _) = await LoudestAsync(output);
        Assert.Equal(1.75, loudestAt, 0.05);
        Assert.Equal(3, Mp4MetadataReader.Read(output)!.Duration!.Value.TotalSeconds, 0.2);
    }

    [Fact]
    public async Task A_clips_own_sound_stays_with_its_picture_and_a_fade_brings_it_down()
    {
        await WindowsVideoEncoderTests.SkipWithoutMediaToolsAsync();
        var clicking = await MakeClickingAsync("clicking.mp4", clickAt: 1.0, seconds: 3);

        // Cut from half a second in, so the click is at half a second.
        var plain = await SaveAsync(EditProject.Empty.Insert(new EditClip(clicking, Seconds(3), Seconds(0.5), Seconds(3))), "plain.mp4");
        var (at, level) = await LoudestAsync(plain);
        Assert.Equal(0.5, at, 0.05);

        // Two thirds of the way through a fade in, it's about two thirds as loud.
        var faded = await SaveAsync(EditProject.Empty.Insert(new EditClip(clicking, Seconds(3), Seconds(0), Seconds(3)) with { FadeIn = Seconds(1.5) }), "faded.mp4");
        var (fadedAt, fadedLevel) = await LoudestAsync(faded);
        Assert.Equal(1.0, fadedAt, 0.05);
        Assert.InRange(fadedLevel / (double)level, 0.5, 0.85);
    }

    [Fact]
    public async Task A_sound_longer_than_the_edit_doesnt_make_it_longer()
    {
        var source = await MakeAsync("short.mp4", 640, 360, true, Red, Green);
        var song = PathOf("long.wav");
        WriteClick(song, clickAt: 4, seconds: 6);
        var project = EditProject.Empty
            .Insert(EditClip.Whole(source, Seconds(2)))
            .AddSound(new EditSound(song, Seconds(6), TimeSpan.Zero, Seconds(6), Seconds(0.5)));

        var output = await SaveAsync(project);

        Assert.Equal(2, Mp4MetadataReader.Read(output)!.Duration!.Value.TotalSeconds, 0.2);
    }

    [Fact]
    public void A_fade_is_made_of_short_even_steps_that_cover_it()
    {
        var steps = WindowsVideoEncoder.Steps(Seconds(1), rising: true).ToList();

        Assert.Equal(12, steps.Count);
        Assert.Equal(TimeSpan.Zero, steps[0].From);
        Assert.Equal(Seconds(1), steps[^1].To);
        Assert.All(steps.Zip(steps.Skip(1)), pair => Assert.Equal(pair.First.To, pair.Second.From));
        Assert.True(steps[0].Opacity < steps[^1].Opacity);
        Assert.Equal(1 - steps[0].Opacity, WindowsVideoEncoder.Steps(Seconds(1), rising: false).First().Opacity, 6);
        Assert.Equal(3, WindowsVideoEncoder.Steps(Seconds(0.05), rising: true).Count());
        Assert.Empty(WindowsVideoEncoder.Steps(TimeSpan.Zero, rising: true));
    }

    /// <summary>A video whose sound is a quiet hum with one loud click, so where the click ends up says where its sound went.</summary>
    private async Task<string> MakeClickingAsync(string name, double clickAt, int seconds)
    {
        var sound = PathOf(name + ".wav");
        WriteClick(sound, clickAt, seconds);
        var composition = new Windows.Media.Editing.MediaComposition();
        composition.Clips.Add(Windows.Media.Editing.MediaClip.CreateFromColor(Blue, Seconds(seconds)));
        composition.BackgroundAudioTracks.Add(await Windows.Media.Editing.BackgroundAudioTrack.CreateFromFileAsync(await Windows.Storage.StorageFile.GetFileFromPathAsync(sound)));
        var profile = Windows.Media.MediaProperties.MediaEncodingProfile.CreateMp4(Windows.Media.MediaProperties.VideoEncodingQuality.HD720p);
        profile.Video.Width = 640;
        profile.Video.Height = 360;
        WindowsVideoEncoder.AskForKeyFrames(profile, 1);
        var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(_folder);
        var file = await folder.CreateFileAsync(name, Windows.Storage.CreationCollisionOption.ReplaceExisting);
        await composition.RenderToFileAsync(file, Windows.Media.Editing.MediaTrimmingPreference.Precise, profile);
        return file.Path;
    }

    private static void WriteClick(string path, double clickAt, int seconds)
    {
        var frames = SoundMixer.Rate * seconds;
        var click = (int)(clickAt * SoundMixer.Rate);
        using var file = File.Create(path);
        SoundMixer.WriteHeader(file, frames);
        var sample = new byte[4];
        for (var frame = 0; frame < frames; frame++)
        {
            var value = (short)(frame >= click && frame < click + 480
                ? Math.Sin(frame * 2 * Math.PI * 1000 / SoundMixer.Rate) * 30000
                : Math.Sin(frame * 2 * Math.PI * 200 / SoundMixer.Rate) * 1500);
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(sample, value);
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(sample.AsSpan(2), value);
            file.Write(sample);
        }
    }

    /// <summary>When the loudest moment of a video's sound is, and how loud.</summary>
    private async Task<(double Seconds, int Level)> LoudestAsync(string video)
    {
        var wav = PathOf(Path.GetFileNameWithoutExtension(video) + "-heard.wav");
        await File.WriteAllBytesAsync(wav, []);
        var profile = Windows.Media.MediaProperties.MediaEncodingProfile.CreateWav(Windows.Media.MediaProperties.AudioEncodingQuality.High);
        profile.Audio = Windows.Media.MediaProperties.AudioEncodingProperties.CreatePcm(SoundMixer.Rate, 2, 16);
        var prepared = await new Windows.Media.Transcoding.MediaTranscoder().PrepareFileTranscodeAsync(
            await Windows.Storage.StorageFile.GetFileFromPathAsync(video), await Windows.Storage.StorageFile.GetFileFromPathAsync(wav), profile);
        await prepared.TranscodeAsync();

        var bytes = File.ReadAllBytes(wav);
        var data = System.Text.Encoding.ASCII.GetString(bytes).IndexOf("data", StringComparison.Ordinal) + 8;
        var loudest = 0;
        var loudestAt = 0;
        for (var at = data; at + 4 <= bytes.Length; at += 4)
        {
            var level = Math.Abs((int)System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(at)));
            if (level > loudest)
            {
                loudest = level;
                loudestAt = (at - data) / 4;
            }
        }

        return (loudestAt / (double)SoundMixer.Rate, loudest);
    }

    /// <summary>A picture the size of the frame, see-through but for a red square in its top left corner.</summary>
    private static void WriteCorner(string path, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < 100; y++)
        {
            for (var x = 0; x < 100; x++)
            {
                var at = ((y * width) + x) * 4;
                pixels[at + 2] = 255;
                pixels[at + 3] = 255;
            }
        }

        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    [Fact]
    public async Task Reads_a_videos_length_and_the_picture_at_a_moment()
    {
        var source = await MakeAsync("colours.mp4", 640, 360, false, Red, Green, Blue);

        Assert.Equal(3, (await WindowsVideoReader.ReadLengthAsync(source))!.Value.TotalSeconds, 0.1);

        var picture = await WindowsVideoReader.ReadFrameAsync(source, Seconds(1.5), 90);
        Assert.NotNull(picture);
        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(new MemoryStream(picture), System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        Assert.Equal(90, decoder.Frames[0].PixelHeight);
        Assert.Equal(160, decoder.Frames[0].PixelWidth);
    }

    [Fact]
    public async Task Something_that_is_not_a_video_reads_as_nothing()
    {
        var notVideo = PathOf("not-a-video.mp4");
        await File.WriteAllTextAsync(notVideo, "This isn't a video.");

        Assert.Null(await WindowsVideoReader.ReadLengthAsync(notVideo));
        Assert.Null(await WindowsVideoReader.ReadFrameAsync(notVideo, TimeSpan.Zero, 90));
        Assert.Null(await WindowsVideoReader.ReadLengthAsync(PathOf("gone.mp4")));
    }

    private sealed class Reporter(List<double> seen) : IProgress<double>
    {
        public void Report(double value)
        {
            lock (seen)
            {
                seen.Add(value);
            }
        }
    }
}
