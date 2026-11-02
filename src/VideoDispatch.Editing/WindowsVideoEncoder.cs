using System.Runtime.InteropServices;
using VideoDispatch.Core.Editing;
using VideoDispatch.Core.Reencoding;
using Windows.Foundation;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace VideoDispatch.Editing;

/// <summary>Re-encodes with what's already in Windows, so nothing needs installing. Some Windows editions leave these tools out.</summary>
public sealed class WindowsVideoEncoder : IVideoEncoder
{
    private const string TryFfmpeg = "Windows' tools couldn't re-encode this video";

    // CODECAPI_AVEncMPVGOPSize: how many frames there may be between key frames.
    private static readonly Guid KeyFrameInterval = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");

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

    /// <summary>
    /// A key frame at least every <paramref name="seconds"/>. Left to itself, Windows' encoder can go many seconds
    /// without one on calm footage, and players then land well away from where they're asked to jump to.
    /// </summary>
    public static void AskForKeyFrames(MediaEncodingProfile profile, double seconds)
    {
        var frames = profile.Video.FrameRate.Denominator > 0 ? (double)profile.Video.FrameRate.Numerator / profile.Video.FrameRate.Denominator : 30;
        profile.Video.Properties[KeyFrameInterval] = (uint)Math.Max(1, Math.Round(frames * seconds));
    }

    public async Task RenderAsync(EditProject project, string output, EncodePlan plan, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var composition = new MediaComposition();
            foreach (var clip in project.Clips)
            {
                var source = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(clip.SourcePath)).AsTask(cancellationToken);
                var media = await MediaClip.CreateFromFileAsync(source).AsTask(cancellationToken);
                var fromEnd = media.OriginalDuration - clip.End;
                media.TrimTimeFromStart = clip.Start;
                media.TrimTimeFromEnd = fromEnd > TimeSpan.Zero ? fromEnd : TimeSpan.Zero;
                composition.Clips.Add(media);
            }

            await File.WriteAllBytesAsync(output, [], cancellationToken);
            var target = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(output)).AsTask(cancellationToken);

            // A clip of another shape is fitted inside this size, with black bars.
            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
            profile.Video.Bitrate = (uint)plan.VideoBitrate;
            profile.Audio.Bitrate = (uint)plan.AudioBitrate;
            profile.Video.Width = (uint)(plan.Width ?? 1920);
            profile.Video.Height = (uint)(plan.Height ?? 1080);
            profile.Video.FrameRate.Numerator = (uint)Math.Round((plan.FramesPerSecond ?? 30) * 1000);
            profile.Video.FrameRate.Denominator = 1000;
            AskForKeyFrames(profile, 2);

            var result = await composition.RenderToFileAsync(target, MediaTrimmingPreference.Precise, profile)
                .AsTask(cancellationToken, progress ?? new Progress<double>());
            if (result != TranscodeFailureReason.None)
            {
                throw new EncodeException($"Windows' tools couldn't save this edit ({result}). One of its videos may be in a format Windows doesn't read. Choose FFmpeg under Settings to try that instead.");
            }
        }
        catch (COMException exception) when (exception.HResult == unchecked((int)0x80040154))
        {
            throw new EncodeException("This edition of Windows doesn't have the media tools. Install its Media Feature Pack, or choose FFmpeg under Settings.", exception);
        }
        catch (Exception exception) when (exception is COMException or UnauthorizedAccessException or FileNotFoundException or ArgumentException)
        {
            throw new EncodeException($"Windows' tools couldn't save this edit (0x{exception.HResult:X8}). One of its videos may be missing, or in a format Windows doesn't read. Choose FFmpeg under Settings to try that instead.", exception);
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

        AskForKeyFrames(profile, 2);

        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = useHardware };
        var prepared = await transcoder.PrepareFileTranscodeAsync(source, target, profile).AsTask(cancellationToken);
        if (!prepared.CanTranscode)
        {
            throw new EncodeException($"{TryFfmpeg} ({prepared.FailureReason}). Its format may not be one Windows reads. Choose FFmpeg under Settings to try that instead, or send the file as it is.");
        }

        await prepared.TranscodeAsync().AsTask(cancellationToken, progress ?? new Progress<double>());
    }
}
