using System.Runtime.InteropServices;
using VideoDispatch.Core.Editing;
using VideoDispatch.Core.Media;
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

    // Mixing the sound is counted as this much of saving an edit.
    private const double MixedPercent = 5;

    // A fade is made of a step about every 80 ms: enough not to be seen as steps, few enough to be quick.
    private const double StepsPerSecond = 12;
    private const int MostSteps = 24;

    // CODECAPI_AVEncMPVGOPSize: how many frames there may be between key frames.
    private static readonly Guid KeyFrameInterval = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");

    private static readonly Windows.UI.Color Black = Windows.UI.Color.FromArgb(255, 0, 0, 0);

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

    /// <remarks>
    /// Windows' crop and zoom effect failed in every form tried, so a zoomed clip is drawn over black, bigger than the
    /// frame. Overlays only have one opacity each, so fades and crossfades are made of short steps of it. All the sound
    /// is mixed first, by <see cref="SoundMixer"/>, and the clips' own sound is turned off.
    /// </remarks>
    public async Task RenderAsync(EditProject project, EditWork work, string output, EncodePlan plan, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var width = plan.Width ?? 1920;
            var height = plan.Height ?? 1080;

            var sound = Path.Combine(work.Folder, "sound.wav");
            await SoundMixer.MixAsync(project, work.Folder, sound, ReadSoundAsync, cancellationToken);
            progress?.Report(MixedPercent);

            var composition = new MediaComposition();
            var pictures = new MediaOverlayLayer();
            var crossfades = new MediaOverlayLayer();
            var fades = new MediaOverlayLayer();
            var words = new MediaOverlayLayer();

            for (var index = 0; index < project.Clips.Count; index++)
            {
                var clip = project.Clips[index];
                var start = project.StartOf(index);
                var overlap = project.OverlapBefore(index);
                var rect = await PlaceAsync(clip, width, height);

                // Where it fades in over the clip before, that part is drawn over it instead, so the main line of clips
                // plays the rest.
                var shown = await ClipAsync(clip, clip.Start + overlap, clip.End, cancellationToken);
                if (clip.IsZoomed)
                {
                    composition.Clips.Add(MediaClip.CreateFromColor(Black, clip.Length - overlap));
                    pictures.Overlays.Add(new MediaOverlay(shown, rect, 1) { Delay = start + overlap, AudioEnabled = false });
                }
                else
                {
                    composition.Clips.Add(shown);
                }

                foreach (var (from, to, opacity) in Steps(overlap, rising: true))
                {
                    var step = await ClipAsync(clip, clip.Start + from, clip.Start + to, cancellationToken);
                    crossfades.Overlays.Add(new MediaOverlay(step, rect, opacity) { Delay = start + from, AudioEnabled = false });
                }

                foreach (var (from, to, opacity) in Steps(project.FadeInOf(index), rising: false))
                {
                    fades.Overlays.Add(new MediaOverlay(MediaClip.CreateFromColor(Black, to - from), Whole(width, height), opacity) { Delay = start + from });
                }

                var fadeOut = project.FadeOutOf(index);
                foreach (var (from, to, opacity) in Steps(fadeOut, rising: true))
                {
                    fades.Overlays.Add(new MediaOverlay(MediaClip.CreateFromColor(Black, to - from), Whole(width, height), opacity) { Delay = start + clip.Length - fadeOut + from });
                }
            }

            for (var index = 0; index < project.Texts.Count && index < work.TextPictures.Count; index++)
            {
                var text = project.Texts[index];
                var finish = text.Finish < project.Length ? text.Finish : project.Length;
                if (finish > text.At)
                {
                    var picture = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(work.TextPictures[index])).AsTask(cancellationToken);
                    var shown = await MediaClip.CreateFromImageFileAsync(picture, finish - text.At).AsTask(cancellationToken);
                    words.Overlays.Add(new MediaOverlay(shown, Whole(width, height), 1) { Delay = text.At });
                }
            }

            foreach (var layer in new[] { pictures, crossfades, fades, words })
            {
                if (layer.Overlays.Count > 0)
                {
                    composition.OverlayLayers.Add(layer);
                }
            }

            if (SoundMixer.FramesIn(project.Length) > 0)
            {
                composition.BackgroundAudioTracks.Add(await BackgroundAudioTrack.CreateFromFileAsync(
                    await StorageFile.GetFileFromPathAsync(Path.GetFullPath(sound)).AsTask(cancellationToken)).AsTask(cancellationToken));
            }

            await File.WriteAllBytesAsync(output, [], cancellationToken);
            var target = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(output)).AsTask(cancellationToken);

            // A clip of another shape is fitted inside this size, with black bars.
            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
            profile.Video.Bitrate = (uint)plan.VideoBitrate;
            profile.Audio.Bitrate = (uint)plan.AudioBitrate;
            profile.Video.Width = (uint)width;
            profile.Video.Height = (uint)height;
            profile.Video.FrameRate.Numerator = (uint)Math.Round((plan.FramesPerSecond ?? 30) * 1000);
            profile.Video.FrameRate.Denominator = 1000;
            AskForKeyFrames(profile, 2);

            var rendering = new Progress<double>(percent => progress?.Report(MixedPercent + (percent * (100 - MixedPercent) / 100)));
            var result = await composition.RenderToFileAsync(target, MediaTrimmingPreference.Precise, profile)
                .AsTask(cancellationToken, rendering);
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

    /// <summary>The steps a fade of <paramref name="length"/> is made of: when each starts and ends, and how much it covers.</summary>
    /// <param name="rising">Each step covering more than the last, as when fading out to black, or fading a clip in over another.</param>
    public static IEnumerable<(TimeSpan From, TimeSpan To, double Opacity)> Steps(TimeSpan length, bool rising)
    {
        if (length <= TimeSpan.Zero)
        {
            yield break;
        }

        var count = Math.Clamp((int)Math.Round(length.TotalSeconds * StepsPerSecond), 3, MostSteps);
        for (var step = 0; step < count; step++)
        {
            var share = (step + 0.5) / count;
            yield return (length * step / count, length * (step + 1) / count, rising ? share : 1 - share);
        }
    }

    /// <summary>Part of a clip's video, from <paramref name="from"/> to <paramref name="to"/> of its own time, with its sound off.</summary>
    private static async Task<MediaClip> ClipAsync(EditClip clip, TimeSpan from, TimeSpan to, CancellationToken cancellationToken)
    {
        var source = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(clip.SourcePath)).AsTask(cancellationToken);
        var media = await MediaClip.CreateFromFileAsync(source).AsTask(cancellationToken);
        var fromEnd = media.OriginalDuration - to;
        media.TrimTimeFromStart = from;
        media.TrimTimeFromEnd = fromEnd > TimeSpan.Zero ? fromEnd : TimeSpan.Zero;
        media.Volume = 0;
        return media;
    }

    /// <summary>
    /// Where a clip's picture goes in the frame: fitted inside it with black bars if its shape differs, as the main line of
    /// clips does by itself, then zoomed. Overlays are stretched to whatever they're given, so the shape must be right.
    /// </summary>
    private static async Task<Rect> PlaceAsync(EditClip clip, int width, int height)
    {
        double across, down;
        if (Mp4MetadataReader.Read(clip.SourcePath) is { DisplayWidth: > 0, DisplayHeight: > 0 } read)
        {
            (across, down) = (read.DisplayWidth, read.DisplayHeight);
        }
        else
        {
            var media = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(Path.GetFullPath(clip.SourcePath)));
            var video = media.GetVideoEncodingProperties();
            (across, down) = video.Width > 0 && video.Height > 0 ? ((double)video.Width, (double)video.Height) : (width, height);
        }

        var scale = Math.Min(width / across, height / down);
        var fitted = new Rect((width - (across * scale)) / 2, (height - (down * scale)) / 2, across * scale, down * scale);
        if (!clip.IsZoomed)
        {
            return fitted;
        }

        var (left, top, size) = clip.ZoomedView();
        return new Rect((fitted.X - (left * width)) / size, (fitted.Y - (top * height)) / size, fitted.Width / size, fitted.Height / size);
    }

    private static Rect Whole(int width, int height) => new(0, 0, width, height);

    /// <summary>A stretch of a file's sound as WAV for the mix. A file with no sound is silence.</summary>
    private static async Task<bool> ReadSoundAsync(SoundPiece piece, string output, CancellationToken cancellationToken)
    {
        var source = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(piece.SourcePath)).AsTask(cancellationToken);
        var own = await MediaEncodingProfile.CreateFromFileAsync(source).AsTask(cancellationToken);
        if (own.Audio is null)
        {
            return false;
        }

        await File.WriteAllBytesAsync(output, [], cancellationToken);
        var target = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(output)).AsTask(cancellationToken);
        var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);
        profile.Audio = AudioEncodingProperties.CreatePcm(SoundMixer.Rate, SoundMixer.Channels, 16);

        // Tried here, the stop is a time in the file, not how much to leave off its end as Microsoft's documents say.
        var transcoder = new MediaTranscoder { TrimStartTime = piece.From, TrimStopTime = piece.To };
        var prepared = await transcoder.PrepareFileTranscodeAsync(source, target, profile).AsTask(cancellationToken);
        if (!prepared.CanTranscode)
        {
            throw new EncodeException($"Windows' tools couldn't read the sound in {Path.GetFileName(piece.SourcePath)} ({prepared.FailureReason}). Choose FFmpeg under Settings to try that instead.");
        }

        await prepared.TranscodeAsync().AsTask(cancellationToken);
        return true;
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
