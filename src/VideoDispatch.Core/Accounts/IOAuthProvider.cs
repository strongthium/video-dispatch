using VideoDispatch.Core.Model;

namespace VideoDispatch.Core.Accounts;

/// <summary>The parts of sign-in that differ between platforms.</summary>
public interface IOAuthProvider
{
    Platform Platform { get; }

    bool RequiresClientSecret { get; }

    /// <summary>TikTok's revoke call takes an access token, and Google's also accepts a refresh token.</summary>
    bool RevokeUsesAccessToken { get; }

    /// <summary>Sign-in fails if the user doesn't grant all of these.</summary>
    IReadOnlyList<string> RequiredScopes { get; }

    /// <summary>Requested only when the user asks for the features that need them.</summary>
    IReadOnlyList<string> OptionalScopes { get; }

    string CallbackPath { get; }

    string BuildRedirectUri(int port);

    Uri BuildAuthorizationUri(
        AppCredentials credentials,
        IReadOnlyList<string> scopes,
        string redirectUri,
        string state,
        string codeVerifier);

    Task<TokenGrant> ExchangeCodeAsync(
        AppCredentials credentials,
        string code,
        string redirectUri,
        string codeVerifier,
        CancellationToken cancellationToken);

    Task<TokenGrant> RefreshAsync(AppCredentials credentials, string refreshToken, CancellationToken cancellationToken);

    Task RevokeAsync(AppCredentials credentials, string token, CancellationToken cancellationToken);

    Task<AccountIdentity> GetIdentityAsync(string accessToken, CancellationToken cancellationToken);
}
