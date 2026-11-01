using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace VideoDispatch.App;

public sealed partial class AccountCard : UserControl
{
    public AccountCard()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is AccountPanel old)
            {
                old.SecretAccepted -= Panel_SecretAccepted;
            }

            if (e.NewValue is AccountPanel panel)
            {
                panel.SecretAccepted += Panel_SecretAccepted;
            }
        };
    }

    private AccountPanel Panel => (AccountPanel)DataContext;

    private void Panel_SecretAccepted(object? sender, EventArgs e) => Secret.Clear();

    private void Secret_PasswordChanged(object sender, RoutedEventArgs e) => Panel.ClientSecret = Secret.Password;

    private async void SaveKeys_Click(object sender, RoutedEventArgs e) => await Panel.SaveKeysAsync();

    private async void RemoveKeys_Click(object sender, RoutedEventArgs e) => await Panel.RemoveKeysAsync();

    private async void Connect_Click(object sender, RoutedEventArgs e) => await Panel.ConnectAsync();

    private void CancelSignIn_Click(object sender, RoutedEventArgs e) => Panel.CancelSignIn();

    private async void Disconnect_Click(object sender, RoutedEventArgs e) => await Panel.DisconnectAsync();

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        if (!Browser.TryOpen(e.Uri))
        {
            MessageBox.Show($"Windows couldn't open {e.Uri}.", "Video Dispatch", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
