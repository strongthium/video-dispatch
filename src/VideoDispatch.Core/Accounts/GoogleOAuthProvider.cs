using System.Text.Json;
using VideoDispatch.Core.Model;

namespace VideoDispatch.Core.Accounts;

public sealed class GoogleOAuthProvider(HttpClient http) : IOAuthProvider
{
    public const string UploadScope = "https://www.googleapis.com/auth/youtube.upload";
    public const string ReadOnlyScope = "https://www.googleapis.com/auth/youtube.readonly";

    /// <summary>
    /// Only this scope and broader ones allow adding to a playlist, but it also allows deleting
    /// videos, comments and captions. So it is requested only when the user wants playlists.
    /// </summary>
    public const string PlaylistScope = "https://www.googleapis.com/auth/youtube.force-ssl";

    private const string AuthorizeEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";
    private const string ChannelEndpoint = "https://www.googleapis.com/youtube/v3/channels?part=snippet&mine=true";

    private const string TestingStatusHint =
        "If your Google app's publishing status is Testing, Google ends its sign-ins after 7 days.";

    public Platform Platform => Platform.YouTube;
    public bool RequiresClientSecret => false;
    public bool RevokeUsesAccessToken => false;
    public IReadOnlyList<string> RequiredScopes { get; } = [UploadScope, ReadOnlyScope];
    public IReadOnlyList<string> OptionalScopes { get; } = [PlaylistScope];
    public string CallbackPath => "/";

    public string BuildRedirectUri(int port) => $"http://127.0.0.1:{port}";

    public Uri BuildAuthorizationUri(
        AppCredentials credentials,
        IReadOnlyList<string> scopes,
        string redirectUri,
        string state,
        string codeVerifier)
    {
        return QueryString.AppendTo(AuthorizeEndpoint, new Dictionary<string, string>
        {
            ["client_id"] = credentials.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = string.Join(' ', scopes),
            ["state"] = state,
            ["code_challenge"] = Pkce.ToBase64UrlChallenge(codeVerifier),
            ["code_challenge_method"] = "S256",

            // Without these, signing in again to an app already approved may return no refresh token.
            ["access_type"] = "offline",
            ["prompt"] = "consent",
        });
    }

    public Task<TokenGrant> ExchangeCodeAsync(
        AppCredentials credentials,
        string code,
        string redirectUri,
        string codeVerifier,
        CancellationToken cancellationToken)
    {
        var form = WithClient(credentials, new Dictionary<string, string>
        {
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri,
        });

        return OAuthHttp.RequestTokenAsync(http, Platform, TokenEndpoint, form, isRefresh: false, expiredGrantHint: null, cancellationToken);
    }

    public Task<TokenGrant> RefreshAsync(AppCredentials credentials, string refreshToken, CancellationToken cancellationToken)
    {
        var form = WithClient(credentials, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        });

        return OAuthHttp.RequestTokenAsync(http, Platform, TokenEndpoint, form, isRefresh: true, TestingStatusHint, cancellationToken);
    }

    public Task RevokeAsync(AppCredentials credentials, string token, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string> { ["token"] = token };
        return OAuthHttp.RevokeAsync(http, Platform, RevokeEndpoint, form, cancellationToken);
    }

    public async Task<AccountIdentity> GetIdentityAsync(string accessToken, CancellationToken cancellationToken)
    {
        var root = await OAuthHttp.GetJsonAsync(http, ChannelEndpoint, accessToken, cancellationToken);
        if (root is not { ValueKind: JsonValueKind.Object } json)
        {
            return new AccountIdentity(null, null);
        }

        if (!json.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array ||
            items.GetArrayLength() == 0)
        {
            throw new AccountSetupException(
                Platform,
                "This Google account has no YouTube channel. Create one on YouTube, then connect again.");
        }

        var channel = items[0];
        var title = channel.ValueKind == JsonValueKind.Object && channel.TryGetProperty("snippet", out var snippet)
            ? OAuthHttp.GetString(snippet, "title")
            : null;
        return new AccountIdentity(OAuthHttp.GetString(channel, "id"), title);
    }

    private static Dictionary<string, string> WithClient(AppCredentials credentials, Dictionary<string, string> form)
    {
        form["client_id"] = credentials.ClientId;

        // Google's docs list the secret as optional for desktop clients.
        if (!string.IsNullOrEmpty(credentials.ClientSecret))
        {
            form["client_secret"] = credentials.ClientSecret;
        }

        return form;
    }
}
