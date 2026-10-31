using System.Net;
using VideoDispatch.Core.Accounts;
using VideoDispatch.Core.Model;

namespace VideoDispatch.Core.Tests.Accounts;

public class OAuthProviderTests
{
    private const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string TikTokTokens =
        """{"access_token":"act.1","expires_in":86400,"open_id":"open-1","refresh_expires_in":31536000,"refresh_token":"rft.1","scope":"user.info.basic,video.upload","token_type":"Bearer"}""";
    private const string GoogleTokens =
        """{"access_token":"ya29.1","expires_in":3599,"refresh_token":"1//r","scope":"https://www.googleapis.com/auth/youtube.upload https://www.googleapis.com/auth/youtube.readonly","token_type":"Bearer"}""";

    private static readonly AppCredentials TikTokKeys = new("tiktok-key", "tiktok-secret");
    private static readonly AppCredentials GoogleKeys = new("google-id.apps.googleusercontent.com", "google-secret");

    private static (TikTokOAuthProvider Provider, FakeHttpHandler Http) TikTok(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(json, status));
        return (new TikTokOAuthProvider(new HttpClient(handler)), handler);
    }

    private static (GoogleOAuthProvider Provider, FakeHttpHandler Http) Google(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(json, status));
        return (new GoogleOAuthProvider(new HttpClient(handler)), handler);
    }

    [Fact]
    public void TikTok_authorization_address_follows_the_desktop_rules()
    {
        var (provider, _) = TikTok("{}");
        var redirect = provider.BuildRedirectUri(5000);

        var uri = provider.BuildAuthorizationUri(TikTokKeys, provider.RequiredScopes, redirect, "state-1", Verifier);
        var query = QueryParser.Parse(uri.Query);

        Assert.StartsWith("https://www.tiktok.com/v2/auth/authorize/?", uri.AbsoluteUri);
        Assert.Equal("tiktok-key", query["client_key"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("user.info.basic,video.upload", query["scope"]);
        Assert.Equal("http://127.0.0.1:5000/callback/", query["redirect_uri"]);
        Assert.Equal("state-1", query["state"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(Pkce.ToHexChallenge(Verifier), query["code_challenge"]);
        Assert.DoesNotContain("tiktok-secret", uri.AbsoluteUri);
        Assert.DoesNotContain(Verifier, uri.AbsoluteUri);
    }

    [Fact]
    public void Google_authorization_address_follows_the_native_app_rules()
    {
        var (provider, _) = Google("{}");
        var redirect = provider.BuildRedirectUri(5000);

        var uri = provider.BuildAuthorizationUri(GoogleKeys, provider.RequiredScopes, redirect, "state-1", Verifier);
        var query = QueryParser.Parse(uri.Query);

        Assert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?", uri.AbsoluteUri);
        Assert.Equal(GoogleKeys.ClientId, query["client_id"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal($"{GoogleOAuthProvider.UploadScope} {GoogleOAuthProvider.ReadOnlyScope}", query["scope"]);
        Assert.Equal("http://127.0.0.1:5000", query["redirect_uri"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(Pkce.ToBase64UrlChallenge(Verifier), query["code_challenge"]);
        Assert.Equal("consent", query["prompt"]);
        Assert.Equal("offline", query["access_type"]);
        Assert.DoesNotContain("google-secret", uri.AbsoluteUri);
    }

    [Fact]
    public void Google_asks_for_the_delete_capable_scope_only_as_optional()
    {
        var (provider, _) = Google("{}");

        Assert.DoesNotContain(GoogleOAuthProvider.PlaylistScope, provider.RequiredScopes);
        Assert.Contains(GoogleOAuthProvider.PlaylistScope, provider.OptionalScopes);
    }

    [Fact]
    public async Task TikTok_exchange_sends_every_required_field()
    {
        var (provider, http) = TikTok(TikTokTokens);

        var grant = await provider.ExchangeCodeAsync(TikTokKeys, "code-1", "http://127.0.0.1:5000/callback/", Verifier, default);

        var request = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://open.tiktokapis.com/v2/oauth/token/", request.Uri.AbsoluteUri);
        Assert.Equal("tiktok-key", request.Form["client_key"]);
        Assert.Equal("tiktok-secret", request.Form["client_secret"]);
        Assert.Equal("code-1", request.Form["code"]);
        Assert.Equal("authorization_code", request.Form["grant_type"]);
        Assert.Equal("http://127.0.0.1:5000/callback/", request.Form["redirect_uri"]);
        Assert.Equal(Verifier, request.Form["code_verifier"]);

        Assert.Equal("act.1", grant.AccessToken);
        Assert.Equal("rft.1", grant.RefreshToken);
        Assert.Equal("open-1", grant.AccountId);
        Assert.Equal(TimeSpan.FromHours(24), grant.AccessTokenLifetime);
        Assert.Equal(TimeSpan.FromDays(365), grant.RefreshTokenLifetime);
        Assert.Equal(["user.info.basic", "video.upload"], grant.Scopes);
    }

    [Fact]
    public async Task TikTok_refresh_sends_every_required_field()
    {
        var (provider, http) = TikTok(TikTokTokens);

        await provider.RefreshAsync(TikTokKeys, "rft.0", default);

        var form = Assert.Single(http.Requests).Form;
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal("rft.0", form["refresh_token"]);
        Assert.Equal("tiktok-secret", form["client_secret"]);
    }

    [Fact]
    public async Task TikTok_revoke_sends_the_token_and_accepts_an_empty_reply()
    {
        var (provider, http) = TikTok("");

        await provider.RevokeAsync(TikTokKeys, "act.1", default);

        var request = Assert.Single(http.Requests);
        Assert.Equal("https://open.tiktokapis.com/v2/oauth/revoke/", request.Uri.AbsoluteUri);
        Assert.Equal("act.1", request.Form["token"]);
        Assert.True(provider.RevokeUsesAccessToken);
    }

    [Fact]
    public async Task TikTok_reads_the_display_name_with_a_bearer_token()
    {
        var (provider, http) = TikTok("""{"data":{"user":{"open_id":"open-1","display_name":"Creator"}},"error":{"code":"ok","message":"","log_id":"1"}}""");

        var identity = await provider.GetIdentityAsync("act.1", default);

        var request = Assert.Single(http.Requests);
        Assert.Contains("fields=open_id,display_name", request.Uri.AbsoluteUri);
        Assert.Equal("Bearer act.1", request.Authorization);
        Assert.DoesNotContain("act.1", request.Uri.AbsoluteUri);
        Assert.Equal(new AccountIdentity("open-1", "Creator"), identity);
    }

    [Theory]
    [InlineData("""{"data":{},"error":{"code":"scope_not_authorized"}}""")]
    [InlineData("not json")]
    [InlineData("[]")]
    public async Task TikTok_identity_is_unknown_when_the_reply_has_none(string json)
    {
        var (provider, _) = TikTok(json);

        Assert.Equal(new AccountIdentity(null, null), await provider.GetIdentityAsync("act.1", default));
    }

    [Fact]
    public async Task Google_exchange_sends_every_required_field()
    {
        var (provider, http) = Google(GoogleTokens);

        var grant = await provider.ExchangeCodeAsync(GoogleKeys, "code-1", "http://127.0.0.1:5000", Verifier, default);

        var request = Assert.Single(http.Requests);
        Assert.Equal("https://oauth2.googleapis.com/token", request.Uri.AbsoluteUri);
        Assert.Equal(GoogleKeys.ClientId, request.Form["client_id"]);
        Assert.Equal("google-secret", request.Form["client_secret"]);
        Assert.Equal("code-1", request.Form["code"]);
        Assert.Equal(Verifier, request.Form["code_verifier"]);
        Assert.Equal("authorization_code", request.Form["grant_type"]);
        Assert.Equal("http://127.0.0.1:5000", request.Form["redirect_uri"]);

        Assert.Equal("ya29.1", grant.AccessToken);
        Assert.Equal([GoogleOAuthProvider.UploadScope, GoogleOAuthProvider.ReadOnlyScope], grant.Scopes);
        Assert.Null(grant.RefreshTokenLifetime);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Google_leaves_out_a_blank_secret(string? secret)
    {
        var (provider, http) = Google(GoogleTokens);

        await provider.RefreshAsync(new AppCredentials("id", secret), "r", default);

        Assert.False(Assert.Single(http.Requests).Form.ContainsKey("client_secret"));
    }

    [Fact]
    public async Task Google_refresh_without_a_new_refresh_token_is_fine()
    {
        var (provider, _) = Google("""{"access_token":"ya29.2","expires_in":3599,"scope":"a b","token_type":"Bearer"}""");

        var grant = await provider.RefreshAsync(GoogleKeys, "r", default);

        Assert.Equal("ya29.2", grant.AccessToken);
        Assert.Null(grant.RefreshToken);
    }

    [Fact]
    public async Task Google_reads_the_channel_name()
    {
        var (provider, http) = Google("""{"items":[{"id":"UC1","snippet":{"title":"My Channel"}}]}""");

        var identity = await provider.GetIdentityAsync("ya29.1", default);

        Assert.Equal("Bearer ya29.1", Assert.Single(http.Requests).Authorization);
        Assert.Equal(new AccountIdentity("UC1", "My Channel"), identity);
    }

    [Theory]
    [InlineData("""{"items":[]}""")]
    [InlineData("""{"kind":"youtube#channelListResponse"}""")]
    public async Task Google_says_when_the_account_has_no_channel(string json)
    {
        var (provider, _) = Google(json);

        var exception = await Assert.ThrowsAsync<AccountSetupException>(() => provider.GetIdentityAsync("ya29.1", default));

        Assert.Contains("no YouTube channel", exception.Message);
    }

    [Fact]
    public async Task Google_identity_is_unknown_when_the_request_fails()
    {
        var (provider, _) = Google("""{"error":{"code":403}}""", HttpStatusCode.Forbidden);

        Assert.Equal(new AccountIdentity(null, null), await provider.GetIdentityAsync("ya29.1", default));
    }

    [Fact]
    public async Task Refresh_with_invalid_grant_requires_reconnecting()
    {
        // TikTok's docs don't state the HTTP status for errors, so both forms are covered.
        foreach (var status in new[] { HttpStatusCode.OK, HttpStatusCode.BadRequest })
        {
            var (provider, _) = TikTok("""{"error":"invalid_grant","error_description":"expired","log_id":"1"}""", status);

            var exception = await Assert.ThrowsAsync<ReconnectRequiredException>(() => provider.RefreshAsync(TikTokKeys, "r", default));

            Assert.Equal(Platform.TikTok, exception.Platform);
        }
    }

    [Fact]
    public async Task Google_reconnect_message_explains_the_7_day_limit()
    {
        var (provider, _) = Google("""{"error":"invalid_grant","error_description":"Token has been expired or revoked."}""", HttpStatusCode.BadRequest);

        var exception = await Assert.ThrowsAsync<ReconnectRequiredException>(() => provider.RefreshAsync(GoogleKeys, "r", default));

        Assert.Contains("7 days", exception.Message);
    }

    [Fact]
    public async Task Exchange_with_invalid_grant_is_a_setup_problem_not_a_lost_sign_in()
    {
        var (provider, _) = TikTok("""{"error":"invalid_grant","error_description":"code expired"}""");

        var exception = await Assert.ThrowsAsync<AccountSetupException>(() => provider.ExchangeCodeAsync(TikTokKeys, "c", "r", Verifier, default));

        Assert.Contains("code expired", exception.Message);
    }

    [Theory]
    [InlineData("invalid_client")]
    [InlineData("unauthorized_client")]
    [InlineData("invalid_scope")]
    [InlineData("invalid_request")]
    public async Task Rejected_keys_are_a_setup_problem(string error)
    {
        var (provider, _) = TikTok($$"""{"error":"{{error}}","error_description":"nope"}""", HttpStatusCode.BadRequest);

        var exception = await Assert.ThrowsAsync<AccountSetupException>(() => provider.RefreshAsync(TikTokKeys, "r", default));

        Assert.Contains(error, exception.Message);
    }

    [Theory]
    [InlineData("""{"error":"server_error"}""", HttpStatusCode.OK)]
    [InlineData("""{"error":"temporarily_unavailable"}""", HttpStatusCode.OK)]
    [InlineData("<html>Bad gateway</html>", HttpStatusCode.BadGateway)]
    [InlineData("", HttpStatusCode.InternalServerError)]
    [InlineData("""{"error":"rate_limited"}""", HttpStatusCode.TooManyRequests)]
    public async Task Server_problems_can_be_retried(string body, HttpStatusCode status)
    {
        var (provider, _) = TikTok(body, status);

        await Assert.ThrowsAsync<TransientAccountException>(() => provider.RefreshAsync(TikTokKeys, "r", default));
    }

    [Fact]
    public async Task A_network_failure_can_be_retried()
    {
        var provider = new TikTokOAuthProvider(new HttpClient(new FakeHttpHandler(_ => throw new HttpRequestException("down"))));

        await Assert.ThrowsAsync<TransientAccountException>(() => provider.RefreshAsync(TikTokKeys, "r", default));
    }

    [Fact]
    public async Task A_client_timeout_can_be_retried()
    {
        var provider = new TikTokOAuthProvider(new HttpClient(new FakeHttpHandler(_ => throw new TaskCanceledException("timed out"))));

        await Assert.ThrowsAsync<TransientAccountException>(() => provider.RefreshAsync(TikTokKeys, "r", default));
    }

    [Fact]
    public async Task Canceling_is_not_reported_as_a_retryable_failure()
    {
        var (provider, _) = TikTok(TikTokTokens);
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.RefreshAsync(TikTokKeys, "r", cancel.Token));
    }

    [Theory]
    [InlineData("""{"token_type":"Bearer"}""")]
    [InlineData("""{"access_token":""}""")]
    [InlineData("""{"access_token":123}""")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("")]
    public async Task A_reply_without_a_token_is_an_error(string body)
    {
        var (provider, _) = TikTok(body);

        await Assert.ThrowsAsync<AccountSetupException>(() => provider.RefreshAsync(TikTokKeys, "r", default));
    }

    [Fact]
    public async Task A_failed_revoke_is_reported()
    {
        var (provider, _) = Google("", HttpStatusCode.BadRequest);

        await Assert.ThrowsAsync<AccountSetupException>(() => provider.RevokeAsync(GoogleKeys, "r", default));
    }

    [Fact]
    public async Task Error_messages_never_contain_the_secret_or_tokens()
    {
        var (provider, _) = TikTok("""{"error":"invalid_client","error_description":"bad"}""", HttpStatusCode.Unauthorized);

        var exception = await Assert.ThrowsAsync<AccountSetupException>(() => provider.RefreshAsync(TikTokKeys, "rft.secret-value", default));

        Assert.DoesNotContain("tiktok-secret", exception.ToString());
        Assert.DoesNotContain("rft.secret-value", exception.ToString());
    }
}
