using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace VideoDispatch.App;

public sealed partial class QueuePage : UserControl
{
    public QueuePage() => InitializeComponent();

    private QueuePanel Panel => (QueuePanel)DataContext;

    private static JobRow RowOf(object sender) => (JobRow)((FrameworkElement)sender).DataContext;

    private async void Retry_Click(object sender, RoutedEventArgs e) => await Panel.RetryAsync(RowOf(sender));

    private async void Cancel_Click(object sender, RoutedEventArgs e) => await Panel.CancelAsync(RowOf(sender));

    private async void MarkPosted_Click(object sender, RoutedEventArgs e) => await Panel.MarkPostedAsync(RowOf(sender));

    private async void Remove_Click(object sender, RoutedEventArgs e) => await Panel.RemoveAsync(RowOf(sender));

    private void CopyCaption_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender).Caption is { } caption)
        {
            try
            {
                Clipboard.SetDataObject(caption, copy: true);
            }
            catch (ExternalException)
            {
                // Another program has the clipboard open. The caption can still be selected in its box.
            }
        }
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        Browser.TryOpen(e.Uri);
    }
}
