using System.Collections.Concurrent;
using System.Net.Http;
using System.Web;
using VideoDispatch.App;
using VideoDispatch.Core.Accounts;
using VideoDispatch.Core.Accounts.Secrets;
using VideoDispatch.Core.Model;

namespace VideoDispatch.Tests;

public class AccountPanelTests
{
    private sealed class MemoryStore : ISecretStore
    {
        public ConcurrentDictionary<string, string> Values { get; } = new();
        public bool Fail { get; set; }

        public string? Get(string key) => Fail ? throw new SecretStoreException("The store failed.") : Values.GetValueOrDefault(key);

        public void Set(string key, string value) => Values[key] = value;

        public void Delete(string key) => Values.TryRemove(key, out _);
    }

    private sealed class StubProvider(Platform platform) : IOAuthProvider
    {
        public Platform Platform => platform;
        public bool RequiresClientSecret => platform == Platform.TikTok;
        public bool RevokeUsesAccessToken => false;
        public IReadOnlyList<string> RequiredScopes { get; } = ["basic"];
        public IReadOnlyList<string> OptionalScopes { get; } = [GoogleOAuthProvider.PlaylistScope];
        public string CallbackPath => "/callback/";
        public List<IReadOnlyList<string>> RequestedScopes { get; } = [];
        public bool GrantsOptional { get; set; } = true;
        public bool RevokeFails { get; set; }
        public bool RefreshFails { get; set; }

        public string BuildRedirectUri(int port) => $"http://127.0.0.1:{port}/callback/";

        public Uri BuildAuthorizationUri(AppCredentials credentials, IReadOnlyList<string> scopes, string redirectUri, string state, string codeVerifier)
        {
            RequestedScopes.Add(scopes);
            return new Uri($"https://sign-in.example/?redirect_uri={Uri.EscapeDataString(redirectUri)}&state={Uri.EscapeDataString(state)}");
        }

        public Task<TokenGrant> ExchangeCodeAsync(AppCredentials credentials, string code, string redirectUri, string codeVerifier, CancellationToken cancellationToken)
        {
            var scopes = GrantsOptional ? RequestedScopes[^1] : RequiredScopes;
            return Task.FromResult(new TokenGrant
            {
                AccessToken = "access",
                AccessTokenLifetime = TimeSpan.Zero,
                RefreshToken = "refresh",
                Scopes = scopes,
            });
        }

        public Task<TokenGrant> RefreshAsync(AppCredentials credentials, string refreshToken, CancellationToken cancellationToken) =>
            RefreshFails
                ? throw new ReconnectRequiredException(platform, "The sign-in has expired.")
                : Task.FromResult(new TokenGrant { AccessToken = "access", AccessTokenLifetime = TimeSpan.FromHours(1) });

        public Task RevokeAsync(AppCredentials credentials, string token, CancellationToken cancellationToken) =>
            RevokeFails ? throw new TransientAccountException(platform, "Couldn't reach it.") : Task.CompletedTask;

        public Task<AccountIdentity> GetIdentityAsync(string accessToken, CancellationToken cancellationToken) =>
            Task.FromResult(new AccountIdentity("id", "Sample Name"));
    }

    private sealed class Harness
    {
        private static readonly HttpClient Client = new();

        public Harness(Platform platform, Func<Uri, CancellationToken, Task>? openBrowser = null)
        {
            Provider = new StubProvider(platform);
            Accounts = new AccountService(Store, [Provider], TimeProvider.System);
            Panel = new AccountPanel(Accounts, platform, openBrowser ?? ApproveAsync);
        }

        public MemoryStore Store { get; } = new();
        public StubProvider Provider { get; }
        public AccountService Accounts { get; }
        public AccountPanel Panel { get; }

        public static async Task ApproveAsync(Uri uri, CancellationToken cancellationToken)
        {
            var query = HttpUtility.ParseQueryString(uri.Query);
            using var response = await Client.GetAsync($"{query["redirect_uri"]}?code=code&state={Uri.EscapeDataString(query["state"]!)}", cancellationToken);
        }

        public static async Task DenyAsync(Uri uri, CancellationToken cancellationToken)
        {
            var query = HttpUtility.ParseQueryString(uri.Query);
            using var response = await Client.GetAsync($"{query["redirect_uri"]}?error=access_denied&state={Uri.EscapeDataString(query["state"]!)}", cancellationToken);
        }

        public async Task SaveKeysAsync(string clientId = "client", string secret = "secret")
        {
            Panel.ClientId = clientId;
            Panel.ClientSecret = secret;
            await Panel.SaveKeysAsync();
        }
    }

    [Fact]
    public async Task With_no_keys_it_asks_for_them_and_offers_no_connection()
    {
        var harness = new Harness(Platform.TikTok);

        await harness.Panel.LoadAsync();

        Assert.Equal("Enter your app's keys to begin", harness.Panel.StateText);
        Assert.False(harness.Panel.CanConnect);
        Assert.False(harness.Panel.CanDisconnect);
        Assert.False(harness.Panel.CanRemoveKeys);
        Assert.True(harness.Panel.CanEditKeys);
    }

    [Fact]
    public async Task Saving_keys_empties_the_secret_box_and_allows_connecting()
    {
        var harness = new Harness(Platform.TikTok);
        var emptied = false;
        harness.Panel.SecretAccepted += (_, _) => emptied = true;

        await harness.SaveKeysAsync(" client ");

        Assert.True(emptied);
        Assert.True(harness.Panel.HasSavedSecret);
        Assert.Equal("client", harness.Panel.ClientId);
        Assert.True(harness.Panel.CanConnect);
        Assert.False(harness.Panel.MessageIsError);
    }

    [Fact]
    public async Task A_saved_secret_is_never_shown_back()
    {
        const string secret = "very-secret-value";
        var harness = new Harness(Platform.TikTok);
        await harness.SaveKeysAsync(secret: secret);

        var fresh = new AccountPanel(harness.Accounts, Platform.TikTok, Harness.ApproveAsync);
        await fresh.LoadAsync();

        Assert.True(fresh.HasSavedSecret);
        foreach (var property in typeof(AccountPanel).GetProperties().Where(p => p.GetMethod?.IsPublic == true))
        {
            Assert.NotEqual(secret, property.GetValue(fresh)?.ToString());
        }
    }

    [Fact]
    public async Task An_empty_secret_box_keeps_the_saved_secret_for_the_same_app()
    {
        var harness = new Harness(Platform.TikTok);
        await harness.SaveKeysAsync();

        await harness.SaveKeysAsync(secret: "");

        Assert.Equal("secret", harness.Accounts.GetCredentials(Platform.TikTok)?.ClientSecret);
        Assert.False(harness.Panel.MessageIsError);
    }

    [Fact]
    public async Task A_new_client_does_not_inherit_the_old_secret()
    {
        var harness = new Harness(Platform.TikTok);
        await harness.SaveKeysAsync();

        await harness.SaveKeysAsync(clientId: "other", secret: "");

        Assert.True(harness.Panel.MessageIsError);
        Assert.Equal("client", harness.Accounts.GetCredentials(Platform.TikTok)?.ClientId);
    }

    [Fact]
    public async Task YouTube_keys_can_have_no_secret()
    {
        var harness = new Harness(Platform.YouTube);

        await harness.SaveKeysAsync(secret: "");

        Assert.False(harness.Panel.MessageIsError);
        Assert.False(harness.Panel.HasSavedSecret);
        Assert.True(harness.Panel.CanConnect);
    }

    [Fact]
    public async Task Connecting_shows_the_name_and_locks_the_keys()
    {
        var harness = new Harness(Platform.TikTok);
        await harness.SaveKeysAsync();

        await harness.Panel.ConnectAsync();

        Assert.Equal("Connected as Sample Name", harness.Panel.StateText);
        Assert.Equal("Connect again", harness.Panel.ConnectText);
        Assert.True(harness.Panel.IsConnected);
        Assert.False(harness.Panel.IsBusy);
        Assert.False(harness.Panel.CanEditKeys);
        Assert.False(harness.Panel.CanRemoveKeys);
        Assert.True(harness.Panel.CanDisconnect);
    }

    [Fact]
    public async Task It_reads_as_waiting_until_the_browser_comes_back()
    {
        var release = new TaskCompletionSource();
        var harness = new Harness(Platform.TikTok, async (uri, cancellationToken) =>
        {
            await release.Task;
            await Harness.ApproveAsync(uri, cancellationToken);
        });
        await harness.SaveKeysAsync();

        var connecting = harness.Panel.ConnectAsync();
        Assert.True(harness.Panel.IsBusy);
        Assert.True(harness.Panel.IsWaitingForSignIn);
        Assert.False(harness.Panel.CanConnect);
        Assert.False(harness.Panel.CanEditKeys);

        release.SetResult();
        await connecting;

        Assert.False(harness.Panel.IsWaitingForSignIn);
    }

    [Fact]
    public async Task Canceling_the_sign_in_leaves_it_not_connected()
    {
        var harness = new Harness(Platform.TikTok, (_, _) => Task.CompletedTask);
        await harness.SaveKeysAsync();

        var connecting = harness.Panel.ConnectAsync();
        harness.Panel.CancelSignIn();
        await connecting;

        Assert.Equal("Sign-in canceled.", harness.Panel.Message);
        Assert.False(harness.Panel.MessageIsError);
        Assert.False(harness.Panel.IsBusy);
        Assert.Equal(ConnectionState.NotConnected, harness.Panel.State);
    }

    [Fact]
    public async Task A_denied_sign_in_shows_why()
    {
        var harness = new Harness(Platform.TikTok, Harness.DenyAsync);
        await harness.SaveKeysAsync();

        await harness.Panel.ConnectAsync();

        Assert.True(harness.Panel.MessageIsError);
        Assert.Contains("canceled or denied", harness.Panel.Message);
        Assert.Equal(ConnectionState.NotConnected, harness.Panel.State);
    }

    [Fact]
    public async Task A_browser_that_will_not_open_shows_why()
    {
        var harness = new Harness(Platform.TikTok, (_, _) => throw new BrowserException());
        await harness.SaveKeysAsync();

        await harness.Panel.ConnectAsync();

        Assert.True(harness.Panel.MessageIsError);
        Assert.False(harness.Panel.IsBusy);
    }

    [Fact]
    public async Task Playlists_off_asks_only_for_the_required_permissions()
    {
        var harness = new Harness(Platform.YouTube);
        await harness.SaveKeysAsync();

        await harness.Panel.ConnectAsync();

        Assert.DoesNotContain(GoogleOAuthProvider.PlaylistScope, harness.Provider.RequestedScopes.Single());
        Assert.False(harness.Panel.WantsPlaylists);
    }

    [Fact]
    public async Task Playlists_on_asks_for_the_extra_permission()
    {
        var harness = new Harness(Platform.YouTube);
        await harness.SaveKeysAsync();
        harness.Panel.WantsPlaylists = true;

        await harness.Panel.ConnectAsync();

        Assert.Contains(GoogleOAuthProvider.PlaylistScope, harness.Provider.RequestedScopes.Single());
        Assert.True(harness.Panel.WantsPlaylists);
    }

    [Fact]
    public async Task Refusing_the_playlist_permission_still_connects_and_says_so()
    {
        var harness = new Harness(Platform.YouTube);
        harness.Provider.GrantsOptional = false;
        await harness.SaveKeysAsync();
        harness.Panel.WantsPlaylists = true;

        await harness.Panel.ConnectAsync();

        Assert.True(harness.Panel.IsConnected);
        Assert.False(harness.Panel.WantsPlaylists);
        Assert.Contains("wasn't granted", harness.Panel.Message);
    }

    [Fact]
    public async Task Turning_playlists_on_later_means_connecting_again()
    {
        var harness = new Harness(Platform.YouTube);
        await harness.SaveKeysAsync();
        await harness.Panel.ConnectAsync();

        harness.Panel.WantsPlaylists = true;

        Assert.True(harness.Panel.CanConnect);
        Assert.Contains("Connect again", harness.Panel.PlaylistNote);
    }

    [Fact]
    public async Task Giving_up_playlists_needs_a_disconnect_so_the_broader_sign_in_is_revoked()
    {
        var harness = new Harness(Platform.YouTube);
        await harness.SaveKeysAsync();
        harness.Panel.WantsPlaylists = true;
        await harness.Panel.ConnectAsync();

        harness.Panel.WantsPlaylists = false;

        Assert.False(harness.Panel.CanConnect);
        Assert.True(harness.Panel.CanDisconnect);
        Assert.Contains("disconnect", harness.Panel.PlaylistNote);
    }

    [Fact]
    public async Task A_disconnect_the_platform_heard_needs_no_revoke_link()
    {
        var harness = new Harness(Platform.TikTok);
        await harness.SaveKeysAsync();
        await harness.Panel.ConnectAsync();

        await harness.Panel.DisconnectAsync();

        Assert.Equal(ConnectionState.NotConnected, harness.Panel.State);
        Assert.False(harness.Panel.ShowsRevokeLink);
        Assert.True(harness.Panel.CanEditKeys);
    }

    [Fact]
    public async Task A_failed_revoke_points_to_the_platforms_site()
    {
        var harness = new Harness(Platform.YouTube);
        await harness.SaveKeysAsync();
        await harness.Panel.ConnectAsync();
        harness.Provider.RevokeFails = true;

        await harness.Panel.DisconnectAsync();

        Assert.Equal(ConnectionState.NotConnected, harness.Panel.State);
        Assert.True(harness.Panel.ShowsRevokeLink);
        Assert.Contains("remove the app", harness.Panel.Message);
    }

    [Fact]
    public async Task An_expired_sign_in_offers_connecting_again_and_unlocks_the_keys()
    {
        var harness = new Harness(Platform.TikTok);
        await harness.SaveKeysAsync();
        await harness.Panel.ConnectAsync();
        harness.Provider.RefreshFails = true;
        await Assert.ThrowsAsync<ReconnectRequiredException>(() => harness.Accounts.GetAccessTokenAsync(Platform.TikTok));

        await harness.Panel.LoadAsync();

        Assert.Equal(ConnectionState.NeedsReconnect, harness.Panel.State);
        Assert.Equal("Connect again", harness.Panel.ConnectText);
        Assert.True(harness.Panel.CanEditKeys);
        Assert.True(harness.Panel.CanConnect);
        Assert.True(harness.Panel.CanDisconnect);
    }

    [Fact]
    public async Task Removing_keys_empties_the_fields_and_the_store()
    {
        var harness = new Harness(Platform.TikTok);
        await harness.SaveKeysAsync();

        await harness.Panel.RemoveKeysAsync();

        Assert.Empty(harness.Store.Values);
        Assert.Equal("", harness.Panel.ClientId);
        Assert.False(harness.Panel.HasSavedSecret);
        Assert.False(harness.Panel.CanConnect);
    }

    [Fact]
    public async Task A_failing_store_shows_a_message_instead_of_crashing()
    {
        var harness = new Harness(Platform.TikTok);
        harness.Store.Fail = true;

        await harness.Panel.LoadAsync();

        Assert.True(harness.Panel.MessageIsError);
        Assert.Equal("The store failed.", harness.Panel.Message);
        Assert.False(harness.Panel.IsBusy);
    }

    [Fact]
    public void Only_YouTube_offers_playlists()
    {
        Assert.False(new Harness(Platform.TikTok).Panel.OffersPlaylists);
        Assert.True(new Harness(Platform.YouTube).Panel.OffersPlaylists);
    }

    [Theory]
    [InlineData("https://www.tiktok.com/v2/auth/authorize/", true)]
    [InlineData("http://www.tiktok.com/", false)]
    [InlineData("file:///C:/Windows/notepad.exe", false)]
    [InlineData("ms-settings:privacy", false)]
    public void Only_https_links_can_open(string url, bool expected)
    {
        Assert.Equal(expected, Browser.CanOpen(new Uri(url)));
    }

    [Theory]
    [InlineData(Platform.TikTok, "Sample Creator")]
    [InlineData(Platform.YouTube, "Sample Channel")]
    public async Task The_sample_mode_signs_in_with_the_real_flow_and_no_network(Platform platform, string name)
    {
        var services = await SampleServices.CreateAsync();
        var panel = new AccountPanel(services.Accounts, platform, services.OpenBrowser);
        await panel.LoadAsync();

        Assert.Equal($"Connected as {name}", panel.StateText);

        await panel.DisconnectAsync();

        Assert.Equal(ConnectionState.NotConnected, panel.State);
        Assert.False(panel.ShowsRevokeLink);

        await panel.ConnectAsync();

        Assert.Equal($"Connected as {name}", panel.StateText);
    }

    [Fact]
    public async Task Connecting_tells_whoever_is_waiting_on_the_account()
    {
        var services = await SampleServices.CreateAsync();
        var panel = new AccountPanel(services.Accounts, Platform.YouTube, services.OpenBrowser);
        await panel.LoadAsync();
        await panel.DisconnectAsync();
        var told = new List<Platform>();
        panel.Connected += told.Add;

        await panel.ConnectAsync();

        Assert.Equal([Platform.YouTube], told);
    }
}
