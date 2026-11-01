using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using Microsoft.Win32;

namespace VideoDispatch.App;

public sealed partial class SettingsPage : UserControl
{
    public SettingsPage() => InitializeComponent();

    private SettingsPanel Panel => (SettingsPanel)DataContext;

    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose ffmpeg.exe",
            Filter = "FFmpeg|ffmpeg.exe|Programs|*.exe",
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            Panel.FfmpegPath = dialog.FileName;
        }
    }

    private async void Download_Click(object sender, RoutedEventArgs e) => await Panel.DownloadAsync();

    private void CancelDownload_Click(object sender, RoutedEventArgs e) => Panel.CancelDownload();

    private async void Use_Click(object sender, RoutedEventArgs e) => await Panel.UseAsync();

    private async void StopUsing_Click(object sender, RoutedEventArgs e) => await Panel.StopUsingAsync();

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        Browser.TryOpen(e.Uri);
    }
}
