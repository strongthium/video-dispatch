using System.Windows;
using System.Windows.Controls;

namespace VideoDispatch.App;

public sealed partial class PresetsPage : UserControl
{
    public PresetsPage() => InitializeComponent();

    private PresetsPanel Panel => (PresetsPanel)DataContext;

    private void NewTikTok_Click(object sender, RoutedEventArgs e) => Panel.TikTok.New();

    private async void SaveTikTok_Click(object sender, RoutedEventArgs e) => await RunAsync(Panel.TikTok.SaveAsync);

    private async void DeleteTikTok_Click(object sender, RoutedEventArgs e) => await RunAsync(Panel.TikTok.DeleteAsync);

    private void NewYouTube_Click(object sender, RoutedEventArgs e) => Panel.YouTube.New();

    private async void SaveYouTube_Click(object sender, RoutedEventArgs e) => await RunAsync(Panel.YouTube.SaveAsync);

    private async void DeleteYouTube_Click(object sender, RoutedEventArgs e) => await RunAsync(Panel.YouTube.DeleteAsync);

    private async Task RunAsync(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(Window.GetWindow(this), exception.Message, "Video Dispatch", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
