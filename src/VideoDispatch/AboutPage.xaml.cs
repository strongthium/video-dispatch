using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VideoDispatch.Core;

namespace VideoDispatch.App;

/// <summary>
/// Strongthium, this program, and whether there's a newer version of it. GitHub is asked when the page is first shown
/// (and again if it's shown an hour later), and at no other time; nothing is installed until Update is pressed.
/// </summary>
public sealed partial class AboutPage : UserControl
{
    private static readonly Updater Updater = new(
        Updater.NewClient("video-dispatch"),
        "strongthium/video-dispatch",
        version => $"video-dispatch-{version}-win-x64-needs-dotnet.zip",
        "video-dispatch.exe");

    private static readonly TimeSpan AskAgainAfter = TimeSpan.FromHours(1);
    private const string Info = "", Working = "", Problem = "", Newer = "", UpToDate = "";

    private Release? _release;
    private DateTime? _asked;
    private bool _busy;

    public AboutPage()
    {
        InitializeComponent();
        ProgramIcon.Source = App.Icon;
        VersionText.Text = $"Version {Updater.RunningVersion.ToString(4)}";
        Loaded += async (_, _) =>
        {
            if (!_busy && (_asked is null || DateTime.UtcNow - _asked > AskAgainAfter))
            {
                await CheckAsync();
            }
        };
    }

    /// <summary>Starts the new program, which waits for this one to quit. If it won't start, the old one goes back.</summary>
    private static void Restart(string running)
    {
        var start = new ProcessStartInfo(running) { UseShellExecute = false };
        start.ArgumentList.Add("--wait-for");
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        try
        {
            Process.Start(start)?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Updater.Undo(running);
            throw;
        }

        Application.Current.Shutdown();
    }

    private static void Open(string url) => Browser.TryOpen(new Uri(url));

    private async Task CheckAsync()
    {
        if (Program.Sample)
        {
            Show(Info, "Not checked", "This is the sample, so GitHub isn't asked.");
            CheckButton.Visibility = Visibility.Collapsed;
            _asked = DateTime.UtcNow;
            return;
        }

        _busy = true;
        _release = null;
        Show(Working, "Asking GitHub for the latest version…", "");
        UpdateBar.IsIndeterminate = true;
        UpdateBar.Visibility = Visibility.Visible;
        CheckButton.IsEnabled = false;
        NotesButton.Visibility = UpdateButton.Visibility = Visibility.Collapsed;
        try
        {
            _release = await Updater.LatestAsync();
            _asked = DateTime.UtcNow;
            ShowAnswer();
        }
        catch (Exception exception) when (Updater.IsExpected(exception))
        {
            Show(Problem, "Couldn't ask GitHub for the latest version", Updater.Explain(exception, ""));
        }
        finally
        {
            _busy = false;
            UpdateBar.Visibility = Visibility.Collapsed;
            CheckButton.IsEnabled = true;
        }
    }

    private void ShowAnswer()
    {
        if (_release is null)
        {
            return;
        }

        var latest = _release.Version.ToString(4);
        NotesButton.Visibility = Visibility.Visible;
        if (_release.Version > Updater.RunningVersion)
        {
            Show(Newer, $"Version {latest} is out. You have {Updater.RunningVersion.ToString(4)}.",
                "Updating downloads it from GitHub, checks it against the release's checksums, replaces this program and starts it again.");
            UpdateButton.Content = $"Update to {latest} and restart";
            UpdateButton.Visibility = Visibility.Visible;
        }
        else if (_release.Version == Updater.RunningVersion)
        {
            Show(UpToDate, "You have the latest version.", "");
        }
        else
        {
            Show(UpToDate, $"This is newer than the latest release, {latest}.", "");
        }
    }

    private void Show(string icon, string title, string detail)
    {
        UpdateIcon.Text = icon;
        UpdateIcon.Foreground = icon is Newer or UpToDate ? (Brush)FindResource("AccentTextFillColorPrimaryBrush") : (Brush)FindResource("TextFillColorSecondaryBrush");
        UpdateTitle.Text = title;
        UpdateDetail.Text = detail;
        UpdateDetail.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_release is null || Environment.ProcessPath is not { } running)
        {
            return;
        }

        var latest = _release.Version.ToString(4);
        _busy = true;
        UpdateButton.Visibility = CheckButton.Visibility = Visibility.Collapsed;
        UpdateBar.IsIndeterminate = false;
        UpdateBar.Value = 0;
        UpdateBar.Visibility = Visibility.Visible;
        Show(Newer, $"Downloading version {latest}…", "");
        try
        {
            await Updater.InstallAsync(_release, running, new Progress<double>(share => UpdateBar.Value = share * 100));
            Show(Working, $"Starting version {latest}…", "");
            Restart(running);
        }
        catch (Exception exception) when (Updater.IsExpected(exception) || exception is System.ComponentModel.Win32Exception)
        {
            _busy = false;
            UpdateBar.Visibility = Visibility.Collapsed;
            CheckButton.Visibility = UpdateButton.Visibility = Visibility.Visible;
            Show(Problem, "The update wasn't installed", Updater.Explain(exception, running) + " You can download it from the release's page instead: What's new.");
        }
    }

    private async void Check_Click(object sender, RoutedEventArgs e) => await CheckAsync();

    private void Notes_Click(object sender, RoutedEventArgs e) => Open(_release?.Page ?? Updater.ReleasesPage);

    private void Banner_Click(object sender, RoutedEventArgs e) => Open((string)((Button)sender).Tag);

    private void Link_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        Open(e.Uri.AbsoluteUri);
        e.Handled = true;
    }
}
