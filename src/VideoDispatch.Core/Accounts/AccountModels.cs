using VideoDispatch.Core.Model;

namespace VideoDispatch.Core.Accounts;

/// <summary>The keys from the user's own TikTok or Google app registration.</summary>
public sealed record AppCredentials(string ClientId, string? ClientSecret)
{
    public override string ToString() => $"AppCredentials {{ ClientId = {ClientId}, ClientSecret = *** }}";
}

/// <summary>What a token endpoint returned.</summary>
public sealed record TokenGrant
{
    public required string AccessToken { get; init; }
    public TimeSpan AccessTokenLifetime { get; init; }
    public string? RefreshToken { get; init; }
    public TimeSpan? RefreshTokenLifetime { get; init; }
    public IReadOnlyList<string> Scopes { get; init; } = [];
    public string? AccountId { get; init; }

    public override string ToString() => "TokenGrant { *** }";
}

/// <summary>
/// What is kept in the secure store for a connected account. Access tokens stay in memory:
/// they are short-lived, and leaving them out keeps this under Credential Manager's size limit.
/// </summary>
public sealed record StoredAccount
{
    public required string RefreshToken { get; init; }
    public DateTimeOffset? RefreshTokenExpiresAt { get; init; }
    public IReadOnlyList<string> Scopes { get; init; } = [];
    public string? AccountId { get; init; }
    public string? DisplayName { get; init; }
    public bool NeedsReconnect { get; init; }

    public override string ToString() => $"StoredAccount {{ DisplayName = {DisplayName}, RefreshToken = *** }}";
}

public sealed record AccountIdentity(string? Id, string? DisplayName);

public enum ConnectionState
{
    NotConnected,
    Connected,
    NeedsReconnect,
}

public sealed record AccountStatus(
    Platform Platform,
    bool HasCredentials,
    ConnectionState State,
    string? DisplayName,
    IReadOnlyList<string> Scopes);

/// <summary>What the upload clients use to get a working access token.</summary>
public interface IAccessTokenSource
{
    Task<string> GetAccessTokenAsync(Platform platform, CancellationToken cancellationToken = default);

    /// <summary>Call when the platform refuses a token, so the next request gets a new one.</summary>
    Task ReportRejectedAsync(Platform platform, string accessToken, CancellationToken cancellationToken = default);
}
