using System.ComponentModel;
using System.Net.Sockets;
using VideoDispatch.Core.Accounts;
using VideoDispatch.Core.Accounts.Secrets;
using VideoDispatch.Core.Model;

namespace VideoDispatch.App;

/// <summary>
/// What one platform's card on the Accounts screen shows and does. The secure store and the sign-in run off the UI
/// thread. A saved secret never comes back here: <see cref="ClientSecret"/> only ever holds what the user just typed.
/// </summary>
public sealed class AccountPanel(
    AccountService accounts,
    Platform platform,
    Func<Uri, CancellationToken, Task> openBrowser) : INotifyPropertyChanged
{
    private const string PlaylistExplanation =
        "Adding videos to playlists needs a broader YouTube permission, which also allows deleting videos, comments and " +
        "captions. Leave this off if you don't use playlists. Turning it on later means connecting again.";

    private AccountStatus? _status;
    private CancellationTokenSource? _signIn;
    private string _clientId = "";
    private bool _wantsPlaylists;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The keys were saved, so the view must empty its password box.</summary>
    public event EventHandler? SecretAccepted;

    public string Title => platform.ToString();

    public string ClientIdLabel => platform == Platform.TikTok ? "Client key" : "Client ID";

    public string ClientSecretLabel => platform == Platform.TikTok ? "Client secret" : "Client secret (optional)";

    public string SetupText => platform == Platform.TikTok
        ? "Register your own app on TikTok for Developers. Add the Content Posting API and Login Kit, with the scopes " +
          "user.info.basic and video.upload, and this redirect URI:"
        : "Create your own project in Google Cloud. Enable the YouTube Data API v3 and create an OAuth client of the type " +
          "Desktop app. If the app's publishing status is Testing, Google ends its sign-ins after 7 days.";

    public string? RedirectUri => platform == Platform.TikTok ? "http://127.0.0.1:*/callback/" : null;

    public bool HasRedirectUri => RedirectUri is not null;

    public Uri RegisterLink => new(platform == Platform.TikTok
        ? "https://developers.tiktok.com/apps/"
        : "https://console.cloud.google.com/apis/credentials");

    public string RegisterLinkText => platform == Platform.TikTok ? "Open TikTok for Developers" : "Open Google Cloud credentials";

    public Uri RevokeLink => new(platform == Platform.TikTok
        ? "https://www.tiktok.com/setting"
        : "https://myaccount.google.com/permissions");

    public string RevokeLinkText => platform == Platform.TikTok ? "Open TikTok's settings" : "Open your Google account's permissions";

    public string ClientId
    {
        get => _clientId;
        set => _clientId = value;
    }

    /// <summary>Write only: what the user has typed since the keys were last saved.</summary>
    public string ClientSecret { private get; set; } = "";

    public bool HasSavedSecret { get; private set; }

    public ConnectionState State => _status?.State ?? ConnectionState.NotConnected;

    public string StateText => State switch
    {
        ConnectionState.Connected when _status?.DisplayName is { } name => $"Connected as {name}",
        ConnectionState.Connected => "Connected",
        ConnectionState.NeedsReconnect => "The sign-in has expired or was revoked. Connect again.",
        _ => _status?.HasCredentials == true ? "Not connected" : "Enter your app's keys to begin",
    };

    /// <summary>Waiting on the browser, or on the secure store or network.</summary>
    public bool IsBusy { get; private set; }

    public bool IsWaitingForSignIn { get; private set; }

    public string? Message { get; private set; }

    public bool MessageIsError { get; private set; }

    public bool HasMessage => Message is not null;

    public bool ShowsRevokeLink { get; private set; }

    public bool IsConnected => State == ConnectionState.Connected;

    public bool OffersPlaylists => platform == Platform.YouTube;

    public bool CanChoosePlaylists => !IsBusy;

    public bool WantsPlaylists
    {
        get => _wantsPlaylists;
        set
        {
            _wantsPlaylists = value;
            Refresh();
        }
    }

    public string PlaylistNote => (_wantsPlaylists, HasPlaylistPermission, State) switch
    {
        (true, false, ConnectionState.Connected) => "Connect again to grant this permission.",
        (false, true, ConnectionState.Connected) => "To give up this permission, disconnect, which revokes it, then connect again.",
        _ => PlaylistExplanation,
    };

    /// <summary>Connected keys stay put: a new client would drop the sign-in without revoking it.</summary>
    public bool CanEditKeys => !IsBusy && State != ConnectionState.Connected;

    public bool CanRemoveKeys => !IsBusy && _status?.HasCredentials == true && State == ConnectionState.NotConnected;

    /// <summary>Signing in again without the playlist permission would leave the old, broader sign-in alive at Google.</summary>
    public bool CanConnect =>
        !IsBusy &&
        _status?.HasCredentials == true &&
        !(State == ConnectionState.Connected && HasPlaylistPermission && !_wantsPlaylists);

    public bool CanDisconnect => !IsBusy && State != ConnectionState.NotConnected;

    public string ConnectText => State == ConnectionState.NotConnected ? "Connect" : "Connect again";

    private bool HasPlaylistPermission => _status?.Scopes.Contains(GoogleOAuthProvider.PlaylistScope) == true;

    public Task LoadAsync() => RunAsync(async _ =>
    {
        await ReadAsync();
        _clientId = await Task.Run(() => accounts.GetCredentials(platform)?.ClientId) ?? "";
        _wantsPlaylists = HasPlaylistPermission;
        return null;
    });

    public Task SaveKeysAsync() => RunAsync(async cancellationToken =>
    {
        var clientId = _clientId;
        var typedSecret = ClientSecret;

        await Task.Run(async () =>
        {
            // An empty box keeps the saved secret, unless the keys are for a different app now.
            var saved = accounts.GetCredentials(platform);
            var secret = typedSecret.Length == 0 && saved?.ClientId == clientId.Trim() ? saved.ClientSecret : typedSecret;
            await accounts.SaveCredentialsAsync(platform, new AppCredentials(clientId, secret), cancellationToken);
        }, cancellationToken);

        ClientSecret = "";
        SecretAccepted?.Invoke(this, EventArgs.Empty);
        _clientId = clientId.Trim();
        await ReadAsync();
        return "Keys saved in Windows Credential Manager.";
    });

    public Task RemoveKeysAsync() => RunAsync(async cancellationToken =>
    {
        await Task.Run(() => accounts.RemoveCredentialsAsync(platform, cancellationToken), cancellationToken);
        ClientSecret = "";
        SecretAccepted?.Invoke(this, EventArgs.Empty);
        _clientId = "";
        await ReadAsync();
        return "Keys removed.";
    });

    public Task ConnectAsync()
    {
        var wanted = _wantsPlaylists;

        return RunAsync(async cancellationToken =>
        {
            IsWaitingForSignIn = true;
            Refresh();

            await Task.Run(
                () => accounts.ConnectAsync(platform, openBrowser, wanted, cancellationToken: cancellationToken),
                cancellationToken);

            await ReadAsync();
            _wantsPlaylists = HasPlaylistPermission;
            return wanted && !HasPlaylistPermission
                ? "Connected, but the playlist permission wasn't granted, so playlists stay off."
                : null;
        });
    }

    public void CancelSignIn() => _signIn?.Cancel();

    public Task DisconnectAsync() => RunAsync(async cancellationToken =>
    {
        var revoked = await Task.Run(() => accounts.DisconnectAsync(platform, cancellationToken), cancellationToken);
        await ReadAsync();
        _wantsPlaylists = false;
        if (revoked)
        {
            return "Disconnected.";
        }

        ShowsRevokeLink = true;
        return $"Removed from this computer, but {platform} couldn't be told. To end its access fully, remove the app on {platform}'s site.";
    });

    private async Task RunAsync(Func<CancellationToken, Task<string?>> work)
    {
        using var signIn = new CancellationTokenSource();
        _signIn = signIn;
        IsBusy = true;
        Message = null;
        MessageIsError = false;
        ShowsRevokeLink = false;
        Refresh();

        try
        {
            Message = await work(signIn.Token);
        }
        catch (OperationCanceledException)
        {
            Message = "Sign-in canceled.";
        }
        catch (Exception exception) when (exception is AccountException or SecretStoreException or BrowserException or SocketException)
        {
            // These messages are written for the user, and never contain a token or secret.
            Message = exception.Message;
            MessageIsError = true;
            await ReadAfterFailureAsync();
        }
        finally
        {
            _signIn = null;
            IsBusy = false;
            IsWaitingForSignIn = false;
            Refresh();
        }
    }

    private async Task ReadAsync()
    {
        _status = await Task.Run(() => accounts.GetStatus(platform));
        HasSavedSecret = await Task.Run(() => accounts.GetCredentials(platform)?.ClientSecret is { Length: > 0 });
    }

    /// <summary>The failure already explains itself, so if the store is what failed, what's shown just stays.</summary>
    private async Task ReadAfterFailureAsync()
    {
        try
        {
            await ReadAsync();
        }
        catch (SecretStoreException)
        {
        }
    }

    private void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
}
