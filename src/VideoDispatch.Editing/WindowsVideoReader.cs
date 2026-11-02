using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Storage;

namespace VideoDispatch.Editing;

/// <summary>
/// What the editor asks of a video with Windows' own tools: how long it is, and single pictures from it for the timeline.
/// Windows' call for many pictures at once returned the first one every time when tried, so they're asked for singly,
/// at about 0.4 seconds each. Anything Windows can't read is an answer of null, never an error.
/// </summary>
public static class WindowsVideoReader
{
    // Each public call makes the library reachable before calling the part that uses it, since that part can't even
    // be prepared to run without it.
    public static Task<TimeSpan?> ReadLengthAsync(string path, CancellationToken cancellationToken = default)
    {
        WinRtLibrary.Install();
        return LengthAsync(path, cancellationToken);
    }

    /// <summary>The picture at <paramref name="at"/>, in the video's own time, as an encoded image <paramref name="height"/> high.</summary>
    public static Task<byte[]?> ReadFrameAsync(string path, TimeSpan at, int height, CancellationToken cancellationToken = default)
    {
        WinRtLibrary.Install();
        return FrameAsync(path, at, height, cancellationToken);
    }

    private static async Task<TimeSpan?> LengthAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask(cancellationToken);
            var clip = await MediaClip.CreateFromFileAsync(file).AsTask(cancellationToken);
            return clip.OriginalDuration > TimeSpan.Zero ? clip.OriginalDuration : null;
        }
        catch (Exception exception) when (exception is COMException or UnauthorizedAccessException or FileNotFoundException or ArgumentException)
        {
            return null;
        }
    }

    private static async Task<byte[]?> FrameAsync(string path, TimeSpan at, int height, CancellationToken cancellationToken)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask(cancellationToken);
            var composition = new MediaComposition();
            composition.Clips.Add(await MediaClip.CreateFromFileAsync(file).AsTask(cancellationToken));

            using var stream = await composition.GetThumbnailAsync(at, 0, height, VideoFramePrecision.NearestFrame).AsTask(cancellationToken);
            using var read = stream.AsStreamForRead();
            using var copy = new MemoryStream();
            await read.CopyToAsync(copy, cancellationToken);
            return copy.ToArray();
        }
        catch (Exception exception) when (exception is COMException or UnauthorizedAccessException or FileNotFoundException or ArgumentException)
        {
            return null;
        }
    }
}
