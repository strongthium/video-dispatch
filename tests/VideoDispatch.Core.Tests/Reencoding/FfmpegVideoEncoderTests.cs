using VideoDispatch.Core.Media;
using VideoDispatch.Core.Reencoding;

namespace VideoDispatch.Core.Tests.Reencoding;

/// <summary>
/// The arguments, and running a real process that reads and answers as FFmpeg does. That stand-in is a batch file,
/// because FFmpeg isn't required to build this. Set VIDEO_DISPATCH_FFMPEG to the real one to try it too.
/// </summary>
public sealed class FfmpegVideoEncoderTests : IDisposable
{
    private static readonly EncodePlan Quality = new(720, 1280, 30, 2_000_000, 128_000, 23, TimeSpan.FromSeconds(4));
    private static readonly EncodePlan Size = new(720, 1280, 30, 2_000_000, 128_000, null, TimeSpan.FromSeconds(4));

    private readonly string _folder = Path.Combine(AppContext.BaseDirectory, "ffmpeg-" + Guid.NewGuid().ToString("N"));

    public FfmpegVideoEncoderTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Script(string body)
    {
        var path = Path.Combine(_folder, "ffmpeg.cmd");
        File.WriteAllText(path, "@echo off\r\n" + body.ReplaceLineEndings("\r\n"));
        return path;
    }

    private string Working() => Script("""
        if "%2"=="-version" (
          echo ffmpeg version 9.9 Copyright the FFmpeg developers
          exit /b 0
        )
        if "%2"=="-encoders" (
          echo  V..... libx264              H.264 encoder
          exit /b 0
        )
        echo out_time_us=1000000
        echo progress=continue
        echo out_time_us=2000000
        echo progress=continue
        echo out_time_us=4000000
        echo progress=end
        :next
        set last=%~1
        shift
        if not "%~1"=="" goto next
        echo made> "%last%"
        exit /b 0
        """);

    [Fact]
    public void Aiming_at_quality_uses_crf_with_a_ceiling()
    {
        var arguments = FfmpegVideoEncoder.ArgumentsFor("in.mov", "out.mp4", Quality).ToList();

        Assert.Equal("23", arguments[arguments.IndexOf("-crf") + 1]);
        Assert.Equal("4000000", arguments[arguments.IndexOf("-maxrate") + 1]);
        Assert.DoesNotContain("-b:v", arguments);
    }

    [Fact]
    public void Aiming_at_a_size_uses_the_bitrate()
    {
        var arguments = FfmpegVideoEncoder.ArgumentsFor("in.mov", "out.mp4", Size).ToList();

        Assert.Equal("2000000", arguments[arguments.IndexOf("-b:v") + 1]);
        Assert.DoesNotContain("-crf", arguments);
    }

    [Fact]
    public void Makes_an_H264_MP4_players_can_start_early_and_leaves_out_what_the_source_carried()
    {
        var arguments = FfmpegVideoEncoder.ArgumentsFor("in.mov", "out.mp4", Size).ToList();

        Assert.Equal("libx264", arguments[arguments.IndexOf("-c:v") + 1]);
        Assert.Equal("yuv420p", arguments[arguments.IndexOf("-pix_fmt") + 1]);
        Assert.Equal("aac", arguments[arguments.IndexOf("-c:a") + 1]);
        Assert.Equal("128000", arguments[arguments.IndexOf("-b:a") + 1]);
        Assert.Equal("+faststart", arguments[arguments.IndexOf("-movflags") + 1]);
        Assert.Equal("-1", arguments[arguments.IndexOf("-map_metadata") + 1]);
        Assert.Equal("in.mov", arguments[arguments.IndexOf("-i") + 1]);
        Assert.Equal("out.mp4", arguments[^1]);
    }

    [Fact]
    public void Makes_the_size_it_was_asked_for_with_square_pixels()
    {
        Assert.Contains("scale=720:1280,setsar=1", FfmpegVideoEncoder.ArgumentsFor("a", "b", Size));
    }

    [Fact]
    public void A_size_that_could_not_be_read_is_still_made_even()
    {
        Assert.Contains("scale=trunc(iw/2)*2:trunc(ih/2)*2", FfmpegVideoEncoder.ArgumentsFor("a", "b", Size with { Width = null, Height = null }));
    }

    [Fact]
    public async Task Runs_the_program_and_reports_progress()
    {
        var output = Path.Combine(_folder, "output.mp4");
        var seen = new List<double>();

        await new FfmpegVideoEncoder(Working()).EncodeAsync("in.mov", output, Size, new Reporter(seen));

        Assert.True(File.Exists(output));
        Assert.Equal([25d, 50d, 100d], seen);
    }

    [Fact]
    public async Task A_failure_passes_on_the_first_thing_it_said_without_who_said_it()
    {
        var script = Script("""
            echo [vost#0:0/libx264 @ 000001C4] [enc:libx264 @ 000001C5] width not divisible by 2 (641x361) 1>&2
            echo [vf#0:0 @ 000001C6] Task finished with error code: -22 1>&2
            exit /b 1
            """);

        var problem = await Assert.ThrowsAsync<EncodeException>(() => new FfmpegVideoEncoder(script).EncodeAsync("in.mov", "out.mp4", Size));

        Assert.EndsWith(": width not divisible by 2 (641x361)", problem.Message);
    }

    [Fact]
    public async Task A_build_without_H264_says_so()
    {
        var script = Script("""
            echo Unknown encoder 'libx264' 1>&2
            exit /b 1
            """);

        var problem = await Assert.ThrowsAsync<EncodeException>(() => new FfmpegVideoEncoder(script).EncodeAsync("in.mov", "out.mp4", Size));

        Assert.Contains("no H.264 encoder", problem.Message);
    }

    [Fact]
    public async Task A_program_that_is_not_there_says_how_to_go_back_to_Windows_tools()
    {
        var problem = await Assert.ThrowsAsync<EncodeException>(() =>
            new FfmpegVideoEncoder(Path.Combine(_folder, "gone.exe")).EncodeAsync("in.mov", "out.mp4", Size));

        Assert.Contains("Windows' tools", problem.Message);
    }

    [Fact]
    public async Task Can_be_stopped_while_it_runs()
    {
        var script = Script("""
            echo out_time_us=1000000
            ping -n 30 127.0.0.1 > nul
            """);
        using var stop = new CancellationTokenSource();
        var reached = new TaskCompletionSource();

        var running = new FfmpegVideoEncoder(script).EncodeAsync("in.mov", "out.mp4", Size, new OnReport(reached), stop.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public async Task Inspecting_reads_the_version_and_whether_it_can_make_H264()
    {
        var info = await FfmpegVideoEncoder.InspectAsync(Working());

        Assert.StartsWith("ffmpeg version 9.9", info.Version);
        Assert.True(info.HasH264);
    }

    [Fact]
    public async Task Inspecting_a_build_without_H264_says_so()
    {
        var script = Script("""
            echo ffmpeg version 9.9
            """);

        Assert.False((await FfmpegVideoEncoder.InspectAsync(script)).HasH264);
    }

    [Fact]
    public async Task Inspecting_another_program_refuses_it()
    {
        var script = Script("""
            echo Hello from something else
            """);

        await Assert.ThrowsAsync<EncodeException>(() => FfmpegVideoEncoder.InspectAsync(script));
    }

    [Fact]
    public async Task A_real_FFmpeg_if_there_is_one_to_try()
    {
        var program = Environment.GetEnvironmentVariable("VIDEO_DISPATCH_FFMPEG");
        if (string.IsNullOrEmpty(program))
        {
            Assert.Skip("Set VIDEO_DISPATCH_FFMPEG to try a real FFmpeg.");
        }

        Assert.True((await FfmpegVideoEncoder.InspectAsync(program)).HasH264);

        // An odd size is what H.264 refuses, and a Matroska file's size can't be read here, so nothing would correct it.
        var source = Path.Combine(_folder, "odd.mkv");
        var make = new System.Diagnostics.ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", "color=blue:s=641x361:r=30:d=1", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv444p", source })
        {
            make.ArgumentList.Add(argument);
        }

        using (var process = System.Diagnostics.Process.Start(make)!)
        {
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }

        var encoder = new FfmpegVideoEncoder(program);

        var unread = Path.Combine(_folder, "unread.mp4");
        await encoder.EncodeAsync(source, unread, Quality with { Width = null, Height = null });

        // Its pixels stay very slightly out of square to keep the shape, since its real shape isn't known here.
        Assert.InRange(Mp4MetadataReader.Read(unread)!.DisplayWidth, 639, 641);

        var sized = Path.Combine(_folder, "sized.mp4");
        await encoder.EncodeAsync(source, sized, Quality with { Width = 640, Height = 360 });
        Assert.Equal(640, Mp4MetadataReader.Read(sized)!.DisplayWidth);
        Assert.Equal(360, Mp4MetadataReader.Read(sized)!.DisplayHeight);

        var notVideo = Path.Combine(_folder, "not-a-video.mp4");
        await File.WriteAllTextAsync(notVideo, "This isn't a video.");
        var problem = await Assert.ThrowsAsync<EncodeException>(() => encoder.EncodeAsync(notVideo, Path.Combine(_folder, "none.mp4"), Quality));
        Assert.DoesNotContain("Conversion failed", problem.Message);
    }

    private sealed class Reporter(List<double> seen) : IProgress<double>
    {
        public void Report(double value) => seen.Add(value);
    }

    private sealed class OnReport(TaskCompletionSource reached) : IProgress<double>
    {
        public void Report(double value) => reached.TrySetResult();
    }
}
