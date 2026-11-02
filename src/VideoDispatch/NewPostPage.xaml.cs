using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace VideoDispatch.App;

public sealed partial class NewPostPage : UserControl
{
    public NewPostPage() => InitializeComponent();

    private NewPostPanel Panel => (NewPostPanel)DataContext;

    private async void Choose_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a video",
            Filter = "Videos|*.mp4;*.mov;*.webm;*.m4v;*.mkv;*.avi;*.wmv;*.mpg;*.mpeg;*.3gp|All files|*.*",
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            await Panel.ChooseVideoAsync(dialog.FileName);
        }
    }

    private void Edit_Click(object sender, RoutedEventArgs e) => Panel.RequestEdit();

    private async void Queue_Click(object sender, RoutedEventArgs e) => await Panel.QueueAsync();
}
