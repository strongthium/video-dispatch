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
/// tried for the stills too, and when paused it sometimes kept showing an earlier picture. Zoom, fades and words are
/// drawn over both, the same way saving draws them. While playing, a crossfade shows the next clip's first picture
/// fading in, since there's only one player, and the sounds laid over the edit play alongside it.
/// </summary>
public sealed partial class EditPage : UserControl
{
    private const double ClipTop = 24;
    private const double ClipHeight = 64;
    private const double LaneHeight = 26;
    private const double LaneGap = 4;
    private const double TextHeight = 22;
    private const double EdgeGrip = 7;
    private const double RoomAfter = 80;
    private const double DragStarts = 4;
    private const int StillHeight = 540;
    private const int PictureHeight = 144;
    private const double SnapshotLongestSide = 960;
    private static readonly TimeSpan OneFrame = TimeSpan.FromSeconds(1.0 / 30);
    private static readonly TimeSpan SoundDrift = TimeSpan.FromMilliseconds(300);
    private static readonly double[] RulerSteps = [0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800];
    private static readonly Brush SoundFill = Frozen(Color.FromArgb(0x99, 0x2E, 0x8B, 0x57));
    private static readonly Brush TextFill = Frozen(Color.FromArgb(0x99, 0x7A, 0x5A, 0xF8));
    private static readonly Brush Shade = Frozen(Color.FromArgb(0x77, 0, 0, 0));

    private readonly Func<string, TimeSpan, int, CancellationToken, Task<ImageSource?>> _readFrame;
    private readonly Dictionary<(string Path, long Ticks), ImageSource?> _pictures = [];
    private readonly DispatcherTimer _clock = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly List<(Rect Area, EditItem Item, int Index)> _hits = [];
    private readonly Dictionary<int, SoundLayer> _soundLayers = [];

    private EditPanel? _panel;
    private EditProject? _drawn;
    private (EditItem, int, int, int) _drawnChoice;
    private EditProject? _trimmed;
    private Drag? _drag;
    private ZoomDrag? _zoomDrag;
    private Rectangle? _playhead;
    private Rectangle? _landing;
    private CancellationTokenSource? _stillLoad;
    private CancellationTokenSource? _overLoad;
    private TimeSpan? _stillAt;
    private (string? Under, TimeSpan UnderAt, string? Over, TimeSpan OverAt) _stillKey;
    private List<EditText> _shownTexts = [];
    private (double Width, double Height) _wordsSize;
    private double _soundTop;
    private double _textTop;
    private bool _playing;
    private bool _switching;
    private bool _fitted;
    private bool _fitting;
    private bool _zoomChosen;
    private int _playingIndex = -1;
    private TimeSpan _playingClipStart;
    private string? _openSource;
    private DateTime _seekedAt;
    private ImageSource? _nextPicture;

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
        Unloaded += (_, _) =>
        {
            Stop();
            CloseSoundLayers();
        };
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
        Screen.MouseLeftButtonDown += Frame_MouseDown;
        Screen.MouseMove += Frame_MouseMove;
        Screen.MouseLeftButtonUp += (_, _) => EndZoomDrag();
        Screen.LostMouseCapture += (_, _) => _zoomDrag = null;
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

    private static SolidColorBrush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }

    private void Panel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(Panel.Project, _drawn) && _drag is null)
        {
            if (_playing)
            {
                Stop();
            }

            CloseSoundLayers();

            // Videos into an empty edit: show all of them.
            if (_drawn is not { Clips.Count: > 0 })
            {
                _fitted = false;
                ZoomToFit();
            }

            Redraw();
            _stillAt = null;
        }
        else if ((Panel.Chosen, Panel.SelectedIndex, Panel.SelectedSound, Panel.SelectedText) != _drawnChoice && _drag is null)
        {
            // Something else chosen, so the outline moves.
            Redraw();
        }

        MessageText.Foreground = (Brush)FindResource(Panel.MessageIsError ? "SystemFillColorCriticalBrush" : "TextFillColorPrimaryBrush");
        MovePlayhead();
        if (!_playing && _drag?.Kind is not (DragKind.TrimStart or DragKind.TrimEnd) && _stillAt != Panel.Position)
        {
            ShowStill();
        }
        else if (!_playing)
        {
            ApplyView(Shown, Panel.Position);
        }
    }

    // Drawing

    private void Redraw()
    {
        if (_panel is null || !IsLoaded)
        {
            return;
        }

        var (frameWidth, frameHeight) = Panel.FrameSize;
        Screen.Width = frameWidth;
        Screen.Height = frameHeight;

        var project = Shown;
        var perSecond = PixelsPerSecond;
        Timeline.Children.Clear();
        _hits.Clear();

        var lanes = SoundLanes(project, out var laneCount);
        _soundTop = ClipTop + ClipHeight + 8;
        _textTop = _soundTop + (Math.Max(1, laneCount) * (LaneHeight + LaneGap)) + 4;
        Timeline.Height = _textTop + TextHeight + 8;
        Timeline.Width = Math.Max(TimelineScroll.ViewportWidth, (project.Length.TotalSeconds * perSecond) + RoomAfter);

        DrawRuler(perSecond);
        for (var index = 0; index < project.Clips.Count; index++)
        {
            var left = project.StartOf(index).TotalSeconds * perSecond;
            var width = project.Clips[index].Length.TotalSeconds * perSecond;
            Place(DrawClip(project, index, width), new Rect(left, ClipTop, width, ClipHeight), EditItem.Clip, index);
        }

        if (project.Sounds.Count == 0)
        {
            Hint("Music and other sounds go here. Choose Sound, or drop them on this line.", _soundTop);
        }

        for (var index = 0; index < project.Sounds.Count; index++)
        {
            var sound = project.Sounds[index];
            var width = sound.Length.TotalSeconds * perSecond;
            var top = _soundTop + (lanes[index] * (LaneHeight + LaneGap));
            Place(DrawSound(project, index, width), new Rect(sound.At.TotalSeconds * perSecond, top, width, LaneHeight), EditItem.Sound, index);
        }

        if (project.Texts.Count == 0)
        {
            Hint("Words over the picture go here. Choose Words to add some.", _textTop);
        }

        for (var index = 0; index < project.Texts.Count; index++)
        {
            var text = project.Texts[index];
            var width = text.Length.TotalSeconds * perSecond;
            Place(DrawWords(text, index, width), new Rect(text.At.TotalSeconds * perSecond, _textTop, width, TextHeight), EditItem.Text, index);
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

        _drawnChoice = (Panel.Chosen, Panel.SelectedIndex, Panel.SelectedSound, Panel.SelectedText);
    }

    /// <summary>Which line each sound goes on: the first one free when it starts, so sounds that overlap are seen apart.</summary>
    private static int[] SoundLanes(EditProject project, out int count)
    {
        var lanes = new int[project.Sounds.Count];
        var ends = new List<TimeSpan>();
        foreach (var index in Enumerable.Range(0, project.Sounds.Count).OrderBy(each => project.Sounds[each].At))
        {
            var sound = project.Sounds[index];
            var lane = ends.FindIndex(end => end <= sound.At);
            if (lane < 0)
            {
                lane = ends.Count;
                ends.Add(sound.Finish);
            }
            else
            {
                ends[lane] = sound.Finish;
            }

            lanes[index] = lane;
        }

        count = ends.Count;
        return lanes;
    }

    private void Place(FrameworkElement element, Rect area, EditItem item, int index)
    {
        Canvas.SetLeft(element, area.X);
        Canvas.SetTop(element, area.Y);
        Timeline.Children.Add(element);
        _hits.Add((area, item, index));
    }

    private void Hint(string text, double top)
    {
        var hint = new TextBlock { Text = text, FontSize = 11, Foreground = Brush("TextFillColorTertiaryBrush"), IsHitTestVisible = false };
        Canvas.SetLeft(hint, 8);
        Canvas.SetTop(hint, top + 5);
        Timeline.Children.Add(hint);
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

    private Border DrawClip(EditProject project, int index, double width)
    {
        var clip = project.Clips[index];
        var chosen = index == Panel.SelectedIndex;
        var perSecond = PixelsPerSecond;

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

        // Fades are shaded corners whose slope is the fade; a crossfade is a lighter band where it overlaps the clip before.
        var overlap = project.OverlapBefore(index).TotalSeconds * perSecond;
        if (overlap > 1)
        {
            content.Children.Add(new Rectangle
            {
                Width = overlap,
                HorizontalAlignment = HorizontalAlignment.Left,
                Fill = new LinearGradientBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF), Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 0),
            });
        }

        var fadeIn = project.FadeInOf(index).TotalSeconds * perSecond;
        if (fadeIn > 1)
        {
            content.Children.Add(new Polygon { Points = [new Point(0, 0), new Point(fadeIn, 0), new Point(0, ClipHeight)], Fill = Shade });
        }

        var fadeOut = project.FadeOutOf(index).TotalSeconds * perSecond;
        var inner = Math.Max(width - 2, 3);
        if (fadeOut > 1)
        {
            content.Children.Add(new Polygon { Points = [new Point(inner - fadeOut, 0), new Point(inner, 0), new Point(inner, ClipHeight)], Fill = Shade });
        }

        var notes = new List<string> { clip.Name, EditPanel.Format(clip.Length) };
        if (clip.IsZoomed)
        {
            notes.Add($"zoom {clip.Zoom * 100:0}%");
        }

        if (clip.Volume <= 0)
        {
            notes.Add("muted");
        }

        content.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xAA, 0, 0, 0)),
            VerticalAlignment = VerticalAlignment.Bottom,
            Padding = new Thickness(5, 1, 5, 2),
            Child = new TextBlock
            {
                Text = string.Join("  ", notes),
                Foreground = Brushes.White,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
        });

        // Grips on the edges of the chosen clip, where dragging trims it.
        if (chosen)
        {
            AddGrips(content, 12);
        }

        return Block(inner, ClipHeight, chosen, Brush("SubtleFillColorSecondaryBrush"), content, (EditItem.Clip, index));
    }

    private Border DrawSound(EditProject project, int index, double width)
    {
        var sound = project.Sounds[index];
        var chosen = index == Panel.SelectedSound;
        var inner = Math.Max(width - 1, 3);
        var content = new Grid();

        // How loud it is along its length, with its fades.
        var line = new Polyline { Stroke = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1, IsHitTestVisible = false };
        const int Points = 40;
        for (var point = 0; point <= Points; point++)
        {
            var gain = project.SoundGain(index, sound.Length * point / Points);
            line.Points.Add(new Point(inner * point / Points, LaneHeight - 3 - (gain * (LaneHeight - 6))));
        }

        content.Children.Add(line);

        // What runs past the end of the edit isn't heard.
        var heard = project.HeardLengthOf(index).TotalSeconds * PixelsPerSecond;
        if (heard < inner - 1)
        {
            content.Children.Add(new Rectangle { Width = inner - heard, HorizontalAlignment = HorizontalAlignment.Right, Fill = Shade });
        }

        content.Children.Add(new TextBlock
        {
            Text = sound.Volume <= 0 ? $"{sound.Name}  muted" : $"{sound.Name}  {sound.Volume * 100:0}%",
            Foreground = Brushes.White,
            FontSize = 11,
            Margin = new Thickness(6, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        if (chosen)
        {
            AddGrips(content, 5);
        }

        return Block(inner, LaneHeight, chosen, SoundFill, content, (EditItem.Sound, index));
    }

    private Border DrawWords(EditText text, int index, double width)
    {
        var chosen = index == Panel.SelectedText;
        var content = new Grid();
        content.Children.Add(new TextBlock
        {
            Text = text.Text.ReplaceLineEndings(" "),
            Foreground = Brushes.White,
            FontSize = 11,
            Margin = new Thickness(6, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        if (chosen)
        {
            AddGrips(content, 4);
        }

        return Block(Math.Max(width - 1, 3), TextHeight, chosen, TextFill, content, (EditItem.Text, index));
    }

    private void AddGrips(Grid content, double inset)
    {
        foreach (var side in new[] { HorizontalAlignment.Left, HorizontalAlignment.Right })
        {
            content.Children.Add(new Border { Width = 5, Margin = new Thickness(1, inset, 1, inset), CornerRadius = new CornerRadius(2), HorizontalAlignment = side, Background = Brush("AccentFillColorDefaultBrush") });
        }
    }

    private Border Block(double width, double height, bool chosen, Brush background, UIElement content, (EditItem, int) tag) => new()
    {
        Width = width,
        Height = height,
        CornerRadius = new CornerRadius(5),
        BorderThickness = new Thickness(chosen ? 2 : 1),
        BorderBrush = Brush(chosen ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush"),
        Background = background,
        ClipToBounds = true,
        Tag = tag,
        Child = content,
    };

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

    /// <summary>
    /// Shows the exact picture under the playhead, and while one clip fades into another, that one over it, after a
    /// moment's pause so dragging doesn't queue up dozens. Nothing is read again if the pictures haven't changed.
    /// </summary>
    private async void ShowStill()
    {
        if (_panel is null)
        {
            return;
        }

        _stillAt = Panel.Position;
        var project = Shown;
        if (project.ViewAt(Panel.Position) is not { } view)
        {
            _stillLoad?.Cancel();
            _overLoad?.Cancel();
            Still.Source = null;
            OverStill.Opacity = 0;
            _stillKey = default;
            ApplyView(project, Panel.Position);
            return;
        }

        var under = project.Clips[view.Under.Index];
        var underAt = Before(under, view.Under.SourceTime);
        var over = view.Over is { } look ? project.Clips[look.Index] : null;
        var overAt = over is null ? TimeSpan.Zero : Before(over, view.Over!.SourceTime);
        ApplyView(project, Panel.Position);

        var key = (under.SourcePath, underAt, over?.SourcePath, overAt);
        if (key == _stillKey && Player.Visibility != Visibility.Visible)
        {
            return;
        }

        _stillKey = key;
        if (over is not null)
        {
            _ = LoadOverAsync(over.SourcePath, overAt, playing: false);
        }

        await ShowStillAsync(under.SourcePath, underAt);
    }

    /// <summary>Just before a clip's very end, where there's still a picture to read.</summary>
    private static TimeSpan Before(EditClip clip, TimeSpan at) => at < clip.End - OneFrame ? at : Max(clip.Start, clip.End - OneFrame);

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
                Cover.Visibility = Visibility.Hidden;
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

    /// <summary>The picture of the clip fading in over another: where the playhead is when paused, or its first while playing.</summary>
    private async Task LoadOverAsync(string path, TimeSpan at, bool playing)
    {
        _overLoad?.Cancel();
        using var load = new CancellationTokenSource();
        _overLoad = load;

        try
        {
            var picture = await _readFrame(path, at, StillHeight, load.Token);
            if (!load.IsCancellationRequested && picture is not null && _playing == playing)
            {
                if (playing)
                {
                    _nextPicture = picture;
                }
                else
                {
                    OverStill.Source = picture;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_overLoad == load)
            {
                _overLoad = null;
            }
        }
    }

    /// <summary>Draws what goes over the pictures at <paramref name="at"/> when paused: zoom, the crossfade, the fades and the words.</summary>
    private void ApplyView(EditProject project, TimeSpan at)
    {
        if (project.ViewAt(at) is not { } view)
        {
            Darken.Opacity = 0;
            OverStill.Opacity = 0;
            ShowWords(project, at);
            return;
        }

        StillHost.RenderTransform = ZoomOf(project.Clips[view.Under.Index]);
        if (view.Over is { } over)
        {
            OverHost.RenderTransform = ZoomOf(project.Clips[over.Index]);
            OverStill.Opacity = over.Opacity;
        }
        else
        {
            OverStill.Opacity = 0;
        }

        Darken.Opacity = view.Black;
        ShowWords(project, at);
    }

    /// <summary>Shows the words for <paramref name="at"/>, built again only when they change.</summary>
    private void ShowWords(EditProject project, TimeSpan at)
    {
        var texts = project.TextsAt(at).ToList();
        var size = (Screen.Width, Screen.Height);
        if (texts.SequenceEqual(_shownTexts) && size == _wordsSize)
        {
            return;
        }

        _shownTexts = texts;
        _wordsSize = size;
        Words.Children.Clear();
        foreach (var text in texts)
        {
            Words.Children.Add(TextPicture.Build(text, Screen.Width, Screen.Height));
        }
    }

    /// <summary>The frame zoomed as saving zooms it: the part in view stretched to fill it.</summary>
    private Transform ZoomOf(EditClip clip)
    {
        if (!clip.IsZoomed)
        {
            return Transform.Identity;
        }

        var (left, top, size) = clip.ZoomedView();
        return new MatrixTransform(1 / size, 0, 0, 1 / size, -left * Screen.Width / size, -top * Screen.Height / size);
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
        _overLoad?.Cancel();
        _playing = true;
        PlayIcon.Text = "";
        OverStill.Opacity = 0;
        OpenSoundLayers();
        if (Panel.Project.Find(Panel.Position) is { } moment && await StartClipAsync(moment))
        {
            _clock.Start();
        }
    }

    private async Task<bool> StartClipAsync(EditMoment moment)
    {
        var project = Panel.Project;
        var clip = project.Clips[moment.Index];
        _switching = true;
        try
        {
            // Until the player has got to where the clip starts, the last picture shown stays up in its place.
            if (Snapshot() is { } last)
            {
                Cover.Source = last;
                Cover.Visibility = Visibility.Visible;
            }

            Player.Visibility = Visibility.Hidden;
            OverStill.Opacity = 0;
            _nextPicture = null;

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

            PlayerHost.RenderTransform = ZoomOf(clip);
            Player.Volume = project.ClipGain(moment.Index, moment.SourceTime - clip.Start);
            Player.Position = moment.SourceTime;
            _seekedAt = DateTime.UtcNow;
            Player.Play();
            _playingIndex = moment.Index;
            _playingClipStart = moment.ClipStart;

            // The next clip's first picture, ready to fade in over the end of this one.
            if (project.OverlapAfter(moment.Index) > TimeSpan.Zero)
            {
                var next = project.Clips[moment.Index + 1];
                OverHost.RenderTransform = ZoomOf(next);
                _ = LoadOverAsync(next.SourcePath, next.Start, playing: true);
            }

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
        Cover.Visibility = Visibility.Hidden;
        if (at < clip.End)
        {
            var offset = Max(at - clip.Start, TimeSpan.Zero);
            Panel.Position = _playingClipStart + offset;
            ShowPlaying(project, _playingIndex, offset);
            return;
        }

        var next = _playingIndex + 1;
        if (next >= project.Clips.Count)
        {
            Panel.Position = project.Length;
            Stop();
            return;
        }

        // The part of the next clip that fades in over this one has been shown, as its first picture, so it starts after that.
        var following = project.Clips[next];
        var overlap = project.OverlapBefore(next);
        var start = project.StartOf(next);
        if (overlap == TimeSpan.Zero && string.Equals(following.SourcePath, clip.SourcePath, StringComparison.OrdinalIgnoreCase) && following.Start == clip.End && following.Zoom == clip.Zoom && following.ZoomX == clip.ZoomX && following.ZoomY == clip.ZoomY)
        {
            // It carries straight on in the same video.
            _playingIndex = next;
            _playingClipStart = start;
            return;
        }

        await StartClipAsync(new EditMoment(next, start, following.Start + overlap));
    }

    /// <summary>What goes with the playing picture at <paramref name="offset"/> into clip <paramref name="index"/>.</summary>
    private void ShowPlaying(EditProject project, int index, TimeSpan offset)
    {
        var clip = project.Clips[index];
        Player.Volume = project.ClipGain(index, offset);
        Darken.Opacity = 1 - project.Brightness(index, offset);

        var overlap = project.OverlapAfter(index);
        var into = offset - (clip.Length - overlap);
        if (overlap > TimeSpan.Zero && into > TimeSpan.Zero && _nextPicture is not null)
        {
            OverStill.Source = _nextPicture;
            OverStill.Opacity = Math.Clamp(into / overlap, 0, 1);
        }
        else
        {
            OverStill.Opacity = 0;
        }

        ShowWords(project, Panel.Position);
        PlaySoundLayers(project, Panel.Position);
    }

    /// <summary>What the preview's pictures show, as a picture, so a turned or zoomed picture comes out as it's seen.</summary>
    private RenderTargetBitmap? Snapshot()
    {
        if (Pictures.ActualWidth < 2 || Pictures.ActualHeight < 2 || (Player.Visibility != Visibility.Visible && Still.Source is null))
        {
            return null;
        }

        // Smaller than the frame, which can be bigger than the screen.
        var scale = Math.Min(1, SnapshotLongestSide / Math.Max(Pictures.ActualWidth, Pictures.ActualHeight));
        var picture = new RenderTargetBitmap((int)(Pictures.ActualWidth * scale), (int)(Pictures.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        picture.Render(Pictures);
        picture.Freeze();
        return picture;
    }

    private void Stop()
    {
        var was = _playing;
        _playing = false;
        _clock.Stop();
        Player.Pause();
        PauseSoundLayers();
        PlayIcon.Text = "";
        if (was)
        {
            _stillKey = default;
            ShowStill();
        }
    }

    // The sounds laid over the edit, each with a player of its own while playing.

    private sealed class SoundLayer(string path)
    {
        public MediaPlayer Player { get; } = new();

        public string Path { get; } = path;

        public bool Opened { get; set; }

        public bool Playing { get; set; }
    }

    private void OpenSoundLayers()
    {
        var sounds = Panel.Project.Sounds;
        for (var index = 0; index < sounds.Count; index++)
        {
            if (_soundLayers.TryGetValue(index, out var open) && open.Path == sounds[index].SourcePath)
            {
                continue;
            }

            var layer = new SoundLayer(sounds[index].SourcePath);
            layer.Player.MediaOpened += (_, _) => layer.Opened = true;
            layer.Player.Open(new Uri(layer.Path));
            _soundLayers[index] = layer;
        }
    }

    /// <summary>Starts, steers and stops each sound to match the playhead. One that drifts is put back where it should be.</summary>
    private void PlaySoundLayers(EditProject project, TimeSpan at)
    {
        foreach (var (index, layer) in _soundLayers)
        {
            if (index >= project.Sounds.Count || !layer.Opened)
            {
                continue;
            }

            var sound = project.Sounds[index];
            var offset = at - sound.At;
            if (offset >= TimeSpan.Zero && offset < project.HeardLengthOf(index))
            {
                layer.Player.Volume = project.SoundGain(index, offset);
                var wanted = sound.Start + offset;
                if (!layer.Playing || (layer.Player.Position - wanted).Duration() > SoundDrift)
                {
                    layer.Player.Position = wanted;
                }

                if (!layer.Playing)
                {
                    layer.Player.Play();
                    layer.Playing = true;
                }
            }
            else if (layer.Playing)
            {
                layer.Player.Pause();
                layer.Playing = false;
            }
        }
    }

    private void PauseSoundLayers()
    {
        foreach (var layer in _soundLayers.Values.Where(layer => layer.Playing))
        {
            layer.Player.Pause();
            layer.Playing = false;
        }
    }

    private void CloseSoundLayers()
    {
        foreach (var layer in _soundLayers.Values)
        {
            layer.Player.Close();
        }

        _soundLayers.Clear();
    }

    // The timeline and the mouse

    private sealed record Drag(DragKind Kind, EditItem Item, Point From, int Index, object? Original, int Landing = -1);

    private sealed record ZoomDrag(Point From, double X, double Y, double Size);

    private TimeSpan TimeAt(double x) => TimeSpan.FromSeconds(Math.Max(0, x) / PixelsPerSecond);

    /// <summary>What's at <paramref name="point"/>, topmost first, and whether that's near its start or end edge.</summary>
    private (EditItem Item, int Index, DragKind Kind)? ItemAt(Point point)
    {
        for (var each = _hits.Count - 1; each >= 0; each--)
        {
            var (area, item, index) = _hits[each];
            if (!area.Contains(point))
            {
                continue;
            }

            // A narrow one is all body, so it can still be moved.
            var grip = Math.Min(EdgeGrip, area.Width / 4);
            var kind = point.X < area.Left + grip ? DragKind.TrimStart : point.X > area.Right - grip ? DragKind.TrimEnd : DragKind.Pending;
            return (item, index, kind);
        }

        return null;
    }

    /// <summary>Where a clip taken out would land if let go at <paramref name="x"/>: before the first other clip whose middle is past it.</summary>
    private (int Index, double X) LandingAt(double x, int taken)
    {
        var perSecond = PixelsPerSecond;
        var rest = taken >= 0 && taken < Panel.Project.Clips.Count ? Panel.Project.Remove(taken) : Panel.Project;
        for (var index = 0; index < rest.Clips.Count; index++)
        {
            var left = rest.StartOf(index).TotalSeconds * perSecond;
            if (x < left + (rest.Clips[index].Length.TotalSeconds * perSecond / 2))
            {
                return (index, left);
            }
        }

        return (rest.Clips.Count, rest.Length.TotalSeconds * perSecond);
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

        if (Panel.CanEdit && ItemAt(point) is { } hit)
        {
            object original;
            switch (hit.Item)
            {
                case EditItem.Sound:
                    Panel.SelectedSound = hit.Index;
                    original = Panel.Project.Sounds[hit.Index];
                    break;
                case EditItem.Text:
                    Panel.SelectedText = hit.Index;
                    original = Panel.Project.Texts[hit.Index];
                    break;
                default:
                    Panel.SelectedIndex = hit.Index;
                    original = Panel.Project.Clips[hit.Index];
                    break;
            }

            if (hit.Kind == DragKind.Pending)
            {
                Panel.Position = TimeAt(point.X);
            }

            _drag = new Drag(hit.Kind, hit.Item, point, hit.Index, original);
        }
        else
        {
            Panel.Position = TimeAt(point.X);
            _drag = new Drag(DragKind.Scrub, EditItem.None, point, -1, null);
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
            Timeline.Cursor = ItemAt(point) switch
            {
                { Kind: DragKind.TrimStart or DragKind.TrimEnd } => Cursors.SizeWE,
                not null => Cursors.Hand,
                _ => null,
            };
            return;
        }

        var moved = point.X - _drag.From.X;
        var seconds = TimeSpan.FromSeconds(moved / PixelsPerSecond);
        if (_drag.Kind == DragKind.Pending && Math.Abs(moved) > DragStarts)
        {
            _drag = _drag with { Kind = DragKind.Move };
        }

        switch (_drag.Kind)
        {
            case DragKind.Scrub:
                Panel.Position = TimeAt(point.X);
                break;

            case DragKind.Move when _drag.Item == EditItem.Clip:
                var block = Timeline.Children.OfType<Border>().FirstOrDefault(found => found.Tag is (EditItem.Clip, int index) && index == _drag.Index);
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

            case DragKind.Move:
                _trimmed = _drag.Original switch
                {
                    EditSound sound => Panel.Project.MoveSound(_drag.Index, sound.At + seconds),
                    EditText text => Panel.Project.ChangeText(_drag.Index, text with { At = text.At + seconds }),
                    _ => null,
                };
                Redraw();
                break;

            case DragKind.TrimStart or DragKind.TrimEnd:
                var start = _drag.Kind == DragKind.TrimStart;
                _trimmed = _drag.Original switch
                {
                    EditClip clip => start ? Panel.Project.TrimStart(_drag.Index, clip.Start + seconds) : Panel.Project.TrimEnd(_drag.Index, clip.End + seconds),
                    EditSound sound => start ? Panel.Project.TrimSoundStart(_drag.Index, sound.Start + seconds) : Panel.Project.TrimSoundEnd(_drag.Index, sound.End + seconds),
                    EditText text => start ? Panel.Project.TrimTextStart(_drag.Index, text.At + seconds) : Panel.Project.TrimTextEnd(_drag.Index, text.Finish + seconds),
                    _ => null,
                };
                Redraw();

                // Show the picture at the edge of a clip being moved.
                if (_drag.Item == EditItem.Clip && _trimmed is not null)
                {
                    var trimmed = _trimmed.Clips[_drag.Index];
                    _ = ShowStillAsync(trimmed.SourcePath, start ? trimmed.Start : Max(trimmed.Start, trimmed.End - OneFrame));
                }

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
            var index = drag.Index;
            switch (drag.Kind, drag.Item)
            {
                case (DragKind.Move, EditItem.Clip) when drag.Landing >= 0:
                    Panel.Move(index, drag.Landing);
                    break;
                case (DragKind.Move, EditItem.Sound) when trimmed is not null:
                    Panel.MoveSound(index, trimmed.Sounds[index].At);
                    break;
                case (DragKind.Move, EditItem.Text) when trimmed is not null:
                    Panel.MoveText(index, trimmed.Texts[index].At);
                    break;
                case (DragKind.TrimStart, EditItem.Clip) when trimmed is not null:
                    Panel.TrimStart(index, trimmed.Clips[index].Start);
                    break;
                case (DragKind.TrimEnd, EditItem.Clip) when trimmed is not null:
                    Panel.TrimEnd(index, trimmed.Clips[index].End);
                    break;
                case (DragKind.TrimStart, EditItem.Sound) when trimmed is not null:
                    Panel.TrimSoundStart(index, trimmed.Sounds[index].Start);
                    break;
                case (DragKind.TrimEnd, EditItem.Sound) when trimmed is not null:
                    Panel.TrimSoundEnd(index, trimmed.Sounds[index].End);
                    break;
                case (DragKind.TrimStart, EditItem.Text) when trimmed is not null:
                    Panel.TrimTextStart(index, trimmed.Texts[index].At);
                    break;
                case (DragKind.TrimEnd, EditItem.Text) when trimmed is not null:
                    Panel.TrimTextEnd(index, trimmed.Texts[index].Finish);
                    break;
            }
        }

        Redraw();
        _stillKey = default;
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

    // Dragging the preview moves what's in view of a zoomed clip.

    private void Frame_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        if (_panel is null || _playing || !Panel.CanEdit || Panel.SelectedIndex < 0 || Panel.Project.ViewAt(Panel.Position)?.Under.Index != Panel.SelectedIndex)
        {
            return;
        }

        var clip = Panel.Project.Clips[Panel.SelectedIndex];
        if (!clip.IsZoomed)
        {
            return;
        }

        _zoomDrag = new ZoomDrag(e.GetPosition(Screen), clip.ZoomX, clip.ZoomY, clip.ZoomedView().Size);
        Screen.CaptureMouse();
        Screen.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void Frame_MouseMove(object sender, MouseEventArgs e)
    {
        if (_zoomDrag is not { } drag)
        {
            Screen.Cursor = _panel is { SelectedIndex: >= 0, ClipIsZoomed: true } ? Cursors.SizeAll : null;
            return;
        }

        // The picture follows the mouse, so what's in view moves the other way.
        var point = e.GetPosition(Screen);
        var x = drag.X - ((point.X - drag.From.X) / Screen.Width * drag.Size);
        var y = drag.Y - ((point.Y - drag.From.Y) / Screen.Height * drag.Size);
        var half = drag.Size / 2;
        Panel.SetZoomCentre(Math.Clamp(x, half, 1 - half), Math.Clamp(y, half, 1 - half));
    }

    private void EndZoomDrag()
    {
        _zoomDrag = null;
        if (Screen.IsMouseCaptured)
        {
            Screen.ReleaseMouseCapture();
        }
    }

    // Keys, buttons and dropped files

    private async void Page_KeyDown(object sender, KeyEventArgs e)
    {
        if (_panel is null || e.OriginalSource is TextBox)
        {
            return;
        }

        // A slider or list that has the keyboard uses the arrows itself.
        if (e.OriginalSource is Slider or ComboBox or ComboBoxItem && e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)
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

    private async void AddSound_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add sound",
            Multiselect = true,
            Filter = "Sounds|*.mp3;*.wav;*.m4a;*.aac;*.wma;*.flac|The sound of a video|*.mp4;*.mov;*.m4v;*.wmv|All files|*.*",
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            Stop();
            await Panel.AddSoundsAsync(dialog.FileNames);
        }
    }

    private void AddText_Click(object sender, RoutedEventArgs e)
    {
        Stop();
        Panel.AddText();

        // Ready to type over.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            WordsBox.Focus();
            WordsBox.SelectAll();
        });
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

        // Sounds go where they're let go on the timeline, or at the playhead. So do videos let go on the sounds' line,
        // as their sound. Other videos dropped on the timeline go in among the clips where they were let go, and
        // anywhere else, at the end.
        var point = e.GetPosition(Timeline);
        var onTimeline = point.Y >= 0 && point.Y <= Timeline.ActualHeight && point.X >= 0;
        var onSounds = onTimeline && point.Y >= _soundTop && point.Y < _textTop;
        var existing = files.Where(File.Exists).ToList();
        var sounds = existing.Where(file => onSounds || EditPanel.IsSoundFile(file)).ToList();
        var videos = existing.Except(sounds).ToList();

        Stop();
        if (sounds.Count > 0)
        {
            await Panel.AddSoundsAsync(sounds, onTimeline ? TimeAt(point.X) : null);
        }

        if (videos.Count > 0)
        {
            await Panel.AddVideosAsync(videos, onTimeline && point.Y < _soundTop ? LandingAt(point.X, -1).Index : null);
        }
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
