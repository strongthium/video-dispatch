using System.Runtime.InteropServices;
using VideoDispatch.Core.Reencoding;
using Windows.Foundation;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace VideoDispatch.Editing;

/// <summary>Re-encodes with what's already in Windows, so nothing needs installing. Some Windows editions leave these tools out.</summary>
public sealed class WindowsVideoEncoder : IVideoEncoder
{
    private const string TryFfmpeg = "Windows' tools couldn't re-encode this video";

    static WindowsVideoEncoder() => WinRtLibrary.Install();

    public async Task EncodeAsync(string input, string output, EncodePlan plan, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            try
            {
                await RunAsync(input, output, plan, useHardware: true, progress, cancellationToken);
            }
            catch (COMException) when (!cancellationToken.IsCancellationRequested)
            {
                // A video card's encoder can refuse what the software one takes.
                await RunAsync(input, output, plan, useHardware: false, progress, cancellationToken);
            }
        }
        catch (COMException exception) when (exception.HResult == unchecked((int)0x80040154))
        {
            throw new EncodeException("This edition of Windows doesn't have the media tools. Install its Media Feature Pack, or choose FFmpeg under Settings.", exception);
        }
        catch (Exception exception) when (exception is COMException or UnauthorizedAccessException or FileNotFoundException or ArgumentException)
        {
            throw new EncodeException($"{TryFfmpeg} (0x{exception.HResult:X8}). Its format may not be one Windows reads. Choose FFmpeg under Settings to try that instead, or send the file as it is.", exception);
        }
    }

    private static async Task RunAsync(string input, string output, EncodePlan plan, bool useHardware, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        // These calls refuse a relative path.
        var source = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(input)).AsTask(cancellationToken);
        await File.WriteAllBytesAsync(output, [], cancellationToken);
        var target = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(output)).AsTask(cancellationToken);

        // The quality named here is only a starting point, since everything that matters is set below.
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Video.Bitrate = (uint)plan.VideoBitrate;
        profile.Audio.Bitrate = (uint)plan.AudioBitrate;

        // What the file says for itself stands in for anything the plan couldn't read.
        var own = await MediaEncodingProfile.CreateFromFileAsync(source).AsTask(cancellationToken);

        // The transcoder turns a sideways video upright by itself, but takes the size in the order the file stores it.
        // Given the size as viewers see it, it fits the picture sideways, in black bars.
        var (width, height) = plan.SourceIsSideways ? (plan.Height, plan.Width) : (plan.Width, plan.Height);
        profile.Video.Width = width is { } across ? (uint)across : own.Video.Width & ~1u;
        profile.Video.Height = height is { } down ? (uint)down : own.Video.Height & ~1u;

        if (plan.FramesPerSecond is { } frames)
        {
            profile.Video.FrameRate.Numerator = (uint)Math.Round(frames * 1000);
            profile.Video.FrameRate.Denominator = 1000;
        }
        else
        {
            profile.Video.FrameRate.Numerator = own.Video.FrameRate.Numerator;
            profile.Video.FrameRate.Denominator = own.Video.FrameRate.Denominator;
        }

        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = useHardware };
        var prepared = await transcoder.PrepareFileTranscodeAsync(source, target, profile).AsTask(cancellationToken);
        if (!prepared.CanTranscode)
        {
            throw new EncodeException($"{TryFfmpeg} ({prepared.FailureReason}). Its format may not be one Windows reads. Choose FFmpeg under Settings to try that instead, or send the file as it is.");
        }

        await prepared.TranscodeAsync().AsTask(cancellationToken, progress ?? new Progress<double>());
    }
}
