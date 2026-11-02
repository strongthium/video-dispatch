using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VideoDispatch.App;
using VideoDispatch.Core.Editing;
using VideoDispatch.Core.Media;
using VideoDispatch.Editing;
using VideoDispatch.Tests.Reencoding;
using WinColor = Windows.UI.Color;

namespace VideoDispatch.Tests;

/// <summary>
/// The Edit page in a real window, playing real videos with WPF's own player, as the app does. Each second of a test
/// video is its own colour, so the picture says which part of which video is playing.
/// </summary>
public sealed class EditPageTests : IDisposable
{
    private readonly string _folder = Path.Combine(AppContext.BaseDirectory, "edit-page-" + Guid.NewGuid().ToString("N"));

    public EditPageTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // The player can hold a file a moment after its window has gone.
        }
    }

    private static TimeSpan Seconds(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public async Task Plays_one_clip_after_another_across_videos_and_stops_at_the_end()
    {
        await WindowsVideoEncoderTests.SkipWithoutMediaToolsAsync();
        var first = await TestVideos.MakeSecondsAsync(_folder, "first.mp4", 640, 360, true, WinColor.FromArgb(255, 255, 0, 0), WinColor.FromArgb(255, 0, 255, 0), WinColor.FromArgb(255, 0, 0, 255));
        var second = await TestVideos.MakeSecondsAsync(_folder, "second.mp4", 640, 360, true, WinColor.FromArgb(255, 255, 255, 0), WinColor.FromArgb(255, 255, 0, 255));
        SkipOnGitHubRunners();

        // Green from the first, yellow from the second, then blue from the first again.
        var project = new EditProject(
        [
            new EditClip(first, Seconds(3), Seconds(1), Seconds(2)),
            new EditClip(second, Seconds(2), Seconds(0), Seconds(1)),
            new EditClip(first, Seconds(3), Seconds(2), Seconds(3)),
        ]);

        var seen = await OnWindowThreadAsync(async (page, panel) =>
        {
            // There are two players, one opening the next video while the other plays, so whichever is showing is looked at.
            var samples = new List<(double At, string Colour)>();
            var players = new[] { (MediaElement)page.FindName("Player"), (MediaElement)page.FindName("Standby") };
            ((Button)page.FindName("PlayButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.Elapsed < Seconds(6))
            {
                await Task.Delay(100);
                if (players.FirstOrDefault(player => player.Visibility == Visibility.Visible) is { } shown)
                {
                    samples.Add((panel.Position.TotalSeconds, ColourOf(shown)));
                }
            }

            samples.Add((panel.Position.TotalSeconds, "end"));
            return samples;
        }, project);

        var shown = string.Join(" ", seen.Select(sample => $"{sample.At:0.00}={sample.Colour}"));
        string Mostly(double from, double to) => seen.Where(sample => sample.At >= from && sample.At <= to).GroupBy(sample => sample.Colour).OrderByDescending(group => group.Count()).FirstOrDefault()?.Key ?? "nothing";

        Assert.True(Mostly(0.2, 0.8) == "green", shown);
        Assert.True(Mostly(1.2, 1.8) == "yellow", shown);
        Assert.True(Mostly(2.2, 2.8) == "blue", shown);

        // Nothing from outside the clips: the first video's red second, or the second's magenta one.
        Assert.False(seen.Any(sample => sample.Colour is "red" or "magenta"), shown);
        Assert.Equal(3, seen[^1].At, 0.01);
    }

    [Fact]
    public async Task A_sideways_phone_video_plays_the_right_way_up()
    {
        // Stored landscape with red on the left, and marked to be shown turned a quarter clockwise: red on top.
        await WindowsVideoEncoderTests.SkipWithoutMediaToolsAsync();
        var phone = await TestVideos.MakeAsync(_folder, "phone.mp4", 640, 360);
        WindowsVideoEncoderTests.MarkRotatedClockwise(phone);
        SkipOnGitHubRunners();
        var project = new EditProject([EditClip.Whole(phone, Seconds(2))]);

        var (top, bottom, tall) = await OnWindowThreadAsync(async (page, panel) =>
        {
            var player = (MediaElement)page.FindName("Player");
            ((Button)page.FindName("PlayButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            for (var wait = 0; wait < 30 && (player.Visibility != Visibility.Visible || panel.Position < Seconds(0.5)); wait++)
            {
                await Task.Delay(100);
            }

            // The preview's frame, which is the saved video's shape: tall, and filled by the turned picture.
            var shown = (FrameworkElement)player.Parent;
            var picture = Picture(shown);
            return (ColourAt(picture, shown.ActualWidth / 2, shown.ActualHeight / 4),
                ColourAt(picture, shown.ActualWidth / 2, shown.ActualHeight * 3 / 4),
                shown.ActualHeight > shown.ActualWidth);
        }, project);

        Assert.Equal("red", top);
        Assert.Equal("blue", bottom);
        Assert.True(tall, "The frame should be tall.");
    }

    [Fact]
    public async Task Paused_the_preview_shows_zoom_fades_and_words_as_saving_does()
    {
        // Red on the left half, blue on the right; zoomed into the right half, darkened halfway by a fade, with words.
        await WindowsVideoEncoderTests.SkipWithoutMediaToolsAsync();
        var halves = await TestVideos.MakeAsync(_folder, "halves.mp4", 640, 360);
        var project = new EditProject([EditClip.Whole(halves, Seconds(2)) with { Zoom = 2, ZoomX = 0.75, ZoomY = 0.5, FadeIn = Seconds(1) }])
        {
            Texts = [new EditText("Hello", Seconds(0), Seconds(1))],
        };

        var (left, darkness, words) = await OnWindowThreadAsync(async (page, panel) =>
        {
            // The first picture read can take a few seconds on a slow PC, so this waits for it rather than a set time.
            panel.Position = Seconds(0.5);
            var still = (Image)page.FindName("Still");
            for (var wait = 0; wait < 100 && still.Source is null; wait++)
            {
                await Task.Delay(100);
            }

            await Task.Delay(300);
            var pictures = (FrameworkElement)page.FindName("Pictures");
            var shown = ColourAt(Picture(pictures), 8, pictures.ActualHeight / 2);
            return (shown, ((FrameworkElement)page.FindName("Darken")).Opacity, ((Panel)page.FindName("Words")).Children.Count);
        }, project);

        Assert.Equal("blue", left);
        Assert.Equal(0.5, darkness, 2);
        Assert.Equal(1, words);
    }

    [Fact]
    public async Task Saving_from_the_window_draws_the_words_into_the_video()
    {
        // Large black words in a white box, in the middle of a blue picture.
        await WindowsVideoEncoderTests.SkipWithoutMediaToolsAsync();
        var blue = await TestVideos.MakeSecondsAsync(_folder, "blue.mp4", 640, 360, true, WinColor.FromArgb(255, 0, 0, 255), WinColor.FromArgb(255, 0, 0, 255));
        var project = new EditProject([EditClip.Whole(blue, Seconds(2))])
        {
            Texts = [new EditText("WWWWWWWW", Seconds(0.5), Seconds(1)) { Place = TextPlace.Middle, Size = TextSize.Large, Colour = TextColour.Black, Box = true }],
        };
        var output = Path.Combine(_folder, "saved.mp4");

        var message = await OnWindowThreadAsync(async (_, panel) =>
        {
            await panel.SaveAsync(output);
            return panel.Message;
        }, project);

        Assert.Equal("Saved as saved.mp4.", message);
        var during = await WindowsVideoEncoderTests.ReadFrameAsync(output, 1);
        var after = await WindowsVideoEncoderTests.ReadFrameAsync(output, 1.75);
        Assert.Equal("blue", after.ColourInTheMiddle);
        Assert.Equal("blue", during.ColourAt(20, 20));

        // Somewhere across the middle, the box or the words.
        var across = Enumerable.Range(0, 64).Select(step => during.ColourAt(during.Width * step / 64, during.Height / 2)).ToList();
        Assert.Contains("white", across);
        Assert.Contains("black", across);
    }

    /// <summary>
    /// On GitHub's runners the preview's player opens a video but never reaches where it's sent, so nothing is shown,
    /// though WPF's player on its own plays there. Why hasn't been found: there's no sound device or graphics card
    /// there, and it can only be seen by pushing to deploy. So playing is tested on a PC, and skipped there.
    /// </summary>
    private static void SkipOnGitHubRunners() =>
        Assert.SkipWhen(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true", "The preview's player doesn't play on GitHub's runners.");

    private static RenderTargetBitmap Picture(FrameworkElement element)
    {
        var picture = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        picture.Render(element);
        return picture;
    }

    private static string ColourAt(BitmapSource picture, double x, double y)
    {
        var pixel = new byte[4];
        picture.CopyPixels(new Int32Rect((int)x, (int)y, 1, 1), pixel, 4, 0);
        return (pixel[2] > 128, pixel[1] > 128, pixel[0] > 128) switch
        {
            (true, false, false) => "red",
            (false, true, false) => "green",
            (false, false, true) => "blue",
            (true, true, false) => "yellow",
            (true, false, true) => "magenta",
            _ => "other",
        };
    }

    private static string ColourOf(FrameworkElement element)
    {
        if (element.ActualWidth < 2 || element.ActualHeight < 2)
        {
            return "no picture yet";
        }

        var picture = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        picture.Render(element);
        var pixel = new byte[4];
        picture.CopyPixels(new Int32Rect(picture.PixelWidth / 2, picture.PixelHeight / 2, 1, 1), pixel, 4, 0);
        return (pixel[2] > 128, pixel[1] > 128, pixel[0] > 128) switch
        {
            (true, false, false) => "red",
            (false, true, false) => "green",
            (false, false, true) => "blue",
            (true, true, false) => "yellow",
            (true, false, true) => "magenta",
            _ => "other",
        };
    }

    /// <summary>
    /// One thread for every window in these tests, because the app's theme, where the page's colours come from, belongs
    /// to the thread that made it.
    /// </summary>
    private static readonly Lazy<Dispatcher> WindowThread = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() =>
        {
            var app = new App.App();
            app.InitializeComponent();
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        })
        {
            IsBackground = true,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.Result;
    });

    /// <summary>Shows the page in a window on the window thread, runs <paramref name="work"/> there, and closes it.</summary>
    private static Task<T> OnWindowThreadAsync<T>(Func<EditPage, EditPanel, Task<T>> work, EditProject project)
    {
        var done = new TaskCompletionSource<T>();
        WindowThread.Value.InvokeAsync(() =>
        {
            var panel = new EditPanel(new EditStore(null, project), Mp4MetadataReader.Read, (_, _) => Task.FromResult<TimeSpan?>(null), () => new WindowsVideoEncoder(), File.Exists);
            var page = new EditPage(async (path, at, height, cancel) =>
                await WindowsVideoReader.ReadFrameAsync(path, at, height, cancel) is { } read ? Services.ToImage(read) : null)
            {
                DataContext = panel,
            };

            var window = new Window { Width = 900, Height = 760, Content = page, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            window.Loaded += async (_, _) =>
            {
                try
                {
                    await panel.LoadAsync();
                    await Task.Delay(500);
                    done.SetResult(await work(page, panel));
                }
                catch (Exception exception)
                {
                    done.SetException(exception);
                }
                finally
                {
                    window.Close();
                }
            };

            window.Show();
        });

        return done.Task;
    }
}
