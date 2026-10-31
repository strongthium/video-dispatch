using System.Net;
using System.Text.Json;
using VideoDispatch.Core.Accounts;
using VideoDispatch.Core.Model;

namespace VideoDispatch.Core.Tests.Accounts;

public class AccountServiceTests
{
    private const string TikTokTokens =
        """{"access_token":"act.first","expires_in":86400,"open_id":"open-1","refresh_expires_in":31536000,"refresh_token":"rft.first","scope":"user.info.basic,video.upload"}""";
    private const string TikTokRefreshed =
        """{"access_token":"act.second","expires_in":86400,"open_id":"open-1","refresh_expires_in":31536000,"refresh_token":"rft.second","scope":"user.info.basic,video.upload"}""";
    private const string TikTokUser =
        """{"data":{"user":{"open_id":"open-1","display_name":"Creator"}},"error":{"code":"ok"}}""";
    private const string GoogleTokens =
        """{"access_token":"ya29.first","expires_in":3600,"refresh_token":"goog-refresh","scope":"https://www.googleapis.com/auth/youtube.upload https://www.googleapis.com/auth/youtube.readonly"}""";
    private const string GoogleChannel =
        """{"items":[{"id":"UC1","snippet":{"title":"My Channel"}}]}""";

    private static readonly HttpClient Browser = new();

    private sealed class Harness
    {
        public InMemorySecretStore Store { get; } = new();
        public ManualTime Time { get; } = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
        public FakeHttpHandler Http { get; }
        public AccountService Service { get; }
        public Func<RecordedRequest, HttpResponseMessage>? Override { get; set; }

        public Harness()
        {
            Http = new FakeHttpHandler(request => Override?.Invoke(request) ?? Respond(request));
            var client = new HttpClient(Http);
            Service = new AccountService(Store, [new TikTokOAuthProvider(client), new GoogleOAuthProvider(client)], Time);
        }

        public static HttpResponseMessage Respond(RecordedRequest request) => request.Uri.AbsoluteUri switch
        {
            var url when url.StartsWith("https://open.tiktokapis.com/v2/oauth/token/") =>
                FakeHttpHandler.Json(request.Form["grant_type"] == "refresh_token" ? TikTokRefreshed : TikTokTokens),
            var url when url.StartsWith("https://open.tiktokapis.com/v2/user/info/") => FakeHttpHandler.Json(TikTokUser),
            var url when url.StartsWith("https://open.tiktokapis.com/v2/oauth/revoke/") => FakeHttpHandler.Json(""),
            var url when url.StartsWith("https://oauth2.googleapis.com/token") => FakeHttpHandler.Json(GoogleTokens),
            var url when url.StartsWith("https://www.googleapis.com/youtube/v3/channels") => FakeHttpHandler.Json(GoogleChannel),
            var url when url.StartsWith("https://oauth2.googleapis.com/revoke") => FakeHttpHandler.Json(""),
            var url => throw new InvalidOperationException($"Unexpected request to {url}"),
        };

        public IEnumerable<RecordedRequest> TokenRequests =>
            Http.Requests.Where(request => request.Uri.AbsoluteUri.Contains("oauth/token") || request.Uri.AbsoluteUri.Contains("oauth2.googleapis.com/token"));

        public Uri? LastAuthorizationUri { get; private set; }

        /// <summary>Acts as the user's browser: follows the redirect back to the app with a code.</summary>
        public async Task ApproveInBrowserAsync(Uri authorizationUri, CancellationToken cancellationToken)
        {
            LastAuthorizationUri = authorizationUri;
            var query = QueryParser.Parse(authorizationUri.Query);
            var callback = $"{query["redirect_uri"]}?code=the-code&state={Uri.EscapeDataString(query["state"])}";
            using var response = await Browser.GetAsync(callback, cancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        public async Task ConnectTikTokAsync()
        {
            await Service.SaveCredentialsAsync(Platform.TikTok, new AppCredentials("key", "secret"));
            await Service.ConnectAsync(Platform.TikTok, ApproveInBrowserAsync);
        }

        public StoredAccount? StoredTikTok() =>
            Store.Get("tiktok/account") is { } json ? JsonSerializer.Deserialize<StoredAccount>(json, StoredJson.Options) : null;
    }

    [Fact]
    public async Task TikTok_connect_runs_the_whole_flow_with_matching_PKCE()
    {
        var harness = new Harness();
        await harness.Service.SaveCredentialsAsync(Platform.TikTok, new AppCredentials("key", "secret"));

        var status = await harness.Service.ConnectAsync(Platform.TikTok, harness.ApproveInBrowserAsync);

        Assert.Equal(ConnectionState.Connected, status.State);
        Assert.Equal("Creator", status.DisplayName);

        var authorization = QueryParser.Parse(harness.LastAuthorizationUri!.Query);
        var exchange = Assert.Single(harness.TokenRequests).Form;
        Assert.Equal("the-code", exchange["code"]);
        Assert.Equal(authorization["redirect_uri"], exchange["redirect_uri"]);
        Assert.Equal(authorization["code_challenge"], Pkce.ToHexChallenge(exchange["code_verifier"]));
    }

    [Fact]
    public async Task Google_connect_runs_the_whole_flow_with_matching_PKCE()
    {
        var harness = new Harness();
        await harness.Service.SaveCredentialsAsync(Platform.YouTube, new AppCredentials("id", null));

        var status = await harness.Service.ConnectAsync(Platform.YouTube, harness.ApproveInBrowserAsync);

        Assert.Equal(ConnectionState.Connected, status.State);
        Assert.Equal("My Channel", status.DisplayName);

        var authorization = QueryParser.Parse(harness.LastAuthorizationUri!.Query);
        var exchange = Assert.Single(harness.TokenRequests).Form;
        Assert.Equal(authorization["code_challenge"], Pkce.ToBase64UrlChallenge(exchange["code_verifier"]));
        Assert.DoesNotContain(GoogleOAuthProvider.PlaylistScope, authorization["scope"]);
    }

    [Fact]
    public async Task Connect_asks_for_optional_scopes_only_on_request()
    {
        var harness = new Harness();
        await harness.Service.SaveCredentialsAsync(Platform.YouTube, new AppCredentials("id", null));

        await harness.Service.ConnectAsync(Platform.YouTube, harness.ApproveInBrowserAsync, includeOptionalScopes: true);

        var scope = QueryParser.Parse(harness.LastAuthorizationUri!.Query)["scope"];
        Assert.Contains(GoogleOAuthProvider.PlaylistScope, scope);
        Assert.Contains(GoogleOAuthProvider.UploadScope, scope);
    }

    [Fact]
    public async Task Each_connect_uses_a_new_verifier_and_state()
    {
        var harness = new Harness();
        await harness.ConnectTikTokAsync();
        var first = QueryParser.Parse(harness.LastAuthorizationUri!.Query);

        await harness.Service.ConnectAsync(Platform.TikTok, harness.ApproveInBrowserAsync);
        var second = QueryParser.Parse(harness.LastAuthorizationUri!.Query);

        Assert.NotEqual(first["state"], second["state"]);
        Assert.NotEqual(first["code_challenge"], second["code_challenge"]);
    }

    [Fact]
    public async Task Connect_keeps_refresh_tokens_but_never_access_tokens_in_the_store()
    {
        var harness = new Harness();

        await harness.ConnectTikTokAsync();

        var everything = string.Join('\n', harness.Store.Values.Values);
        Assert.Contains("rft.first", everything);
        Assert.DoesNotContain("act.first", everything);
    }

    [Fact]
    public async Task Connect_without_keys_says_so()
    {
        var harness = new Harness();

        await Assert.ThrowsAsync<AccountSetupException>(() => harness.Service.ConnectAsync(Platform.TikTok, harness.ApproveInBrowserAsync));
    }

    [Fact]
    public async Task Denying_access_in_the_browser_is_reported_and_saves_nothing()
    {
        var harness = new Harness();
        await harness.Service.SaveCredentialsAsync(Platform.TikTok, new AppCredentials("key", "secret"));

        var exception = await Assert.ThrowsAsync<AccountSetupException>(() => harness.Service.ConnectAsync(
            Platform.TikTok,
            async (uri, token) =>
            {
                var query = QueryParser.Parse(uri.Query);
                using var response = await Browser.GetAsync($"{query["redirect_uri"]}?error=access_denied&state={query["state"]}", token);
            }));

        Assert.Contains("canceled or denied", exception.Message);
        Assert.Null(harness.StoredTikTok());
        Assert.Empty(harness.TokenRequests);
    }

    [Fact]
    public async Task Sign_in_times_out_when_the_user_never_finishes()
    {
        var harness = new Harness();
        await harness.Service.SaveCredentialsAsync(Platform.TikTok, new AppCredentials("key", "secret"));

        var exception = await Assert.ThrowsAsync<AccountSetupException>(() => harness.Service.ConnectAsync(
            Platform.TikTok,
            (_, _) => Task.CompletedTask,
            timeout: TimeSpan.FromMilliseconds(50)));

        Assert.Contains("timed out", exception.Message);
    }

    [Fact]
    public async Task Sign_in_can_be_canceled()
    {
        var harness = new Harness();
        await harness.Service.SaveCredentialsAsync(Platform.TikTok, new AppCredentials("key", "secret"));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Service.ConnectAsync(
            Platform.TikTok,
            (_, _) => Task.CompletedTask,
            cancellationToken: cancel.Token));
    }

    [Fact]
    public async Task Connect_fails_when_the_user_withheld_a_required_permission()
    {
        var harness = new Harness
        {
            Override = request => request.Uri.AbsoluteUri.Contains("oauth/token")
                ? FakeHttpHandler.Json("""{"access_token":"a","expires_in":86400,"refresh_token":"r","scope":"user.info.basic"}""")
                : null!,
        };
        await harness.Service.SaveCredentialsAsync(Platform.TikTok, new AppCredentials("key", "secret"));

        var exception = await Assert.ThrowsAsync<AccountSetupException>(() => harness.Service.ConnectAsync(Platform.TikTok, harness.ApproveInBrowserAsync));

        Assert.Contains("video.upload", exception.Message);
        Assert.Null(harness.StoredTikTok());
    }

    [Fact]
    public async Task Connect_fails_without_a_refresh_token()
    {
        var harness = new Harness
        {
            Override = request => request.Uri.AbsoluteUri.Contains("oauth/token")
                ? FakeHttpHandler.Json("""{"access_token":"a","expires_in":86400}""")
                : null!,
        };
        await harness.Service.SaveCredentialsAsync(Platform.TikTok, new AppCredentials("key", "secret"));

        await Assert.ThrowsAsync<AccountSetupException>(() => harness.Service.ConnectAsync(Platform.TikTok, harness.ApproveInBrowserAsync));

        Assert.Null(harness.StoredTikTok());
    }

    [Fact]
    public async Task Connect_succeeds_when_the_display_name_cannot_be_fetched()
    {
        var harness = new Harness
        {
            Override = request => request.Uri.AbsoluteUri.Contains("user/info") ? throw new HttpRequestException("down") : null!,
        };

        await harness.ConnectTikTokAsync();

        var status = harness.Service.GetStatus(Platform.TikTok);
        Assert.Equal(ConnectionState.Connected, status.State);
        Assert.Null(status.DisplayName);
        Assert.Equal("open-1", harness.StoredTikTok()!.AccountId);
    }

    [Fact]
    public async Task Connect_fails_for_a_Google_account_without_a_channel()
    {
        var harness = new Harness
        {
            Override = request => request.Uri.AbsoluteUri.Contains("youtube/v3/channels") ? FakeHttpHandler.Json("""{"items":[]}""") : null!,
        };
        await harness.Service.SaveCredentialsAsync(Platform.YouTube, new AppCredentials("id", null));

        await Assert.ThrowsAsync<AccountSetupException>(() => harness.Service.ConnectAsync(Platform.YouTube, harness.ApproveInBrowserAsync));

        Assert.Equal(ConnectionState.NotConnected, harness.Service.GetStatus(Platform.YouTube).State);
    }

    [Fact]
    public async Task TikTok_keys_need_a_secret_and_Google_keys_do_not()
    {
        var harness = new Harness();

        await Assert.ThrowsAsync<AccountSetupException>(() => harness.Service.SaveCredentialsAsync(Platform.TikTok, new AppCredentials("key", " ")));
        await Assert.ThrowsAsync<AccountSetupException>(() => harness.Service.SaveCredentialsAsync(Platform.YouTube, new AppCredentials(" ", "s")));
        await harness.Service.SaveCredentialsAsync(Platform.YouTube, new AppCredentials("id", null));

        Assert.False(harness.Service.GetStatus(Platform.TikTok).HasCredentials);
        Assert.True(harness.Service.GetStatus(Platform.YouTube).HasCredentials);
    }

    [Fact]
    public async Task Pasted_keys_are_trimmed()
    {
        var harness = new Harness();

        await harness.Service.SaveCredentialsAsync(Platform.TikTok, new AppCredentials("  key\n", " secret "));

        Assert.Equal(new AppCredentials("key", "secret"), harness.Service.GetCredentials(Platform.TikTok));
    }

    [Fact]
    public async Task Changing_the_client_removes_its_sign_ins()
    {
        var harness = new Harness();
        await harness.ConnectTikTokAsync();

        await harness.Service.SaveCredentialsAsync(Platform.TikTok, new AppCredentials("other-key", "secret"));

        Assert.Equal(ConnectionState.NotConnected, harness.Service.GetStatus(Platform.TikTok).State);
        await Assert.ThrowsAsync<ReconnectRequiredException>(() => harness.Service.GetAccessTokenAsync(Platform.TikTok));
    }

    [Fact]
    public async Task Changing_only_the_secret_keeps_the_sign_in()
    {
        var harness = new Harness();
        await harness.ConnectTikTokAsync();

        await harness.Service.SaveCredentialsAsync(Platform.TikTok, new AppCredentials("key", "new-secret"));

        Assert.Equal(ConnectionState.Connected, harness.Service.GetStatus(Platform.TikTok).State);
    }

    [Fact]
    public async Task Uses_the_token_from_sign_in_without_refreshing()
    {
        var harness = new Harness();
        await harness.ConnectTikTokAsync();

        Assert.Equal("act.first", await harness.Service.GetAccessTokenAsync(Platform.TikTok));
        Assert.Single(harness.TokenRequests);
    }

    [Fact]
    public async Task Refreshes_shortly_before_the_token_expires_and_saves_the_new_refresh_token()
    {
        var harness = new Harness();
        await harness.ConnectTikTokAsync();

        harness.Time.Now += TimeSpan.FromHours(24) - TimeSpan.FromMinutes(4);

        Assert.Equal("act.second", await harness.Service.GetAccessTokenAsync(Platform.TikTok));
        Assert.Equal("rft.first", harness.TokenRequests.Last().Form["refresh_token"]);
        Assert.Equal("rft.second", harness.StoredTikTok()!.RefreshToken);
    }

    [Fact]
    public async Task Keeps_the_old_refresh_token_when_the_reply_has_none()
    {
        var harness = new Harness();
        await harness.Service.SaveCredentialsAsync(Platform.YouTube, new AppCredentials("id", null));
        await harness.Service.ConnectAsync(Platform.YouTube, harness.ApproveInBrowserAsync);
        harness.Override = request => request.Uri.AbsoluteUri.Contains("oauth2.googleapis.com/token")
            ? FakeHttpHandler.Json("""{"access_token":"ya29.next","expires_in":3600}""")
            : null!;
        harness.Time.Now += TimeSpan.FromHours(2);

        Assert.Equal("ya29.next", await harness.Service.GetAccessTokenAsync(Platform.YouTube));

        var stored = JsonSerializer.Deserialize<StoredAccount>(harness.Store.Get("youtube/account")!, StoredJson.Options)!;
        Assert.Equal("goog-refresh", stored.RefreshToken);
        Assert.NotEmpty(stored.Scopes);
    }

    [Fact]
    public async Task A_new_session_refreshes_from_the_store()
    {
        var harness = new Harness();
        await harness.ConnectTikTokAsync();

        var client = new HttpClient(harness.Http);
        var restarted = new AccountService(harness.Store, [new TikTokOAuthProvider(client), new GoogleOAuthProvider(client)], harness.Time);

        Assert.Equal("act.second", await restarted.GetAccessTokenAsync(Platform.TikTok));
        Assert.Equal("Creator", restarted.GetStatus(Platform.TikTok).DisplayName);
    }

    [Fact]
    public async Task Concurrent_requests_share_one_refresh()
    {
        var harness = new Harness();
        await harness.ConnectTikTokAsync();
        harness.Time.Now += TimeSpan.FromDays(2);
        var before = harness.TokenRequests.Count();

        var tokens = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => harness.Service.GetAccessTokenAsync(Platform.TikTok)));

        Assert.All(tokens, token => Assert.Equal("act.second", token));
        Assert.Equal(before + 1, harness.TokenRequests.Count());
    }

    [Fact]
    public async Task A_revoked_sign_in_is_remembered_and_stops_further_requests()
    {
        var harness = new Harness();
        await harness.ConnectTikTokAsync();
        harness.Time.Now += TimeSpan.FromDays(2);
        harness.Override = request => request.Uri.AbsoluteUri.Contains("oauth/token")
            ? FakeHttpHandler.Json("""{"error":"invalid_grant","error_description":"revoked"}""")
            : null!;

        await Assert.ThrowsAsync<ReconnectRequiredException>(() => harness.Service.GetAccessTokenAsync(Platform.TikTok));
        var requests = harness.TokenRequests.Count();

        await Assert.ThrowsAsync<ReconnectRequiredException>(() => harness.Service.GetAccessTokenAsync(Platform.TikTok));

        Assert.Equal(requests, harness.TokenRequests.Count());
        Assert.Equal(ConnectionState.NeedsReconnect, harness.Service.GetStatus(Platform.TikTok).State);
    }

    [Fact]
    public async Task An_expired_refresh_token_needs_reconnecting_without_a_request()
    {
        var harness = new Harness();
        await harness.ConnectTikTokAsync();
        var requests = harness.TokenRequests.Count();

        harness.Time.Now += TimeSpan.FromDays(366);

        Assert.Equal(ConnectionState.NeedsReconnect, harness.Service.GetStatus(Platform.TikTok).State);
        await Assert.ThrowsAsync<ReconnectRequiredException>(() => harness.Service.GetAccessTokenAsync(Platform.TikTok));
        Assert.Equal(requests, harness.TokenRequests.Count());
    }

    [Fact]
    public async Task Reconnecting_clears_the_reconnect_state()
    {
        var harness = new Harness();
        await harness.ConnectTikTokAsync();
        harness.Time.Now += TimeSpan.FromDays(366);

        await harness.Service.ConnectAsync(Platform.TikTok, harness.ApproveInBrowserAsync);

        Assert.Equal(ConnectionState.Connected, harness.Service.GetStatus(Platform.TikTok).State);
    }

    [Fact]
    public async Task A_temporary_refresh_failure_keeps_the_sign_in()
    {
        var harness = new Harness();
        await harness.ConnectTikTokAsync();
        harness.Time.Now += TimeSpan.FromDays(2);
        harness.Override = request => request.Uri.AbsoluteUri.Contains("oauth/token")
            ? FakeHttpHandler.Json("", HttpStatusCode.ServiceUnavailable)
            : null!;

        await Assert.ThrowsAsync<TransientAccountException>(() => harness.Service.GetAccessTokenAsync(Platform.TikTok));

        Assert.Equal(ConnectionState.Connected, harness.Service.GetStatus(Platform.TikTok).State);

        harness.Override = null;
        Assert.Equal("act.second", await harness.Service.GetAccessTokenAsync(Platform.TikTok));
    }

    [Fact]
    public async Task A_rejected_token_is_replaced_on_the_next_request()
    {
        var harness = new Harness();
        await harness.ConnectTikTokAsync();

        await harness.Service.ReportRejectedAsync(Platform.TikTok, "act.first");

        Assert.Equal("act.second", await harness.Service.GetAccessTokenAsync(Platform.TikTok));
    }

    [Fact]
    public async Task A_late_rejection_of_an_old_token_keeps_the_new_one()
    {
        var harness = new Harness();
        await harness.ConnectTikTokAsync();
        await harness.Service.ReportRejectedAsync(Platform.TikTok, "act.first");
        await harness.Service.GetAccessTokenAsync(Platform.TikTok);
        var requests = harness.TokenRequests.Count();

        await harness.Service.ReportRejectedAsync(Platform.TikTok, "act.first");

        Assert.Equal("act.second", await harness.Service.GetAccessTokenAsync(Platform.TikTok));
        Assert.Equal(requests, harness.TokenRequests.Count());
    }

    [Fact]
    public async Task Getting_a_token_when_never_connected_asks_to_connect()
    {
        var harness = new Harness();

        var exception = await Assert.ThrowsAsync<ReconnectRequiredException>(() => harness.Service.GetAccessTokenAsync(Platform.YouTube));

        Assert.Equal(Platform.YouTube, exception.Platform);
    }

    [Fact]
    public async Task TikTok_disconnect_revokes_with_the_access_token_and_removes_the_sign_in()
    {
        var harness = new Harness();
        await harness.ConnectTikTokAsync();

        Assert.True(await harness.Service.DisconnectAsync(Platform.TikTok));

        var revoke = harness.Http.Requests.Single(request => request.Uri.AbsoluteUri.Contains("oauth/revoke"));
        Assert.Equal("act.first", revoke.Form["token"]);
        Assert.Null(harness.StoredTikTok());
        Assert.True(harness.Service.GetStatus(Platform.TikTok).HasCredentials);
        await Assert.ThrowsAsync<ReconnectRequiredException>(() => harness.Service.GetAccessTokenAsync(Platform.TikTok));
    }

    [Fact]
    public async Task Google_disconnect_revokes_with_the_refresh_token()
    {
        var harness = new Harness();
        await harness.Service.SaveCredentialsAsync(Platform.YouTube, new AppCredentials("id", null));
        await harness.Service.ConnectAsync(Platform.YouTube, harness.ApproveInBrowserAsync);

        await harness.Service.DisconnectAsync(Platform.YouTube);

        var revoke = harness.Http.Requests.Single(request => request.Uri.AbsoluteUri.Contains("/revoke"));
        Assert.Equal("goog-refresh", revoke.Form["token"]);
    }

    [Fact]
    public async Task Disconnect_removes_the_sign_in_even_when_revoking_fails()
    {
        var harness = new Harness();
        await harness.ConnectTikTokAsync();
        harness.Override = request => request.Uri.AbsoluteUri.Contains("oauth/revoke")
            ? FakeHttpHandler.Json("", HttpStatusCode.ServiceUnavailable)
            : null!;

        Assert.False(await harness.Service.DisconnectAsync(Platform.TikTok));

        Assert.Null(harness.StoredTikTok());
    }

    [Fact]
    public async Task Disconnect_with_nothing_connected_succeeds()
    {
        Assert.True(await new Harness().Service.DisconnectAsync(Platform.TikTok));
    }

    [Fact]
    public void A_damaged_store_entry_reads_as_missing()
    {
        var harness = new Harness();
        harness.Store.Set("tiktok/account", "{not json");
        harness.Store.Set("tiktok/app", "{not json");

        var status = harness.Service.GetStatus(Platform.TikTok);

        Assert.Equal(ConnectionState.NotConnected, status.State);
        Assert.False(status.HasCredentials);
    }

    [Fact]
    public void Printing_these_types_never_shows_a_secret()
    {
        var credentials = new AppCredentials("id", "the-secret").ToString();
        var grant = new TokenGrant { AccessToken = "the-access", RefreshToken = "the-refresh" }.ToString();
        var account = new StoredAccount { RefreshToken = "the-refresh", DisplayName = "Name" }.ToString();

        Assert.DoesNotContain("the-secret", credentials);
        Assert.DoesNotContain("the-access", grant);
        Assert.DoesNotContain("the-refresh", grant);
        Assert.DoesNotContain("the-refresh", account);
    }
}
