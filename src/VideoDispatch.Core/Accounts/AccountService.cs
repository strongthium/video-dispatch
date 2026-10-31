using System.Text.Json;
using VideoDispatch.Core.Accounts.Secrets;
using VideoDispatch.Core.Model;

namespace VideoDispatch.Core.Accounts;

/// <summary>Connects accounts, keeps their sign-ins in the secure store and hands out access tokens.</summary>
public sealed class AccountService : IAccessTokenSource
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultSignInTimeout = TimeSpan.FromMinutes(5);

    private readonly ISecretStore _store;
    private readonly TimeProvider _time;
    private readonly Dictionary<Platform, IOAuthProvider> _providers;
    private readonly Dictionary<Platform, PlatformState> _states;

    public AccountService(ISecretStore store, IEnumerable<IOAuthProvider> providers, TimeProvider time)
    {
        _store = store;
        _time = time;
        _providers = providers.ToDictionary(provider => provider.Platform);
        _states = _providers.Keys.ToDictionary(platform => platform, _ => new PlatformState());
    }

    public AppCredentials? GetCredentials(Platform platform) =>
        Load<AppCredentials>(CredentialsKey(platform));

    /// <summary>Saves the app keys. Sign-ins belong to the old client, so changing it removes them.</summary>
    public async Task SaveCredentialsAsync(Platform platform, AppCredentials credentials, CancellationToken cancellationToken = default)
    {
        var provider = _providers[platform];
        var clientId = credentials.ClientId.Trim();
        var clientSecret = credentials.ClientSecret?.Trim();

        if (clientId.Length == 0)
        {
            throw new AccountSetupException(platform, "Enter the client ID from your app registration.");
        }

        if (provider.RequiresClientSecret && string.IsNullOrEmpty(clientSecret))
        {
            throw new AccountSetupException(platform, $"{platform} requires a client secret.");
        }

        var state = _states[platform];
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            if (GetCredentials(platform) is { } existing && existing.ClientId != clientId)
            {
                _store.Delete(AccountKey(platform));
                state.ClearAccessToken();
            }

            Save(CredentialsKey(platform), new AppCredentials(clientId, clientSecret));
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public AccountStatus GetStatus(Platform platform)
    {
        var account = Load<StoredAccount>(AccountKey(platform));
        var connection = account switch
        {
            null => ConnectionState.NotConnected,
            _ when IsSignInOver(account) => ConnectionState.NeedsReconnect,
            _ => ConnectionState.Connected,
        };

        return new AccountStatus(
            platform,
            GetCredentials(platform) is not null,
            connection,
            account?.DisplayName,
            account?.Scopes ?? []);
    }

    /// <summary>Runs the browser sign-in and saves the result.</summary>
    /// <param name="platform">The platform to connect.</param>
    /// <param name="openBrowser">Opens the given address in the user's browser.</param>
    /// <param name="includeOptionalScopes">Also asks for the permissions only some features need.</param>
    /// <param name="timeout">How long to wait for the user. Defaults to five minutes.</param>
    /// <param name="cancellationToken">Cancels the sign-in.</param>
    public async Task<AccountStatus> ConnectAsync(
        Platform platform,
        Func<Uri, CancellationToken, Task> openBrowser,
        bool includeOptionalScopes = false,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var provider = _providers[platform];
        var credentials = GetCredentials(platform)
            ?? throw new AccountSetupException(platform, $"Enter your {platform} app keys first.");

        var verifier = Pkce.CreateVerifier();
        var expectedState = Pkce.CreateState();
        var scopes = includeOptionalScopes
            ? provider.RequiredScopes.Concat(provider.OptionalScopes).ToList()
            : provider.RequiredScopes;

        await using var listener = LoopbackCallbackListener.Start(provider.CallbackPath, expectedState);
        var redirectUri = provider.BuildRedirectUri(listener.Port);

        await openBrowser(
            provider.BuildAuthorizationUri(credentials, scopes, redirectUri, expectedState, verifier),
            cancellationToken);

        var callback = await WaitForCallbackAsync(platform, listener, timeout ?? DefaultSignInTimeout, cancellationToken);
        if (callback.Code is not { } code)
        {
            throw new AccountSetupException(platform, DescribeCallbackError(platform, callback));
        }

        var grant = await provider.ExchangeCodeAsync(credentials, code, redirectUri, verifier, cancellationToken);

        var missing = grant.Scopes.Count == 0 ? [] : provider.RequiredScopes.Except(grant.Scopes).ToList();
        if (missing.Count > 0)
        {
            throw new AccountSetupException(
                platform,
                $"Sign-in didn't grant every permission the app needs. Missing: {string.Join(", ", missing)}.");
        }

        if (string.IsNullOrEmpty(grant.RefreshToken))
        {
            throw new AccountSetupException(platform, $"{platform} didn't return a lasting sign-in. Try connecting again.");
        }

        var identity = await TryGetIdentityAsync(provider, grant.AccessToken, cancellationToken);
        var now = _time.GetUtcNow();
        var account = new StoredAccount
        {
            RefreshToken = grant.RefreshToken,
            RefreshTokenExpiresAt = now + grant.RefreshTokenLifetime,
            Scopes = grant.Scopes,
            AccountId = identity.Id ?? grant.AccountId,
            DisplayName = identity.DisplayName,
        };

        var state = _states[platform];
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            Save(AccountKey(platform), account);
            state.SetAccessToken(grant, now);
        }
        finally
        {
            state.Gate.Release();
        }

        return GetStatus(platform);
    }

    /// <summary>
    /// Removes the sign-in from this computer and asks the platform to revoke it.
    /// The sign-in is removed either way. Returns false when the platform couldn't be told.
    /// </summary>
    public async Task<bool> DisconnectAsync(Platform platform, CancellationToken cancellationToken = default)
    {
        var provider = _providers[platform];
        var account = Load<StoredAccount>(AccountKey(platform));
        var credentials = GetCredentials(platform);
        var revoked = account is null;

        if (account is not null && credentials is not null)
        {
            try
            {
                var token = provider.RevokeUsesAccessToken
                    ? await GetAccessTokenAsync(platform, cancellationToken)
                    : account.RefreshToken;

                await provider.RevokeAsync(credentials, token, cancellationToken);
                revoked = true;
            }
            catch (AccountException)
            {
                // The local copy still goes, and the caller tells the user to revoke it on the platform's site.
            }
        }

        var state = _states[platform];
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            _store.Delete(AccountKey(platform));
            state.ClearAccessToken();
        }
        finally
        {
            state.Gate.Release();
        }

        return revoked;
    }

    public async Task<string> GetAccessTokenAsync(Platform platform, CancellationToken cancellationToken = default)
    {
        var state = _states[platform];
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            var now = _time.GetUtcNow();
            if (state.AccessToken is { } cached && state.AccessTokenExpiresAt - now > RefreshMargin)
            {
                return cached;
            }

            var account = Load<StoredAccount>(AccountKey(platform))
                ?? throw new ReconnectRequiredException(platform, $"Connect your {platform} account first.");

            if (IsSignInOver(account))
            {
                throw new ReconnectRequiredException(platform, $"The {platform} sign-in has expired. Connect the account again.");
            }

            var credentials = GetCredentials(platform)
                ?? throw new AccountSetupException(platform, $"Enter your {platform} app keys first.");

            TokenGrant grant;
            try
            {
                grant = await _providers[platform].RefreshAsync(credentials, account.RefreshToken, cancellationToken);
            }
            catch (ReconnectRequiredException)
            {
                Save(AccountKey(platform), account with { NeedsReconnect = true });
                state.ClearAccessToken();
                throw;
            }

            // A refresh can replace the refresh token, and the old one may stop working, so save before use.
            Save(AccountKey(platform), account with
            {
                RefreshToken = string.IsNullOrEmpty(grant.RefreshToken) ? account.RefreshToken : grant.RefreshToken,
                RefreshTokenExpiresAt = grant.RefreshTokenLifetime is { } lifetime ? now + lifetime : account.RefreshTokenExpiresAt,
                Scopes = grant.Scopes.Count > 0 ? grant.Scopes : account.Scopes,
            });

            state.SetAccessToken(grant, now);
            return grant.AccessToken;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task ReportRejectedAsync(Platform platform, string accessToken, CancellationToken cancellationToken = default)
    {
        var state = _states[platform];
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            // Another request may already have replaced this token, and that one must stay.
            if (state.AccessToken == accessToken)
            {
                state.ClearAccessToken();
            }
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private static string CredentialsKey(Platform platform) => $"{platform.ToString().ToLowerInvariant()}/app";

    private static string AccountKey(Platform platform) => $"{platform.ToString().ToLowerInvariant()}/account";

    private static async Task<AuthorizationCallback> WaitForCallbackAsync(
        Platform platform,
        LoopbackCallbackListener listener,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);

        try
        {
            return await listener.WaitAsync(limit.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AccountSetupException(platform, "Sign-in timed out. Try connecting again.");
        }
    }

    private static string DescribeCallbackError(Platform platform, AuthorizationCallback callback) =>
        callback.Error == "access_denied"
            ? $"The {platform} sign-in was canceled or denied."
            : $"{platform} refused the sign-in ({callback.Error}){(callback.ErrorDescription is { } description ? ": " + description : "")}";

    private static async Task<AccountIdentity> TryGetIdentityAsync(
        IOAuthProvider provider,
        string accessToken,
        CancellationToken cancellationToken)
    {
        try
        {
            return await provider.GetIdentityAsync(accessToken, cancellationToken);
        }
        catch (Exception exception) when (
            exception is HttpRequestException ||
            (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // The name is only for display, so a network problem shouldn't undo a good sign-in.
            return new AccountIdentity(null, null);
        }
    }

    private bool IsSignInOver(StoredAccount account) =>
        account.NeedsReconnect || account.RefreshTokenExpiresAt <= _time.GetUtcNow();

    /// <summary>A damaged entry reads as missing, so the user is asked to enter it again.</summary>
    private T? Load<T>(string key)
        where T : class
    {
        if (_store.Get(key) is not { } json)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, StoredJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Save<T>(string key, T value) =>
        _store.Set(key, JsonSerializer.Serialize(value, StoredJson.Options));

    private sealed class PlatformState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public string? AccessToken { get; private set; }
        public DateTimeOffset AccessTokenExpiresAt { get; private set; }

        public void SetAccessToken(TokenGrant grant, DateTimeOffset now)
        {
            AccessToken = grant.AccessToken;
            AccessTokenExpiresAt = now + grant.AccessTokenLifetime;
        }

        public void ClearAccessToken()
        {
            AccessToken = null;
            AccessTokenExpiresAt = default;
        }
    }
}
