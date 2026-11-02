using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Editing;
using Windows.Media.Playback;
using Windows.Storage;

namespace VideoDispatch.Editing;

/// <summary>A picture from a video, as blue, green, red and alpha bytes for each pixel, row after row.</summary>
public sealed record VideoPicture(int Width, int Height, byte[] Pixels);

/// <summary>
/// What the editor asks of a video with Windows' own tools: how long it is, and single pictures from it. Pictures come
/// from Windows' player, kept open for each video and handing over the frame it's jumped to, in about 20 ms. Asking a
/// composition for a thumbnail, as this did at first, took about 0.4 seconds whatever was kept open, and is only used if
/// the player can't open a video. Both gave the same frame at every moment tried. Anything Windows can't read is an
/// answer of null, never an error.
/// </summary>
public static class WindowsVideoReader
{
    private const int MostOpen = 3;
    private static readonly TimeSpan LongestWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdleFor = TimeSpan.FromSeconds(30);

    // Each public call makes the library reachable before calling the part that uses it, since that part can't even
    // be prepared to run without it.
    public static Task<TimeSpan?> ReadLengthAsync(string path, CancellationToken cancellationToken = default)
    {
        WinRtLibrary.Install();
        return LengthAsync(path, cancellationToken);
    }

    /// <summary>The picture at <paramref name="at"/>, in the video's own time, <paramref name="height"/> high and upright.</summary>
    public static Task<VideoPicture?> ReadFrameAsync(string path, TimeSpan at, int height, CancellationToken cancellationToken = default)
    {
        WinRtLibrary.Install();
        return Task.Run(() => FrameAsync(path, at, height, cancellationToken), cancellationToken);
    }

    /// <summary>Lets go of every video kept open for pictures, so nothing holds them.</summary>
    public static void Release()
    {
        WinRtLibrary.Install();
        ReleaseAll();
    }

    private static void ReleaseAll()
    {
        var open = Kept.Open;
        lock (open)
        {
            foreach (var server in open)
            {
                server.Dispose();
            }

            open.Clear();
        }
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

    private static async Task<VideoPicture?> FrameAsync(string path, TimeSpan at, int height, CancellationToken cancellationToken)
    {
        try
        {
            var server = await ServerForAsync(Path.GetFullPath(path), cancellationToken);
            if (server is not null)
            {
                return await server.ReadAsync(at, height, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or TimeoutException)
        {
            // The slower way may still manage it.
        }

        try
        {
            return await ThumbnailAsync(path, at, height, cancellationToken);
        }
        catch (Exception exception) when (exception is COMException or UnauthorizedAccessException or FileNotFoundException or ArgumentException)
        {
            return null;
        }
    }

    private static async Task<FrameServer?> ServerForAsync(string path, CancellationToken cancellationToken)
    {
        var open = Kept.Open;
        lock (open)
        {
            if (open.FirstOrDefault(server => string.Equals(server.Path, path, StringComparison.OrdinalIgnoreCase)) is { } found)
            {
                return found;
            }
        }

        if (!File.Exists(path))
        {
            return null;
        }

        var opened = await FrameServer.OpenAsync(path, cancellationToken);
        if (opened is null)
        {
            return null;
        }

        lock (open)
        {
            open.Add(opened);
            while (open.Count > MostOpen)
            {
                var oldest = open.MinBy(server => server.LastUsed)!;
                open.Remove(oldest);
                oldest.Dispose();
            }
        }

        return opened;
    }

    private static void CloseIdle()
    {
        var open = Kept.Open;
        lock (open)
        {
            foreach (var server in open.Where(server => DateTime.UtcNow - server.LastUsed > IdleFor && !server.Busy).ToList())
            {
                open.Remove(server);
                server.Dispose();
            }
        }
    }

    private static async Task<VideoPicture?> ThumbnailAsync(string path, TimeSpan at, int height, CancellationToken cancellationToken)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask(cancellationToken);
        var composition = new MediaComposition();
        composition.Clips.Add(await MediaClip.CreateFromFileAsync(file).AsTask(cancellationToken));

        using var stream = await composition.GetThumbnailAsync(at, 0, height, VideoFramePrecision.NearestFrame).AsTask(cancellationToken);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);
        return new VideoPicture((int)decoder.PixelWidth, (int)decoder.PixelHeight, pixels.DetachPixelData());
    }

    /// <summary>
    /// The players kept open. They're in a class of their own so nothing that needs the library is touched before it's
    /// made reachable, and videos left alone for a while are let go.
    /// </summary>
    private static class Kept
    {
        public static readonly List<FrameServer> Open = [];
        public static readonly Timer Tidy = new(_ => CloseIdle(), null, IdleFor, IdleFor);
    }

    /// <summary>Windows' player, kept open on one video and asked for one picture at a time.</summary>
    private sealed class FrameServer : IDisposable
    {
        private readonly MediaPlayer _player;
        private readonly SemaphoreSlim _one = new(1, 1);
        private readonly Dictionary<int, VideoFrame> _surfaces = [];
        private TimeSpan? _at;

        private FrameServer(string path, MediaPlayer player)
        {
            Path = path;
            _player = player;
        }

        public string Path { get; }

        public DateTime LastUsed { get; private set; } = DateTime.UtcNow;

        public bool Busy => _one.CurrentCount == 0;

        public static async Task<FrameServer?> OpenAsync(string path, CancellationToken cancellationToken)
        {
            var player = new MediaPlayer { IsVideoFrameServerEnabled = true, IsMuted = true, AutoPlay = false };
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            player.MediaOpened += (_, _) => ready.TrySetResult(true);
            player.MediaFailed += (_, _) => ready.TrySetResult(false);
            player.Source = MediaSource.CreateFromStorageFile(await StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken));

            var opened = await ready.Task.WaitAsync(LongestWait * 5, cancellationToken);
            if (!opened || player.PlaybackSession.NaturalVideoHeight == 0)
            {
                player.Dispose();
                return null;
            }

            return new FrameServer(path, player);
        }

        public async Task<VideoPicture?> ReadAsync(TimeSpan at, int height, CancellationToken cancellationToken)
        {
            await _one.WaitAsync(cancellationToken);
            try
            {
                LastUsed = DateTime.UtcNow;
                var session = _player.PlaybackSession;

                // Asked for where it already is, it says nothing, so it isn't asked.
                if (_at != at)
                {
                    // On a slow PC the first picture can take longer than the wait. Copying then gives black, not the
                    // picture, so the slower way is used instead.
                    var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var seeked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    void Available(MediaPlayer sender, object args) => frame.TrySetResult();
                    void Seeked(MediaPlaybackSession sender, object args) => seeked.TrySetResult();
                    _player.VideoFrameAvailable += Available;
                    session.SeekCompleted += Seeked;
                    _at = null;
                    try
                    {
                        session.Position = at;
                        var both = Task.WhenAll(seeked.Task, frame.Task);
                        await Task.WhenAny(both, Task.Delay(LongestWait, cancellationToken));
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!both.IsCompleted)
                        {
                            throw new TimeoutException("Windows' player didn't hand over the picture in time.");
                        }
                    }
                    finally
                    {
                        _player.VideoFrameAvailable -= Available;
                        session.SeekCompleted -= Seeked;
                    }

                    _at = at;
                }

                // The player turns a phone's video upright itself, so its size is as it's seen.
                var naturalWidth = (int)session.NaturalVideoWidth;
                var naturalHeight = (int)session.NaturalVideoHeight;
                var tall = Math.Clamp(height <= 0 ? naturalHeight : height, 2, naturalHeight);
                if (!_surfaces.TryGetValue(tall, out var surface))
                {
                    var wide = Math.Max(2, (int)Math.Round(naturalWidth * (double)tall / naturalHeight));
                    _surfaces[tall] = surface = VideoFrame.CreateAsDirect3D11SurfaceBacked(DirectXPixelFormat.B8G8R8A8UIntNormalized, wide, tall);
                }

                _player.CopyFrameToVideoSurface(surface.Direct3DSurface);
                using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(surface.Direct3DSurface, BitmapAlphaMode.Premultiplied).AsTask(cancellationToken);
                var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyToBuffer(pixels.AsBuffer());
                return new VideoPicture(bitmap.PixelWidth, bitmap.PixelHeight, pixels);
            }
            finally
            {
                LastUsed = DateTime.UtcNow;
                _one.Release();
            }
        }

        public void Dispose()
        {
            foreach (var surface in _surfaces.Values)
            {
                surface.Dispose();
            }

            _surfaces.Clear();
            _player.Dispose();
        }
    }
}
