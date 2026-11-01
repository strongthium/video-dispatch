using System.Net.Http;
using System.Windows;
using VideoDispatch.Core.Accounts;
using VideoDispatch.Core.Accounts.Secrets;
using VideoDispatch.Core.Model;

namespace VideoDispatch.App;

public sealed partial class App : Application
{
    private static readonly HttpClient Http = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var (accounts, openBrowser) = Program.Sample
            ? SampleAccounts.Create()
            : (CreateAccounts(), Browser.OpenSignInAsync);

        var window = new MainWindow(
        [
            new AccountPanel(accounts, Platform.TikTok, openBrowser),
            new AccountPanel(accounts, Platform.YouTube, openBrowser),
        ]);
        MainWindow = window;
        window.Show();
    }

    private static AccountService CreateAccounts() => new(
        new WindowsCredentialStore(),
        [new TikTokOAuthProvider(Http), new GoogleOAuthProvider(Http)],
        TimeProvider.System);
}
