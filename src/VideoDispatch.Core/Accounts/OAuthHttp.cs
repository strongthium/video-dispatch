using System.Net;
using System.Text.Json;
using VideoDispatch.Core.Model;

namespace VideoDispatch.Core.Accounts;

/// <summary>Token endpoint handling shared by both platforms, which use the same error fields.</summary>
internal static class OAuthHttp
{
    private const int MaxServerMessageLength = 300;
    private static readonly string[] TransientErrors = ["server_error", "temporarily_unavailable"];

    public static async Task<TokenGrant> RequestTokenAsync(
        HttpClient http,
        Platform platform,
        string endpoint,
        Dictionary<string, string> form,
        bool isRefresh,
        string? expiredGrantHint,
        CancellationToken cancellationToken)
    {
        var (status, body) = await PostFormAsync(http, platform, endpoint, form, cancellationToken);
        var root = TryParse(body);

        ThrowIfError(platform, status, root, isRefresh, expiredGrantHint);

        if (root is not { ValueKind: JsonValueKind.Object } json ||
            GetString(json, "access_token") is not { Length: > 0 } accessToken)
        {
            throw new AccountSetupException(platform, $"{platform} sent an unexpected reply to the sign-in request.");
        }

        return new TokenGrant
        {
            AccessToken = accessToken,
            AccessTokenLifetime = GetSeconds(json, "expires_in") ?? TimeSpan.Zero,
            RefreshToken = GetString(json, "refresh_token"),

            // TikTok and Google use different names for the refresh token's lifetime.
            RefreshTokenLifetime = GetSeconds(json, "refresh_expires_in") ?? GetSeconds(json, "refresh_token_expires_in"),

            // TikTok separates scopes with commas and Google with spaces.
            Scopes = (GetString(json, "scope") ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries),
            AccountId = GetString(json, "open_id"),
        };
    }

    public static async Task RevokeAsync(
        HttpClient http,
        Platform platform,
        string endpoint,
        Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        var (status, body) = await PostFormAsync(http, platform, endpoint, form, cancellationToken);
        ThrowIfError(platform, status, TryParse(body), isRefresh: false, expiredGrantHint: null);

        if ((int)status >= 400)
        {
            throw new AccountSetupException(platform, $"{platform} refused to revoke access (HTTP {(int)status}).");
        }
    }

    public static async Task<JsonElement?> GetJsonAsync(
        HttpClient http,
        string url,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new("Bearer", accessToken);

        using var response = await http.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode
            ? TryParse(await response.Content.ReadAsStringAsync(cancellationToken))
            : null;
    }

    public static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static async Task<(HttpStatusCode Status, string Body)> PostFormAsync(
        HttpClient http,
        Platform platform,
        string endpoint,
        Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var response = await http.PostAsync(endpoint, content, cancellationToken);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (HttpRequestException exception)
        {
            throw new TransientAccountException(platform, $"Couldn't reach {platform}.", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TransientAccountException(platform, $"{platform} took too long to respond.", exception);
        }
    }

    private static void ThrowIfError(
        Platform platform,
        HttpStatusCode status,
        JsonElement? root,
        bool isRefresh,
        string? expiredGrantHint)
    {
        var error = root is { } json ? GetString(json, "error") : null;
        var isServerProblem = (int)status >= 500 || status == HttpStatusCode.TooManyRequests;

        if (isServerProblem || (error is not null && TransientErrors.Contains(error)))
        {
            throw new TransientAccountException(platform, $"{platform} is temporarily unavailable. Try again later.");
        }

        if (error is null)
        {
            return;
        }

        if (isRefresh && error == "invalid_grant")
        {
            throw new ReconnectRequiredException(
                platform,
                $"The {platform} sign-in has expired or was revoked. Connect the account again.{(expiredGrantHint is null ? "" : " " + expiredGrantHint)}");
        }

        var description = root is { } details ? GetString(details, "error_description") : null;
        var suffix = description is null ? "" : ": " + Shorten(description);
        throw new AccountSetupException(platform, $"{platform} refused the request ({Shorten(error)}){suffix}");
    }

    private static string Shorten(string text) =>
        text.Length > MaxServerMessageLength ? text[..MaxServerMessageLength] : text;

    private static JsonElement? TryParse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TimeSpan? GetSeconds(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds) && seconds is >= 0 and < int.MaxValue
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }
}
