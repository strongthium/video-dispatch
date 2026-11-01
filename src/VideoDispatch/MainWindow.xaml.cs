using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using VideoDispatch.Core.Model;

namespace VideoDispatch.App;

public sealed partial class MainWindow : Window
{
    private readonly Services _services;
    private readonly NewPostPanel _newPost;
    private readonly QueuePanel _queue;
    private readonly PresetsPanel _presets;
    private readonly YouTubeChoices _choices;
    private readonly IReadOnlyList<AccountPanel> _accounts;
    private readonly Dictionary<string, UserControl> _pages = [];

    /// <param name="services">What the screens work with.</param>
    /// <param name="damagedJobs">The saved jobs that couldn't be read.</param>
    public MainWindow(Services services, IReadOnlyList<string> damagedJobs)
    {
        InitializeComponent();
        Icon = App.Icon;

        _services = services;
        _choices = new YouTubeChoices(services.YouTube, services.Accounts.GetStatus);
        _newPost = new NewPostPanel(services.Queue, services.Presets, _choices, services.Accounts.GetStatus, services.ReadMetadata, TimeProvider.System, TimeZoneInfo.Local);
        _presets = new PresetsPanel(services.Presets, _choices, DateOnly.FromDateTime(DateTime.Today));
        _accounts =
        [
            new AccountPanel(services.Accounts, Platform.TikTok, services.OpenBrowser),
            new AccountPanel(services.Accounts, Platform.YouTube, services.OpenBrowser),
        ];

        foreach (var account in _accounts)
        {
            account.Connected += platform => _ = Task.Run(() => services.Queue.AccountReconnected(platform));
            account.AccountChanged += _ => _choices.Forget();
        }

        _queue = new QueuePanel(services.Queue, action => Dispatcher.BeginInvoke(action));
        _queue.ReportDamaged(damagedJobs);

        Loaded += async (_, _) =>
        {
            await services.Presets.LoadAsync();
            await Task.WhenAll(_accounts.Select(account => account.LoadAsync()));
            await _newPost.RefreshAsync();
            if (Program.StartVideo is { } video && File.Exists(video))
            {
                await _newPost.ChooseVideoAsync(video);
            }
        };

        (Program.StartPage switch
        {
            "queue" => QueuePlace,
            "presets" => PresetsPlace,
            "accounts" => AccountsPlace,
            "about" => AboutPlace,
            _ => NewPlace,
        }).IsChecked = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_services.Queue.Jobs.Any(job => job.State == JobState.Uploading))
        {
            var answer = MessageBox.Show(
                this,
                "A video is uploading. If you close now, YouTube uploads carry on from where they were the next time you open this, and TikTok uploads start over.\n\nClose anyway?",
                "Video Dispatch",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            e.Cancel = answer != MessageBoxResult.Yes;
        }
    }

    private async void Place_Checked(object sender, RoutedEventArgs e)
    {
        var place = (string)((RadioButton)sender).Tag;
        if (!_pages.TryGetValue(place, out var page))
        {
            page = place switch
            {
                "queue" => new QueuePage { DataContext = _queue },
                "presets" => new PresetsPage { DataContext = _presets },
                "accounts" => new AccountsPage(_accounts),
                "about" => new AboutPage(),
                _ => new NewPostPage { DataContext = _newPost },
            };
            _pages[place] = page;
        }

        Page.Content = page;

        // What these pages offer depends on the accounts, and those may have changed on the Accounts page.
        if (place is "new" or "presets")
        {
            await _newPost.RefreshAsync();
        }
    }
}
