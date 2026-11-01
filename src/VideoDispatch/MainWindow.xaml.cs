using System.Windows;

namespace VideoDispatch.App;

public sealed partial class MainWindow : Window
{
    public MainWindow(IReadOnlyList<AccountPanel> panels)
    {
        InitializeComponent();
        Panels.ItemsSource = panels;
        Loaded += async (_, _) => await Task.WhenAll(panels.Select(panel => panel.LoadAsync()));
    }
}
