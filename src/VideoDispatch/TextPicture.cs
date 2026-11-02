using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using VideoDispatch.Core.Editing;

namespace VideoDispatch.App;

/// <summary>
/// Draws an edit's words over the picture. The preview shows what <see cref="Build"/> makes, and saving draws the same
/// thing into a picture the size of the frame, so the two look alike. Sizes are shares of the frame's shorter side, so
/// words look the same size on a tall video as on a wide one.
/// </summary>
public static class TextPicture
{
    private static readonly FontFamily Font = new("Segoe UI");

    /// <summary>Words laid out in a frame <paramref name="width"/> by <paramref name="height"/>, on nothing.</summary>
    public static FrameworkElement Build(EditText text, double width, double height)
    {
        var shorter = Math.Min(width, height);
        var size = shorter * text.Size switch
        {
            TextSize.Small => 0.045,
            TextSize.Large => 0.095,
            _ => 0.065,
        };

        var (ink, behind) = text.Colour switch
        {
            TextColour.Yellow => (Color.FromRgb(0xFF, 0xD8, 0x2E), Color.FromArgb(0xB4, 0, 0, 0)),
            TextColour.Black => (Colors.Black, Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
            _ => (Colors.White, Color.FromArgb(0xB4, 0, 0, 0)),
        };

        var words = new TextBlock
        {
            Text = text.Text,
            FontFamily = Font,
            FontWeight = FontWeights.SemiBold,
            FontSize = size,
            Foreground = new SolidColorBrush(ink),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        };

        // Without a box, a soft edge of the other shade keeps the words readable over a picture of their own colour.
        if (!text.Box)
        {
            words.Effect = new DropShadowEffect
            {
                Color = behind,
                Opacity = 0.9,
                ShadowDepth = 0,
                BlurRadius = size * 0.35,
            };
        }

        var box = new Border
        {
            Child = words,
            Background = text.Box ? new SolidColorBrush(behind) : Brushes.Transparent,
            CornerRadius = new CornerRadius(size * 0.25),
            Padding = text.Box ? new Thickness(size * 0.45, size * 0.2, size * 0.45, size * 0.25) : new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = text.Place switch
            {
                TextPlace.Top => VerticalAlignment.Top,
                TextPlace.Middle => VerticalAlignment.Center,
                _ => VerticalAlignment.Bottom,
            },
            Margin = new Thickness(width * 0.06, height * 0.07, width * 0.06, height * 0.07),
        };

        return new Grid { Width = width, Height = height, Children = { box }, IsHitTestVisible = false };
    }

    /// <summary>The words as a PNG <paramref name="width"/> by <paramref name="height"/>, see-through except for them. Call on the window's thread.</summary>
    public static byte[] Png(EditText text, int width, int height)
    {
        var drawn = Build(text, width, height);
        drawn.Measure(new Size(width, height));
        drawn.Arrange(new Rect(0, 0, width, height));
        drawn.UpdateLayout();

        var picture = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        picture.Render(drawn);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(picture));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
