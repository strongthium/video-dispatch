using System.Runtime.InteropServices;
using VideoDispatch.Core.Media;
using VideoDispatch.Core.Reencoding;
using VideoDispatch.Editing;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI;

namespace VideoDispatch.Tests.Reencoding;

/// <summary>Real videos, made by Windows' own tools.</summary>
internal static class TestVideos
{
    /// <summary>Two seconds of blue with red on its left half, and with sound, so a picture turned the wrong way can be seen.</summary>
    public static async Task<string> MakeAsync(string folderPath, string name, int width, int height, int framesPerSecond = 30)
    {
        var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
        var composition = new MediaComposition();
        composition.Clips.Add(MediaClip.CreateFromColor(Color.FromArgb(255, 0, 0, 255), TimeSpan.FromSeconds(2)));

        var layer = new MediaOverlayLayer();
        layer.Overlays.Add(new MediaOverlay(MediaClip.CreateFromColor(Color.FromArgb(255, 255, 0, 0), TimeSpan.FromSeconds(2)), new Rect(0, 0, width / 2, height), 1));
        composition.OverlayLayers.Add(layer);
        composition.BackgroundAudioTracks.Add(await BackgroundAudioTrack.CreateFromFileAsync(await MakeToneAsync(folderPath)));

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD720p);
        profile.Video.Width = (uint)width;
        profile.Video.Height = (uint)height;
        profile.Video.FrameRate.Numerator = (uint)framesPerSecond;
        profile.Video.FrameRate.Denominator = 1;

        var file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);

        // Some editions of Windows, and perhaps build servers, have no media tools.
        try
        {
            var result = await composition.RenderToFileAsync(file, MediaTrimmingPreference.Precise, profile);
            if (result != TranscodeFailureReason.None)
            {
                Assert.Skip($"Windows' media tools couldn't make a test video here: {result}.");
            }
        }
        catch (COMException exception)
        {
            Assert.Skip($"Windows' media tools aren't available here: {exception.HResult:X8}.");
        }

        return file.Path;
    }

    /// <summary>Exactly as long as the video, because a longer audio layer makes a file that misreports its length.</summary>
    private static async Task<StorageFile> MakeToneAsync(string folderPath)
    {
        const int Rate = 44100;
        var path = Path.Combine(folderPath, "tone.wav");
        var samples = Rate * 2;

        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + (samples * 2));
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(Rate);
            writer.Write(Rate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(samples * 2);
            for (var i = 0; i < samples; i++)
            {
                writer.Write((short)(Math.Sin(i * 2 * Math.PI * 440 / Rate) * 12000));
            }
        }

        return await StorageFile.GetFileFromPathAsync(path);
    }
}

/// <summary>
/// Windows' own tools, really encoding videos that they first make here. These are flat colours, so they say nothing
/// about how good real footage looks, or how close a real file comes to a size. Editions of Windows without the media
/// tools skip these.
/// </summary>
public sealed class WindowsVideoEncoderTests : IDisposable
{
    private static readonly Color Red = Color.FromArgb(255, 255, 0, 0);
    private static readonly Color Blue = Color.FromArgb(255, 0, 0, 255);

    private readonly string _folder = Path.Combine(AppContext.BaseDirectory, "windows-encoder-" + Guid.NewGuid().ToString("N"));
    private readonly WindowsVideoEncoder _encoder = new();

    public WindowsVideoEncoderTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task Keeps_the_picture_size_frame_rate_length_and_sound()
    {
        var source = await MakeVideoAsync("source.mp4", 1280, 720, framesPerSecond: 60);
        var output = PathOf("output.mp4");

        var plan = new EncodePlan(1280, 720, 60, 2_000_000, 128_000, null, TimeSpan.FromSeconds(2));
        await _encoder.EncodeAsync(source, output, plan);

        var read = Mp4MetadataReader.Read(output);
        Assert.NotNull(read);
        Assert.Equal(1280, read.DisplayWidth);
        Assert.Equal(720, read.DisplayHeight);
        Assert.Equal(60, read.FramesPerSecond!.Value, 0.5);
        Assert.Equal(2, read.Duration!.Value.TotalSeconds, 0.2);
        Assert.Equal("avc1", read.CodecTag);
        Assert.Equal(1, await AudioTracksInAsync(output));
    }

    [Fact]
    public async Task Makes_the_picture_smaller_when_asked()
    {
        var source = await MakeVideoAsync("source.mp4", 1920, 1080);
        var output = PathOf("output.mp4");

        await _encoder.EncodeAsync(source, output, new EncodePlan(1280, 720, 30, 1_000_000, 96_000, null, TimeSpan.FromSeconds(2)));

        var read = Mp4MetadataReader.Read(output);
        Assert.Equal(1280, read!.DisplayWidth);
        Assert.Equal(720, read.DisplayHeight);
    }

    [Fact]
    public async Task A_sideways_phone_video_stays_the_right_way_up()
    {
        // Stored landscape with red on the left, and marked to be shown turned a quarter clockwise, as phones do: red on top.
        var source = await MakeVideoAsync("source.mp4", 640, 360);
        MarkRotatedClockwise(source);
        var metadata = Mp4MetadataReader.Read(source);
        Assert.Equal(360, metadata!.DisplayWidth);
        Assert.Equal(640, metadata.DisplayHeight);

        var output = PathOf("output.mp4");
        await _encoder.EncodeAsync(source, output, EncodePlan.For(new EncodeSettings(EncodeGoal.HighQuality), metadata, new FileInfo(source).Length));

        var read = Mp4MetadataReader.Read(output);
        Assert.Equal(360, read!.DisplayWidth);
        Assert.Equal(640, read.DisplayHeight);

        var frame = await ReadFrameAsync(output);
        Assert.True(frame.IsRed(frame.Width / 2, frame.Height / 4), "The top should be red.");
        Assert.False(frame.IsRed(frame.Width / 2, frame.Height * 3 / 4), "The bottom should be blue.");
    }

    [Fact]
    public async Task Says_how_far_along_it_is()
    {
        var source = await MakeVideoAsync("source.mp4", 640, 360);
        var seen = new List<double>();

        await _encoder.EncodeAsync(source, PathOf("output.mp4"), new EncodePlan(640, 360, 30, 500_000, 96_000, null, TimeSpan.FromSeconds(2)), new Reporter(seen));

        Assert.NotEmpty(seen);
        Assert.All(seen, percent => Assert.InRange(percent, 0, 100));
    }

    [Fact]
    public async Task Can_be_stopped()
    {
        var source = await MakeVideoAsync("source.mp4", 640, 360);
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _encoder.EncodeAsync(source, PathOf("output.mp4"), new EncodePlan(640, 360, 30, 500_000, 96_000, null, null), null, stop.Token));
    }

    [Fact]
    public async Task A_file_that_is_not_a_video_says_so()
    {
        var notVideo = PathOf("not-a-video.mp4");
        await File.WriteAllTextAsync(notVideo, "This isn't a video.");
        await SkipWithoutMediaToolsAsync();

        var problem = await Assert.ThrowsAsync<EncodeException>(() =>
            _encoder.EncodeAsync(notVideo, PathOf("output.mp4"), new EncodePlan(640, 360, 30, 500_000, 96_000, null, null)));

        Assert.Contains("FFmpeg", problem.Message);
    }

    /// <summary>Writes the rotation into the track's header, in place, as a phone would have.</summary>
    private static void MarkRotatedClockwise(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var matrix = new[] { 0, 1 << 16, 0, -1 << 16, 0, 0, 0, 0, 0x40000000 };

        for (var i = 0; i < bytes.Length - 4; i++)
        {
            // A version 0 header has its matrix 44 bytes from the start of its name, and the video track has a width after that.
            if (bytes[i] != 't' || bytes[i + 1] != 'k' || bytes[i + 2] != 'h' || bytes[i + 3] != 'd')
            {
                continue;
            }

            var version = bytes[i + 4];
            var at = i + (version == 1 ? 56 : 44);
            var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(at + 36));
            if (width == 0)
            {
                continue;
            }

            for (var cell = 0; cell < matrix.Length; cell++)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(at + (cell * 4)), matrix[cell]);
            }

            File.WriteAllBytes(path, bytes);
            return;
        }

        throw new InvalidOperationException("No video track header was found.");
    }

    private static async Task<int> AudioTracksInAsync(string path)
    {
        var clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(path));
        return clip.EmbeddedAudioTracks.Count;
    }

    private static async Task<Frame> ReadFrameAsync(string path)
    {
        var composition = new MediaComposition();
        composition.Clips.Add(await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(path)));

        using var stream = await composition.GetThumbnailAsync(TimeSpan.FromSeconds(1), 0, 0, VideoFramePrecision.NearestFrame);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);

        return new Frame((int)decoder.PixelWidth, (int)decoder.PixelHeight, pixels.DetachPixelData());
    }

    private static async Task SkipWithoutMediaToolsAsync()
    {
        try
        {
            var composition = new MediaComposition();
            composition.Clips.Add(MediaClip.CreateFromColor(Red, TimeSpan.FromSeconds(1)));
            await Task.CompletedTask;
        }
        catch (COMException)
        {
            Assert.Skip("This edition of Windows has no media tools.");
        }
    }

    private string PathOf(string name) => Path.Combine(_folder, name);

    private async Task<string> MakeVideoAsync(string name, int width, int height, int framesPerSecond = 30)
    {
        await SkipWithoutMediaToolsAsync();
        return await TestVideos.MakeAsync(_folder, name, width, height, framesPerSecond);
    }

    private sealed record Frame(int Width, int Height, byte[] Pixels)
    {
        public bool IsRed(int x, int y)
        {
            var at = ((y * Width) + x) * 4;
            return Pixels[at + 2] > 128 && Pixels[at] < 128;
        }
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
