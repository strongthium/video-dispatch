using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace VideoDispatch.Core.Reencoding;

/// <summary>What a program the user pointed at says it is.</summary>
/// <param name="Version">Its first line, such as <c>ffmpeg version 7.1</c>.</param>
/// <param name="HasH264">Whether it has the H.264 encoder this app asks for. Some builds leave it out.</param>
public sealed record FfmpegInfo(string Version, bool HasH264);

/// <summary>
/// Uses an <c>ffmpeg.exe</c> the user chose, or asked this app to download. None ships with the app, and nothing needs
/// it: it's only for those who want what it adds. Arguments go straight to the program, with no shell to read them.
/// </summary>
public sealed class FfmpegVideoEncoder(string program) : IVideoEncoder
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
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", input, "-map_metadata", "-1", "-c:v", "libx264", "-preset", "medium", "-pix_fmt", "yuv420p" };

        if (plan.ConstantQuality is { } quality)
        {
            // Quality decides, and the plan's bitrate only stops a difficult scene running away.
            arguments.AddRange(["-crf", quality.ToString(CultureInfo.InvariantCulture), "-maxrate", Bits(plan.VideoBitrate * 2), "-bufsize", Bits(plan.VideoBitrate * 4)]);
        }
        else
        {
            arguments.AddRange(["-b:v", Bits(plan.VideoBitrate), "-maxrate", Bits(plan.VideoBitrate * 3 / 2), "-bufsize", Bits(plan.VideoBitrate * 3)]);
        }

        // FFmpeg applies a phone's rotation first, so this is the size viewers see.
        if (plan is { Width: { } width, Height: { } height })
        {
            arguments.AddRange(["-vf", string.Create(CultureInfo.InvariantCulture, $"scale={width}:{height}")]);
        }

        arguments.AddRange(["-c:a", "aac", "-b:a", Bits(plan.AudioBitrate), "-movflags", "+faststart", "-progress", "pipe:1", "-nostats", output]);
        return arguments;
    }

    public async Task EncodeAsync(string input, string output, EncodePlan plan, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using var process = Start(program, ArgumentsFor(input, output, plan));

        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var stop = cancellationToken.Register(() => TryKill(process));

        while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
        {
            if (plan.Duration is { TotalMicroseconds: > 0 } duration &&
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
                : $"FFmpeg couldn't re-encode this video: {LastLineOf(text)}");
        }
    }

    private static string Bits(long bitsPerSecond) => bitsPerSecond.ToString(CultureInfo.InvariantCulture);

    private static string LastLineOf(string text)
    {
        var last = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "it gave no reason.";
        return last.Length > MaxMessageLength ? last[..MaxMessageLength] : last;
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
}
