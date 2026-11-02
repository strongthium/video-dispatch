using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using VideoDispatch.Core.Editing;

namespace VideoDispatch.App;

/// <summary>
/// Draws the edit and turns the mouse and keys into changes to it. The preview is a still read from the video when
/// paused, which lands on the exact frame, and WPF's own player while playing, one clip after another. WPF's player was
/// tried for the stills too, and when paused it sometimes kept showing an earlier picture.
/// </summary>
public sealed partial class EditPage : UserControl
{
    private const double ClipTop = 24;
    private const double ClipHeight = 76;
    private const double EdgeGrip = 7;
    private const double RoomAfter = 80;
    private const double DragStarts = 4;
    private const int StillHeight = 540;
    private const int PictureHeight = 144;
    private static readonly TimeSpan OneFrame = TimeSpan.FromSeconds(1.0 / 30);
    private static readonly double[] RulerSteps = [0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800];

    private readonly Func<string, TimeSpan, int, CancellationToken, Task<ImageSource?>> _readFrame;
    private readonly Dictionary<(string Path, long Ticks), ImageSource?> _pictures = [];
    private readonly DispatcherTimer _clock = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };

    private EditPanel? _panel;
    private EditProject? _drawn;
    private EditProject? _trimmed;
    private Drag? _drag;
    private Rectangle? _playhead;
    private Rectangle? _landing;
    private CancellationTokenSource? _stillLoad;
    private TimeSpan? _stillAt;
    private bool _playing;
    private bool _switching;
    private bool _fitted;
    private bool _fitting;
    private bool _zoomChosen;
    private int _playingIndex = -1;
    private TimeSpan _playingClipStart;
    private string? _openSource;
    private DateTime _seekedAt;

    /// <param name="readFrame">The picture in a video at a moment of its own, at a height, or null if it can't be read.</param>
    public EditPage(Func<string, TimeSpan, int, CancellationToken, Task<ImageSource?>> readFrame)
    {
        InitializeComponent();
        _readFrame = readFrame;
        _clock.Tick += async (_, _) => await TickAsync();

        DataContextChanged += (_, _) =>
        {
            if (_panel is not null)
            {
                _panel.PropertyChanged -= Panel_PropertyChanged;
            }

            _panel = DataContext as EditPanel;
            if (_panel is not null)
            {
                _panel.PropertyChanged += Panel_PropertyChanged;
            }

            Redraw();
        };

        Loaded += (_, _) =>
        {
            ZoomToFit();
            Redraw();
            ShowStill();
            Focus();
        };
        Unloaded += (_, _) => Stop();
        TimelineScroll.SizeChanged += (_, _) =>
        {
            // Until the zoom is chosen, all of the edit stays in view.
            if (!_zoomChosen)
            {
                _fitted = false;
                ZoomToFit();
            }

            Redraw();
        };
        Player.MediaFailed += (_, _) => Stop();

        Timeline.MouseLeftButtonDown += Timeline_MouseDown;
        Timeline.MouseMove += Timeline_MouseMove;
        Timeline.MouseLeftButtonUp += Timeline_MouseUp;
        Timeline.LostMouseCapture += (_, _) => EndDrag(commit: false);
        PreviewKeyDown += Page_KeyDown;
        DragOver += Page_DragOver;
        Drop += Page_Drop;
    }

    private enum DragKind
    {
        Scrub,
        Pending,
        Move,
        TrimStart,
        TrimEnd,
    }

    private EditPanel Panel => _panel!;

    /// <summary>From about 8 to 400 pixels a second.</summary>
    private double PixelsPerSecond => 8 * Math.Pow(50, Zoom.Value);

    private EditProject Shown => _trimmed ?? Panel.Project;

    private void Panel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(Panel.Project, _drawn) && _drag is null)
        {
            if (_playing)
            {
                Stop();
            }

            // Videos into an empty edit: show all of them.
            if (_drawn is not { Clips.Count: > 0 })
            {
                _fitted = false;
                ZoomToFit();
            }

            Redraw();
            _stillAt = null;
        }

        MessageText.Foreground = (Brush)FindResource(Panel.MessageIsError ? "SystemFillColorCriticalBrush" : "TextFillColorPrimaryBrush");
        MovePlayhead();
        if (!_playing && _drag?.Kind is not (DragKind.TrimStart or DragKind.TrimEnd) && _stillAt != Panel.Position)
        {
            ShowStill();
        }
    }

    // Drawing

    private void Redraw()
    {
        if (_panel is null || !IsLoaded)
        {
            return;
        }

        var project = Shown;
        var perSecond = PixelsPerSecond;
        Timeline.Children.Clear();
        Timeline.Width = Math.Max(TimelineScroll.ViewportWidth, (project.Length.TotalSeconds * perSecond) + RoomAfter);

        DrawRuler(perSecond);
        var left = 0d;
        for (var index = 0; index < project.Clips.Count; index++)
        {
            var clip = project.Clips[index];
            var width = clip.Length.TotalSeconds * perSecond;
            var block = DrawClip(clip, index, width);
            Canvas.SetLeft(block, left);
            Canvas.SetTop(block, ClipTop);
            Timeline.Children.Add(block);
            left += width;
        }

        _landing = new Rectangle { Width = 3, Height = ClipHeight + 8, Fill = Brush("AccentFillColorDefaultBrush"), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        Canvas.SetTop(_landing, ClipTop - 4);
        Canvas.SetZIndex(_landing, 2);
        Timeline.Children.Add(_landing);

        _playhead = new Rectangle { Width = 2, Height = Timeline.Height, Fill = Brush("TextFillColorPrimaryBrush"), IsHitTestVisible = false };
        Canvas.SetZIndex(_playhead, 3);
        Timeline.Children.Add(_playhead);
        MovePlayhead();

        if (_trimmed is null)
        {
            _drawn = Panel.Project;
        }
    }

    private void DrawRuler(double perSecond)
    {
        var step = RulerSteps.FirstOrDefault(seconds => seconds * perSecond >= 70, RulerSteps[^1]);
        var muted = Brush("TextFillColorSecondaryBrush");
        for (var seconds = 0d; seconds * perSecond < Timeline.Width; seconds += step)
        {
            var x = seconds * perSecond;
            var tick = new Rectangle { Width = 1, Height = 6, Fill = muted, IsHitTestVisible = false };
            Canvas.SetLeft(tick, x);
            Canvas.SetTop(tick, 14);
            Timeline.Children.Add(tick);

            var label = new TextBlock { Text = RulerLabel(TimeSpan.FromSeconds(seconds), step), FontSize = 10, Foreground = muted, IsHitTestVisible = false };
            Canvas.SetLeft(label, x + 3);
            Canvas.SetTop(label, 1);
            Timeline.Children.Add(label);
        }
    }

    private static string RulerLabel(TimeSpan time, double step) => step < 1
        ? time.ToString(@"m\:ss\.f", CultureInfo.InvariantCulture)
        : time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    private Border DrawClip(EditClip clip, int index, double width)
    {
        var chosen = index == Panel.SelectedIndex;
        // The clip's first picture, again and again along it, like film.
        var content = new Grid();
        if (PictureFor(clip) is { } picture && picture.Height > 0)
        {
            var across = ClipHeight * picture.Width / picture.Height;
            content.Background = new ImageBrush(picture)
            {
                TileMode = TileMode.Tile,
                Stretch = Stretch.Uniform,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, across, ClipHeight),
                Opacity = 0.85,
            };
        }

        content.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xAA, 0, 0, 0)),
            VerticalAlignment = VerticalAlignment.Bottom,
            Padding = new Thickness(5, 1, 5, 2),
            Child = new TextBlock
            {
                Text = $"{clip.Name}  {EditPanel.Format(clip.Length)}",
                Foreground = Brushes.White,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
        });

        // Grips on the edges of the chosen clip, where dragging trims it.
        if (chosen)
        {
            foreach (var side in new[] { HorizontalAlignment.Left, HorizontalAlignment.Right })
            {
                content.Children.Add(new Border { Width = 5, Margin = new Thickness(1, 14, 1, 14), CornerRadius = new CornerRadius(2), HorizontalAlignment = side, Background = Brush("AccentFillColorDefaultBrush") });
            }
        }

        return new Border
        {
            Width = Math.Max(width - 2, 3),
            Height = ClipHeight,
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(chosen ? 2 : 1),
            BorderBrush = Brush(chosen ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush"),
            Background = Brush("SubtleFillColorSecondaryBrush"),
            ClipToBounds = true,
            Tag = index,
            Child = content,
        };
    }

    private void MovePlayhead()
    {
        if (_playhead is null || _panel is null)
        {
            return;
        }

        var x = Panel.Position.TotalSeconds * PixelsPerSecond;
        Canvas.SetLeft(_playhead, x - 1);

        // Keep it in sight while playing.
        if (_playing && (x < TimelineScroll.HorizontalOffset || x > TimelineScroll.HorizontalOffset + TimelineScroll.ViewportWidth - 20))
        {
            TimelineScroll.ScrollToHorizontalOffset(Math.Max(0, x - 40));
        }
    }

    private Brush Brush(string key) => (Brush)FindResource(key);

    // Pictures

    private ImageSource? PictureFor(EditClip clip)
    {
        var key = (clip.SourcePath, clip.Start.Ticks);
        if (_pictures.TryGetValue(key, out var picture) && picture is not null)
        {
            return picture;
        }

        // While an edge is dragged, each step would ask for a picture, so the one there is stands in until it's let go.
        if (_trimmed is null && !_pictures.ContainsKey(key))
        {
            _pictures[key] = null;
            _ = LoadPictureAsync(clip, key);
        }

        return _pictures.FirstOrDefault(found => found.Value is not null && string.Equals(found.Key.Path, clip.SourcePath, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private async Task LoadPictureAsync(EditClip clip, (string, long) key)
    {
        var at = clip.Start + TimeSpan.FromMilliseconds(50) < clip.End ? clip.Start + TimeSpan.FromMilliseconds(50) : clip.Start;
        var picture = await _readFrame(clip.SourcePath, at, PictureHeight, CancellationToken.None);
        if (picture is not null)
        {
            _pictures[key] = picture;
            if (_drag is null)
            {
                Redraw();
            }
        }
    }

    /// <summary>Shows the exact picture under the playhead, after a moment's pause so dragging doesn't queue up dozens.</summary>
    private async void ShowStill()
    {
        if (_panel is null)
        {
            return;
        }

        _stillLoad?.Cancel();
        _stillAt = Panel.Position;
        if (Shown.Find(Panel.Position) is not { } moment)
        {
            Still.Source = null;
            return;
        }

        var clip = Shown.Clips[moment.Index];
        await ShowStillAsync(clip.SourcePath, moment.SourceTime < clip.End - OneFrame ? moment.SourceTime : Max(clip.Start, clip.End - OneFrame));
    }

    private async Task ShowStillAsync(string path, TimeSpan at)
    {
        _stillLoad?.Cancel();
        using var load = new CancellationTokenSource();
        _stillLoad = load;

        try
        {
            await Task.Delay(60, load.Token);
            var picture = await _readFrame(path, at, StillHeight, load.Token);
            if (!load.IsCancellationRequested && picture is not null && !_playing)
            {
                Still.Source = picture;
                Player.Visibility = Visibility.Hidden;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_stillLoad == load)
            {
                _stillLoad = null;
            }
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    // Playing

    private async Task PlayAsync()
    {
        if (!Panel.HasClips)
        {
            return;
        }

        if (Panel.Position >= Panel.Length - OneFrame)
        {
            Panel.Position = TimeSpan.Zero;
        }

        _stillLoad?.Cancel();
        _playing = true;
        PlayIcon.Text = "";
        if (Panel.Project.Find(Panel.Position) is { } moment && await StartClipAsync(moment))
        {
            _clock.Start();
        }
    }

    private async Task<bool> StartClipAsync(EditMoment moment)
    {
        var clip = Panel.Project.Clips[moment.Index];
        _switching = true;
        try
        {
            // Until the player has got to where the clip starts, the last picture it showed stays up in its place.
            if (Player.Visibility == Visibility.Visible && Snapshot() is { } last)
            {
                Still.Source = last;
            }

            Player.Visibility = Visibility.Hidden;

            if (!string.Equals(_openSource, clip.SourcePath, StringComparison.OrdinalIgnoreCase))
            {
                var opened = new TaskCompletionSource<bool>();
                void Opened(object? sender, RoutedEventArgs e) => opened.TrySetResult(true);
                void Failed(object? sender, ExceptionRoutedEventArgs e) => opened.TrySetResult(false);
                Player.MediaOpened += Opened;
                Player.MediaFailed += Failed;
                try
                {
                    // WPF's player ignores the mark a phone leaves to show its video turned upright.
                    var turn = Panel.RotationOf(clip.SourcePath);
                    Player.LayoutTransform = turn == 0 ? Transform.Identity : new RotateTransform(turn);

                    // Opened playing: opened paused, it ignored the jump that follows.
                    Player.Source = new Uri(clip.SourcePath);
                    Player.Play();
                    var done = await Task.WhenAny(opened.Task, Task.Delay(TimeSpan.FromSeconds(10)));
                    if (done != opened.Task || !await opened.Task)
                    {
                        _openSource = null;
                        Stop();
                        return false;
                    }
                }
                finally
                {
                    Player.MediaOpened -= Opened;
                    Player.MediaFailed -= Failed;
                }

                _openSource = clip.SourcePath;
            }

            if (!_playing)
            {
                return false;
            }

            Player.Position = moment.SourceTime;
            _seekedAt = DateTime.UtcNow;
            Player.Play();
            _playingIndex = moment.Index;
            _playingClipStart = moment.ClipStart;
            return true;
        }
        finally
        {
            _switching = false;
        }
    }

    private async Task TickAsync()
    {
        if (!_playing || _switching || _playingIndex < 0 || _playingIndex >= Panel.Project.Clips.Count)
        {
            return;
        }

        // Just after a jump, the player can still say where it was.
        if (DateTime.UtcNow - _seekedAt < TimeSpan.FromMilliseconds(150))
        {
            return;
        }

        var project = Panel.Project;
        var clip = project.Clips[_playingIndex];
        var at = Player.Position;
        if (at < clip.Start - TimeSpan.FromMilliseconds(100))
        {
            // Still on its way there.
            return;
        }

        Player.Visibility = Visibility.Visible;
        if (at < clip.End)
        {
            Panel.Position = _playingClipStart + Max(at - clip.Start, TimeSpan.Zero);
            return;
        }

        var next = _playingIndex + 1;
        if (next >= project.Clips.Count)
        {
            Panel.Position = project.Length;
            Stop();
            return;
        }

        var following = project.Clips[next];
        var start = project.StartOf(next);
        if (string.Equals(following.SourcePath, clip.SourcePath, StringComparison.OrdinalIgnoreCase) && following.Start == clip.End)
        {
            // It carries straight on in the same video.
            _playingIndex = next;
            _playingClipStart = start;
            return;
        }

        await StartClipAsync(new EditMoment(next, start, following.Start));
    }

    /// <summary>What the preview is showing, as a picture: the whole of it, so a turned picture comes out as it's seen.</summary>
    private RenderTargetBitmap? Snapshot()
    {
        if (Player.Parent is not FrameworkElement shown || shown.ActualWidth < 2 || shown.ActualHeight < 2)
        {
            return null;
        }

        var scale = VisualTreeHelper.GetDpi(shown);
        var picture = new RenderTargetBitmap((int)(shown.ActualWidth * scale.DpiScaleX), (int)(shown.ActualHeight * scale.DpiScaleY), scale.PixelsPerInchX, scale.PixelsPerInchY, PixelFormats.Pbgra32);
        picture.Render(shown);
        picture.Freeze();
        return picture;
    }

    private void Stop()
    {
        var was = _playing;
        _playing = false;
        _clock.Stop();
        Player.Pause();
        PlayIcon.Text = "";
        if (was)
        {
            ShowStill();
        }
    }

    // The timeline and the mouse

    private sealed record Drag(DragKind Kind, Point From, int Index, EditClip? Clip, int Landing = -1);

    private TimeSpan TimeAt(double x) => TimeSpan.FromSeconds(Math.Max(0, x) / PixelsPerSecond);

    /// <summary>The clip at <paramref name="x"/>, and whether that's near its start or end edge.</summary>
    private (int Index, DragKind Kind)? ClipAt(Point point)
    {
        if (point.Y < ClipTop || point.Y > ClipTop + ClipHeight)
        {
            return null;
        }

        var perSecond = PixelsPerSecond;
        var left = 0d;
        var clips = Panel.Project.Clips;
        for (var index = 0; index < clips.Count; index++)
        {
            var right = left + (clips[index].Length.TotalSeconds * perSecond);
            if (point.X >= left && point.X < right)
            {
                // A narrow clip is all body, so it can still be moved.
                var grip = Math.Min(EdgeGrip, (right - left) / 4);
                return point.X < left + grip ? (index, DragKind.TrimStart) : point.X > right - grip ? (index, DragKind.TrimEnd) : (index, DragKind.Pending);
            }

            left = right;
        }

        return null;
    }

    /// <summary>Where a clip taken out would land if let go at <paramref name="x"/>: before the first other clip whose middle is past it.</summary>
    private (int Index, double X) LandingAt(double x, int taken)
    {
        var perSecond = PixelsPerSecond;
        var left = 0d;
        var landing = 0;
        for (var index = 0; index < Panel.Project.Clips.Count; index++)
        {
            if (index == taken)
            {
                continue;
            }

            var width = Panel.Project.Clips[index].Length.TotalSeconds * perSecond;
            if (x < left + (width / 2))
            {
                return (landing, left);
            }

            left += width;
            landing++;
        }

        return (landing, left);
    }

    private void Timeline_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_panel is null)
        {
            return;
        }

        Focus();
        var point = e.GetPosition(Timeline);
        if (_playing)
        {
            Stop();
        }

        if (Panel.CanEdit && ClipAt(point) is { } hit)
        {
            Panel.SelectedIndex = hit.Index;
            Redraw();
            if (hit.Kind == DragKind.Pending)
            {
                Panel.Position = TimeAt(point.X);
            }

            _drag = new Drag(hit.Kind, point, hit.Index, Panel.Project.Clips[hit.Index]);
        }
        else
        {
            Panel.Position = TimeAt(point.X);
            _drag = new Drag(DragKind.Scrub, point, -1, null);
        }

        Timeline.CaptureMouse();
        e.Handled = true;
    }

    private void Timeline_MouseMove(object sender, MouseEventArgs e)
    {
        if (_panel is null)
        {
            return;
        }

        var point = e.GetPosition(Timeline);
        if (_drag is null)
        {
            Timeline.Cursor = ClipAt(point) switch
            {
                { Kind: DragKind.TrimStart or DragKind.TrimEnd } => Cursors.SizeWE,
                not null => Cursors.Hand,
                _ => null,
            };
            return;
        }

        var moved = point.X - _drag.From.X;
        switch (_drag.Kind)
        {
            case DragKind.Scrub:
                Panel.Position = TimeAt(point.X);
                break;

            case DragKind.Pending when Math.Abs(moved) > DragStarts:
                _drag = _drag with { Kind = DragKind.Move };
                goto case DragKind.Move;

            case DragKind.Move:
                var block = Timeline.Children.OfType<Border>().FirstOrDefault(found => found.Tag is int index && index == _drag.Index);
                if (block is not null)
                {
                    Canvas.SetLeft(block, (Panel.Project.StartOf(_drag.Index).TotalSeconds * PixelsPerSecond) + moved);
                    Canvas.SetZIndex(block, 1);
                    block.Opacity = 0.8;
                }

                var (landing, x) = LandingAt(point.X, _drag.Index);
                _drag = _drag with { Landing = landing };
                if (_landing is not null)
                {
                    Canvas.SetLeft(_landing, x - 1);
                    _landing.Visibility = Visibility.Visible;
                }

                break;

            case DragKind.TrimStart or DragKind.TrimEnd:
                var seconds = TimeSpan.FromSeconds(moved / PixelsPerSecond);
                var clip = _drag.Clip!;
                _trimmed = _drag.Kind == DragKind.TrimStart
                    ? Panel.Project.TrimStart(_drag.Index, clip.Start + seconds)
                    : Panel.Project.TrimEnd(_drag.Index, clip.End + seconds);
                Redraw();

                // Show the picture at the edge being moved.
                var trimmed = _trimmed.Clips[_drag.Index];
                _ = ShowStillAsync(trimmed.SourcePath, _drag.Kind == DragKind.TrimStart ? trimmed.Start : Max(trimmed.Start, trimmed.End - OneFrame));
                break;
        }
    }

    private void Timeline_MouseUp(object sender, MouseButtonEventArgs e) => EndDrag(commit: true);

    private void EndDrag(bool commit)
    {
        if (_drag is not { } drag)
        {
            return;
        }

        _drag = null;
        var trimmed = _trimmed;
        _trimmed = null;
        if (Timeline.IsMouseCaptured)
        {
            Timeline.ReleaseMouseCapture();
        }

        if (commit)
        {
            switch (drag.Kind)
            {
                case DragKind.Move when drag.Landing >= 0:
                    Panel.Move(drag.Index, drag.Landing);
                    break;

                case DragKind.TrimStart when trimmed is not null:
                    Panel.TrimStart(drag.Index, trimmed.Clips[drag.Index].Start);
                    break;

                case DragKind.TrimEnd when trimmed is not null:
                    Panel.TrimEnd(drag.Index, trimmed.Clips[drag.Index].End);
                    break;
            }
        }

        Redraw();
        ShowStill();
    }

    /// <summary>The first time the edit is shown, all of it fits.</summary>
    private void ZoomToFit()
    {
        if (_fitted || _panel is not { HasClips: true } || TimelineScroll.ActualWidth <= 0)
        {
            return;
        }

        _fitted = true;
        var perSecond = (TimelineScroll.ActualWidth - RoomAfter) / Panel.Length.TotalSeconds;
        _fitting = true;
        Zoom.Value = Math.Clamp(Math.Log(perSecond / 8) / Math.Log(50), Zoom.Minimum, Zoom.Maximum);
        _fitting = false;
    }

    private void Zoom_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_panel is null)
        {
            return;
        }

        _zoomChosen |= !_fitting;

        Redraw();
        var x = Panel.Position.TotalSeconds * PixelsPerSecond;
        TimelineScroll.ScrollToHorizontalOffset(Math.Max(0, x - (TimelineScroll.ViewportWidth / 2)));
    }

    // Keys, buttons and dropped files

    private async void Page_KeyDown(object sender, KeyEventArgs e)
    {
        if (_panel is null || e.OriginalSource is TextBox)
        {
            return;
        }

        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var step = shift ? TimeSpan.FromSeconds(1) : OneFrame;
        e.Handled = true;
        switch (e.Key)
        {
            case Key.Space:
                await TogglePlayAsync();
                break;
            case Key.S when !control:
                Stop();
                Panel.Split();
                break;
            case Key.Delete or Key.Back:
                Stop();
                Panel.DeleteSelected();
                break;
            case Key.Z when control && shift:
            case Key.Y when control:
                Stop();
                Panel.Redo();
                break;
            case Key.Z when control:
                Stop();
                Panel.Undo();
                break;
            case Key.Left:
                Stop();
                Panel.Position -= step;
                break;
            case Key.Right:
                Stop();
                Panel.Position += step;
                break;
            case Key.Home:
                Stop();
                Panel.Position = TimeSpan.Zero;
                break;
            case Key.End:
                Stop();
                Panel.Position = Panel.Length;
                break;
            default:
                e.Handled = false;
                break;
        }
    }

    private async Task TogglePlayAsync()
    {
        if (_playing)
        {
            Stop();
        }
        else
        {
            await PlayAsync();
        }
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        await TogglePlayAsync();
        Focus();
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        Stop();
        Panel.Undo();
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        Stop();
        Panel.Redo();
    }

    private void Split_Click(object sender, RoutedEventArgs e)
    {
        Stop();
        Panel.Split();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        Stop();
        Panel.DeleteSelected();
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add videos",
            Multiselect = true,
            Filter = "Videos|*.mp4;*.mov;*.webm;*.m4v;*.mkv;*.avi;*.wmv;*.mpg;*.mpeg;*.3gp|All files|*.*",
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            Stop();
            await Panel.AddVideosAsync(dialog.FileNames);
        }
    }

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && Panel.CanEdit ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || !Panel.CanEdit)
        {
            return;
        }

        // Dropped on the timeline, the videos go where they were let go. Anywhere else, at the end.
        var point = e.GetPosition(Timeline);
        int? at = point.Y >= 0 && point.Y <= Timeline.ActualHeight && point.X >= 0 ? LandingAt(point.X, -1).Index : null;
        Stop();
        await Panel.AddVideosAsync(files.Where(File.Exists).ToList(), at);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        Stop();
        var dialog = new SaveFileDialog
        {
            Title = "Save the edit as a video",
            FileName = Panel.SuggestedName,
            InitialDirectory = Panel.SuggestedFolder,
            Filter = "MP4 video|*.mp4",
            DefaultExt = ".mp4",
            AddExtension = true,
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            await Panel.SaveAsync(dialog.FileName);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => Panel.CancelSave();

    private void Use_Click(object sender, RoutedEventArgs e) => Panel.UseSaved();

    private void Show_Click(object sender, RoutedEventArgs e)
    {
        if (Panel.SavedPath is { } path)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""))?.Dispose();
        }
    }
}
