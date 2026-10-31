using System.Text.Json;
using VideoDispatch.Core.Model;

namespace VideoDispatch.Core.Accounts;

public sealed class TikTokOAuthProvider(HttpClient http) : IOAuthProvider
{
    public const string UserInfoScope = "user.info.basic";
    public const string VideoUploadScope = "video.upload";

    private const string AuthorizeEndpoint = "https://www.tiktok.com/v2/auth/authorize/";
    private const string TokenEndpoint = "https://open.tiktokapis.com/v2/oauth/token/";
    private const string RevokeEndpoint = "https://open.tiktokapis.com/v2/oauth/revoke/";
    private const string UserInfoEndpoint = "https://open.tiktokapis.com/v2/user/info/?fields=open_id,display_name";

    public Platform Platform => Platform.TikTok;
    public bool RequiresClientSecret => true;
    public bool RevokeUsesAccessToken => true;
    public IReadOnlyList<string> RequiredScopes { get; } = [UserInfoScope, VideoUploadScope];
    public IReadOnlyList<string> OptionalScopes { get; } = [];
    public string CallbackPath => "/callback/";

    public string BuildRedirectUri(int port) => $"http://127.0.0.1:{port}/callback/";

    public Uri BuildAuthorizationUri(
        AppCredentials credentials,
        IReadOnlyList<string> scopes,
        string redirectUri,
        string state,
        string codeVerifier)
    {
        return QueryString.AppendTo(AuthorizeEndpoint, new Dictionary<string, string>
        {
            ["client_key"] = credentials.ClientId,
            ["response_type"] = "code",
            ["scope"] = string.Join(',', scopes),
            ["redirect_uri"] = redirectUri,
            ["state"] = state,
            ["code_challenge"] = Pkce.ToHexChallenge(codeVerifier),
            ["code_challenge_method"] = "S256",
        });
    }

    public Task<TokenGrant> ExchangeCodeAsync(
        AppCredentials credentials,
        string code,
        string redirectUri,
        string codeVerifier,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["client_key"] = credentials.ClientId,
            ["client_secret"] = credentials.ClientSecret ?? "",
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
        };

        return OAuthHttp.RequestTokenAsync(http, Platform, TokenEndpoint, form, isRefresh: false, expiredGrantHint: null, cancellationToken);
    }

    public Task<TokenGrant> RefreshAsync(AppCredentials credentials, string refreshToken, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["client_key"] = credentials.ClientId,
            ["client_secret"] = credentials.ClientSecret ?? "",
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        };

        return OAuthHttp.RequestTokenAsync(http, Platform, TokenEndpoint, form, isRefresh: true, expiredGrantHint: null, cancellationToken);
    }

    public Task RevokeAsync(AppCredentials credentials, string token, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["client_key"] = credentials.ClientId,
            ["client_secret"] = credentials.ClientSecret ?? "",
            ["token"] = token,
        };

        return OAuthHttp.RevokeAsync(http, Platform, RevokeEndpoint, form, cancellationToken);
    }

    public async Task<AccountIdentity> GetIdentityAsync(string accessToken, CancellationToken cancellationToken)
    {
        var root = await OAuthHttp.GetJsonAsync(http, UserInfoEndpoint, accessToken, cancellationToken);
        if (root is not { ValueKind: JsonValueKind.Object } json ||
            !json.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("user", out var user))
        {
            return new AccountIdentity(null, null);
        }

        return new AccountIdentity(OAuthHttp.GetString(user, "open_id"), OAuthHttp.GetString(user, "display_name"));
    }
}
