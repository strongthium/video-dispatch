using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Web;
using VideoDispatch.Core.Accounts;
using VideoDispatch.Core.Accounts.Secrets;

namespace VideoDispatch.App;

/// <summary>
/// The account screen on made-up data. The real sign-in code runs, including the local redirect, but the platforms
/// are answered from here. Nothing leaves this computer, nothing is saved, and no browser opens.
/// </summary>
public static class SampleAccounts
{
    public static (AccountService Accounts, Func<Uri, CancellationToken, Task> OpenBrowser) Create()
    {
        var platforms = new SamplePlatforms();
        var http = new HttpClient(platforms);
        var accounts = new AccountService(
            new MemorySecretStore(),
            [new TikTokOAuthProvider(http), new GoogleOAuthProvider(http)],
            TimeProvider.System);

        return (accounts, platforms.ApproveAsync);
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly ConcurrentDictionary<string, string> _values = new();

        public string? Get(string key) => _values.GetValueOrDefault(key);

        public void Set(string key, string value) => _values[key] = value;

        public void Delete(string key) => _values.TryRemove(key, out _);
    }

    private sealed class SamplePlatforms : HttpMessageHandler
    {
        private static readonly HttpClient Redirects = new();
        private string _approvedScopes = "";

        /// <summary>Does what the user's browser would: signs in at once and follows the redirect back.</summary>
        public async Task ApproveAsync(Uri authorizationUri, CancellationToken cancellationToken)
        {
            var query = HttpUtility.ParseQueryString(authorizationUri.Query);
            _approvedScopes = query["scope"] ?? "";

            var callback = $"{query["redirect_uri"]}?code=sample-code&state={Uri.EscapeDataString(query["state"] ?? "")}";
            using var response = await Redirects.GetAsync(callback, cancellationToken);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Json(request.RequestUri?.AbsoluteUri switch
            {
                var url when url?.StartsWith("https://open.tiktokapis.com/v2/oauth/token/") == true =>
                    $$"""{"access_token":"sample","expires_in":86400,"refresh_token":"sample","refresh_expires_in":31536000,"scope":"{{_approvedScopes}}"}""",
                var url when url?.StartsWith("https://open.tiktokapis.com/v2/user/info/") == true =>
                    """{"data":{"user":{"open_id":"sample","display_name":"Sample Creator"}}}""",
                var url when url?.StartsWith("https://oauth2.googleapis.com/token") == true =>
                    $$"""{"access_token":"sample","expires_in":3600,"refresh_token":"sample","scope":"{{_approvedScopes}}"}""",
                var url when url?.StartsWith("https://www.googleapis.com/youtube/v3/channels") == true =>
                    """{"items":[{"id":"sample","snippet":{"title":"Sample Channel"}}]}""",
                var url when url?.Contains("revoke") == true => "",
                var url => throw new InvalidOperationException($"The sample doesn't answer {url}."),
            }));

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
