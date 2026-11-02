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

    private async Task<string> SaveAsync(EditProject project, string name = "edit.mp4")
    {
        var output = PathOf(name);
        await _encoder.RenderAsync(project, output, EncodePlan.ForEdit(Mp4MetadataReader.Read(project.Clips[0].SourcePath), project.Length));
        return output;
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

        await _encoder.RenderAsync(project, PathOf("edit.mp4"), EncodePlan.ForEdit(null, project.Length) with { Width = 640, Height = 360 }, new Reporter(seen));
        Assert.NotEmpty(seen);
        Assert.All(seen, percent => Assert.InRange(percent, 0, 100));

        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _encoder.RenderAsync(project, PathOf("stopped.mp4"), EncodePlan.ForEdit(null, project.Length), null, stop.Token));
    }

    [Fact]
    public async Task A_missing_video_says_so()
    {
        await WindowsVideoEncoderTests.SkipWithoutMediaToolsAsync();
        var project = EditProject.Empty.Insert(EditClip.Whole(PathOf("gone.mp4"), Seconds(3)));

        var problem = await Assert.ThrowsAsync<EncodeException>(() => _encoder.RenderAsync(project, PathOf("edit.mp4"), EncodePlan.ForEdit(null, project.Length)));

        Assert.Contains("missing", problem.Message);
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
