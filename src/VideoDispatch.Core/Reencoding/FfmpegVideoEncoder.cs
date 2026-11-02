using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VideoDispatch.Core.Editing;

namespace VideoDispatch.Core.Reencoding;

/// <summary>What a program the user pointed at says it is.</summary>
/// <param name="Version">Its first line, such as <c>ffmpeg version 7.1</c>.</param>
/// <param name="HasH264">Whether it has the H.264 encoder this app asks for. Some builds leave it out.</param>
public sealed record FfmpegInfo(string Version, bool HasH264);

/// <summary>
/// Uses an <c>ffmpeg.exe</c> the user chose, or asked this app to download. None ships with the app, and nothing needs
/// it: it's only for those who want what it adds. Arguments go straight to the program, with no shell to read them.
/// </summary>
public sealed partial class FfmpegVideoEncoder(string program) : IVideoEncoder
{
    private const int MaxMessageLength = 300;

    /// <exception cref="EncodeException">It couldn't be run, or isn't FFmpeg.</exception>
    public static async Task<FfmpegInfo> InspectAsync(string program, CancellationToken cancellationToken = default)
    {
        var version = await RunToEndAsync(program, ["-hide_banner", "-version"], cancellationToken);
        var first = version.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        if (!first.StartsWith("ffmpeg", StringComparison.OrdinalIgnoreCase))
        {
            throw new EncodeException("That program doesn't say it's FFmpeg.");
        }

        var encoders = await RunToEndAsync(program, ["-hide_banner", "-encoders"], cancellationToken);
        return new FfmpegInfo(first.Length > MaxMessageLength ? first[..MaxMessageLength] : first, encoders.Contains(" libx264 ", StringComparison.Ordinal));
    }

    public static IReadOnlyList<string> ArgumentsFor(string input, string output, EncodePlan plan)
    {
        // Errors only, because the first one says why, and what FFmpeg says last is just "Conversion failed!".
        var arguments = new List<string> { "-hide_banner", "-v", "error", "-nostdin", "-y", "-i", input };

        // FFmpeg applies a phone's rotation first, so this is the size viewers see. It's the shape as shown, so the pixels
        // are square. H.264 refuses an odd size, so one that couldn't be read is still made even.
        arguments.AddRange(["-vf", plan is { Width: { } width, Height: { } height }
            ? string.Create(CultureInfo.InvariantCulture, $"scale={width}:{height},setsar=1")
            : "scale=trunc(iw/2)*2:trunc(ih/2)*2"]);

        AddOutput(arguments, output, plan);
        return arguments;
    }

    /// <summary>
    /// The arguments for an edit. Each clip is read from its own part of its video, fitted inside the plan's size with
    /// black bars if its shape differs, zoomed and faded, and made exactly as long as it should be. Clips are joined end
    /// to end, or faded into each other where they overlap. The words go over the top, and the sound, already mixed, is
    /// <paramref name="sound"/>.
    /// </summary>
    /// <param name="sound">All of the edit's sound, as long as the edit, from <see cref="SoundMixer"/>.</param>
    public static IReadOnlyList<string> ArgumentsFor(EditProject project, EditWork work, string sound, string output, EncodePlan plan)
    {
        var arguments = new List<string> { "-hide_banner", "-v", "error", "-nostdin", "-y" };
        var width = plan.Width ?? 1920;
        var height = plan.Height ?? 1080;
        var frames = Number(plan.FramesPerSecond ?? 30);
        var filters = new List<string>();
        var input = 0;

        for (var index = 0; index < project.Clips.Count; index++)
        {
            var clip = project.Clips[index];
            var length = Number(clip.Length.TotalSeconds);
            arguments.AddRange(["-ss", Number(clip.Start.TotalSeconds), "-t", Number(clip.Span.TotalSeconds), "-i", clip.SourcePath]);

            // At another speed, each frame's time is stretched or squeezed, and the steady frame rate after it shows
            // frames again or leaves some out.
            var speed = clip.HasSpeed ? $"setpts=(PTS-STARTPTS)/{Number(clip.Speed)}," : "";
            var chain = new StringBuilder(string.Create(CultureInfo.InvariantCulture,
                $"[{input++}:v:0]scale={width}:{height}:force_original_aspect_ratio=decrease:force_divisible_by=2,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1,{speed}fps={frames},format=yuv420p,tpad=stop_mode=clone:stop_duration={length},trim=duration={length},setpts=PTS-STARTPTS"));

            if (clip.IsZoomed)
            {
                // The part of the frame that's seen, stretched back to fill it.
                var (left, top, size) = clip.ZoomedView();
                var across = Even(width * size);
                var down = Even(height * size);
                var x = Math.Min((int)Math.Round(width * left), width - across);
                var y = Math.Min((int)Math.Round(height * top), height - down);
                chain.Append(CultureInfo.InvariantCulture, $",crop={across}:{down}:{x}:{y},scale={width}:{height},setsar=1");
            }

            if (project.FadeInOf(index) is { TotalSeconds: > 0 } fadeIn)
            {
                chain.Append(CultureInfo.InvariantCulture, $",fade=t=in:st=0:d={Number(fadeIn.TotalSeconds)}:color=black");
            }

            if (project.FadeOutOf(index) is { TotalSeconds: > 0 } fadeOut)
            {
                chain.Append(CultureInfo.InvariantCulture, $",fade=t=out:st={Number((clip.Length - fadeOut).TotalSeconds)}:d={Number(fadeOut.TotalSeconds)}:color=black");
            }

            filters.Add($"{chain}[v{index}]");
        }

        // Joined one at a time, so each join can be a cut or a fade.
        var joined = "v0";
        for (var index = 1; index < project.Clips.Count; index++)
        {
            var overlap = project.OverlapBefore(index);
            filters.Add(overlap > TimeSpan.Zero
                ? $"[{joined}][v{index}]xfade=transition=fade:duration={Number(overlap.TotalSeconds)}:offset={Number(project.StartOf(index).TotalSeconds)}[j{index}]"
                : $"[{joined}][v{index}]concat=n=2:v=1:a=0[j{index}]");
            joined = $"j{index}";
        }

        // Each picture of words is the whole frame, shown only while its words are.
        for (var index = 0; index < project.Texts.Count && index < work.TextPictures.Count; index++)
        {
            var text = project.Texts[index];
            var finish = text.Finish < project.Length ? text.Finish : project.Length;
            if (finish <= text.At)
            {
                continue;
            }

            arguments.AddRange(["-loop", "1", "-framerate", frames, "-t", Number(finish.TotalSeconds), "-i", work.TextPictures[index]]);
            filters.Add($"[{joined}][{input++}:v:0]overlay=0:0:eof_action=pass:enable='between(t,{Number(text.At.TotalSeconds)},{Number(finish.TotalSeconds)})',format=yuv420p[t{index}]");
            joined = $"t{index}";
        }

        arguments.AddRange(["-i", sound]);
        var soundInput = input;
        arguments.AddRange(["-filter_complex", string.Join(";", filters), "-map", $"[{joined}]", "-map", $"{soundInput}:a:0", "-t", Number(project.Length.TotalSeconds)]);
        AddOutput(arguments, output, plan);
        return arguments;
    }

    /// <summary>The arguments that read a stretch of a file's sound out as 48 kHz 16-bit stereo WAV, for <see cref="SoundMixer"/>.</summary>
    public static IReadOnlyList<string> ArgumentsFor(SoundPiece piece, string output) =>
    [
        "-hide_banner", "-v", "error", "-nostdin", "-y",
        "-ss", Number(piece.From.TotalSeconds), "-t", Number(piece.Span.TotalSeconds), "-i", piece.SourcePath,
        "-map", "0:a:0", "-vn", "-ac", "2", "-ar", SoundMixer.Rate.ToString(CultureInfo.InvariantCulture), "-c:a", "pcm_s16le", "-f", "wav", output,
    ];

    public async Task EncodeAsync(string input, string output, EncodePlan plan, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        await RunAsync(ArgumentsFor(input, output, plan), plan.Duration, progress, "FFmpeg couldn't re-encode this video", cancellationToken);
    }

    public async Task RenderAsync(EditProject project, EditWork work, string output, EncodePlan plan, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var withSound = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var sound = Path.Combine(work.Folder, "sound.wav");
        await SoundMixer.MixAsync(project, work.Folder, sound, async (piece, path, cancel) =>
        {
            if (!withSound.TryGetValue(piece.SourcePath, out var has))
            {
                withSound[piece.SourcePath] = has = await HasSoundAsync(piece.SourcePath, cancel);
            }

            if (has)
            {
                await RunAsync(ArgumentsFor(piece, path), null, null, $"FFmpeg couldn't read the sound in {Path.GetFileName(piece.SourcePath)}", cancel);
            }

            return has;
        }, cancellationToken);

        await RunAsync(ArgumentsFor(project, work, sound, output, plan), plan.Duration, progress, "FFmpeg couldn't save this edit", cancellationToken);
    }

    /// <summary>Asked of FFmpeg, which lists a video's streams when given nothing to do with it.</summary>
    private async Task<bool> HasSoundAsync(string path, CancellationToken cancellationToken)
    {
        using var process = Start(program, ["-hide_banner", "-nostdin", "-i", path]);
        using var stop = cancellationToken.Register(() => TryKill(process));

        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var text = await process.StandardError.ReadToEndAsync(CancellationToken.None);
        await output;
        await process.WaitForExitAsync(CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();
        return SoundStreamPattern().IsMatch(text);
    }

    private static void AddOutput(List<string> arguments, string output, EncodePlan plan)
    {
        // A key frame at least every two seconds, so players can jump about in the new file.
        arguments.AddRange(["-map_metadata", "-1", "-c:v", "libx264", "-preset", "medium", "-pix_fmt", "yuv420p", "-force_key_frames", "expr:gte(t,n_forced*2)"]);

        if (plan.ConstantQuality is { } quality)
        {
            // Quality decides, and the plan's bitrate only stops a difficult scene running away.
            arguments.AddRange(["-crf", quality.ToString(CultureInfo.InvariantCulture), "-maxrate", Bits(plan.VideoBitrate * 2), "-bufsize", Bits(plan.VideoBitrate * 4)]);
        }
        else
        {
            arguments.AddRange(["-b:v", Bits(plan.VideoBitrate), "-maxrate", Bits(plan.VideoBitrate * 3 / 2), "-bufsize", Bits(plan.VideoBitrate * 3)]);
        }

        arguments.AddRange(["-c:a", "aac", "-b:a", Bits(plan.AudioBitrate), "-movflags", "+faststart", "-progress", "pipe:1", "-nostats", output]);
    }

    private async Task RunAsync(IReadOnlyList<string> arguments, TimeSpan? length, IProgress<double>? progress, string failure, CancellationToken cancellationToken)
    {
        using var process = Start(program, arguments);

        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var stop = cancellationToken.Register(() => TryKill(process));

        while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
        {
            if (length is { TotalMicroseconds: > 0 } duration &&
                line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var done))
            {
                progress?.Report(Math.Clamp(done / duration.TotalMicroseconds * 100, 0, 100));
            }
        }

        await process.WaitForExitAsync(CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();

        if (process.ExitCode != 0)
        {
            var text = await errors;
            throw new EncodeException(text.Contains("libx264", StringComparison.Ordinal) && text.Contains("Unknown encoder", StringComparison.OrdinalIgnoreCase)
                ? "This FFmpeg has no H.264 encoder (libx264). Choose a build that has one under Settings."
                : $"{failure}: {FirstErrorIn(text)}");
        }
    }

    private static string Bits(long bitsPerSecond) => bitsPerSecond.ToString(CultureInfo.InvariantCulture);

    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>H.264 refuses odd sizes.</summary>
    private static int Even(double value) => Math.Max(2, (int)Math.Round(value / 2) * 2);

    /// <summary>The first line, without the <c>[libx264 @ 000001A2]</c> that says which part of FFmpeg is talking.</summary>
    private static string FirstErrorIn(string text)
    {
        var first = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "it gave no reason.";
        first = SpeakerPattern().Replace(first, "");
        return first.Length > MaxMessageLength ? first[..MaxMessageLength] : first;
    }

    private static Process Start(string program, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            return Process.Start(start) ?? throw new EncodeException("FFmpeg wouldn't start.");
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException or DirectoryNotFoundException or InvalidOperationException)
        {
            throw new EncodeException($"FFmpeg couldn't be run from {program}. Choose it again under Settings, or stop using it to go back to Windows' tools.", exception);
        }
    }

    private static async Task<string> RunToEndAsync(string program, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        using var process = Start(program, arguments);
        using var stop = cancellationToken.Register(() => TryKill(process));

        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var text = await process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        await errors;
        await process.WaitForExitAsync(CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();
        return text;
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
        }
    }

    [GeneratedRegex(@"\A(\[[^\]]* @ [^\]]*\]\s*)+")]
    private static partial Regex SpeakerPattern();

    [GeneratedRegex(@"^\s*Stream #\d+:\d+.*: Audio:", RegexOptions.Multiline)]
    private static partial Regex SoundStreamPattern();
}
