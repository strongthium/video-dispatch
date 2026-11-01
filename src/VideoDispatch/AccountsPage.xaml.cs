using System.Windows.Controls;

namespace VideoDispatch.App;

public sealed partial class AccountsPage : UserControl
{
    public AccountsPage(IReadOnlyList<AccountPanel> panels)
    {
        InitializeComponent();
        Panels.ItemsSource = panels;
    }
}
