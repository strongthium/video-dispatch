using System.Net;
using System.Net.Sockets;
using System.Text;
using VideoDispatch.Core.Accounts;

namespace VideoDispatch.Core.Tests.Accounts;

public class LoopbackCallbackListenerTests
{
    private const string State = "expected-state";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private static LoopbackCallbackListener Start(string path = "/callback/") =>
        LoopbackCallbackListener.Start(path, State);

    private static async Task<string> SendAsync(int port, string requestHead)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(requestHead + "\r\nHost: 127.0.0.1\r\n\r\n"));

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync().WaitAsync(Limit);
    }

    private static Task<string> GetAsync(int port, string target) => SendAsync(port, $"GET {target} HTTP/1.1");

    private static async Task AssertStillWaitingAsync(LoopbackCallbackListener listener)
    {
        await Assert.ThrowsAsync<TimeoutException>(() => listener.WaitAsync(default).WaitAsync(TimeSpan.FromMilliseconds(100)));
    }

    private static async Task<AuthorizationCallback> WaitAsync(LoopbackCallbackListener listener)
    {
        using var limit = new CancellationTokenSource(Limit);
        return await listener.WaitAsync(limit.Token);
    }

    [Fact]
    public async Task Returns_the_code_when_the_state_matches()
    {
        await using var listener = Start();

        var response = await GetAsync(listener.Port, $"/callback/?code=abc123&scopes=video.upload&state={State}");
        var callback = await WaitAsync(listener);

        Assert.Equal("abc123", callback.Code);
        Assert.Null(callback.Error);
        Assert.StartsWith("HTTP/1.1 200 OK", response);
        Assert.Contains("close this tab", response);
    }

    [Fact]
    public async Task Decodes_the_code()
    {
        await using var listener = Start();

        await GetAsync(listener.Port, $"/callback/?code=a%2Ab%2Fc%3D&state={State}");

        Assert.Equal("a*b/c=", (await WaitAsync(listener)).Code);
    }

    [Fact]
    public async Task Works_on_the_root_path_for_Google()
    {
        await using var listener = Start("/");

        await GetAsync(listener.Port, $"/?state={State}&code=xyz");

        Assert.Equal("xyz", (await WaitAsync(listener)).Code);
    }

    [Theory]
    [InlineData("/callback/?code=forged&state=wrong")]
    [InlineData("/callback/?code=forged")]
    [InlineData("/callback/?state=" + State)]
    [InlineData("/callback/?code=&state=" + State)]
    [InlineData("/other/?code=abc&state=" + State)]
    [InlineData("/callback?code=abc&state=" + State)]
    [InlineData("/favicon.ico")]
    [InlineData("/callback/")]
    public async Task Ignores_other_requests_and_keeps_waiting(string target)
    {
        await using var listener = Start();

        var response = await GetAsync(listener.Port, target);

        Assert.StartsWith("HTTP/1.1 404", response);
        await AssertStillWaitingAsync(listener);

        await GetAsync(listener.Port, $"/callback/?code=real&state={State}");
        Assert.Equal("real", (await WaitAsync(listener)).Code);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("HEAD")]
    public async Task Ignores_other_methods(string method)
    {
        await using var listener = Start();

        await SendAsync(listener.Port, $"{method} /callback/?code=abc&state={State} HTTP/1.1");

        await AssertStillWaitingAsync(listener);
    }

    [Fact]
    public async Task Reports_an_error_with_a_matching_state()
    {
        await using var listener = Start();

        var response = await GetAsync(listener.Port, $"/callback/?error=access_denied&error_description=No+thanks&state={State}");
        var callback = await WaitAsync(listener);

        Assert.Null(callback.Code);
        Assert.Equal("access_denied", callback.Error);
        Assert.Equal("No thanks", callback.ErrorDescription);
        Assert.Contains("not completed", response);
    }

    [Fact]
    public async Task Reports_an_error_without_a_state_because_TikTok_may_omit_it()
    {
        await using var listener = Start();

        await GetAsync(listener.Port, "/callback/?error=access_denied");

        Assert.Equal("access_denied", (await WaitAsync(listener)).Error);
    }

    [Fact]
    public async Task Ignores_an_error_with_the_wrong_state()
    {
        await using var listener = Start();

        await GetAsync(listener.Port, "/callback/?error=access_denied&state=wrong");

        await AssertStillWaitingAsync(listener);
    }

    [Fact]
    public async Task Does_not_put_request_values_in_the_page()
    {
        await using var listener = Start();

        var response = await GetAsync(listener.Port, $"/callback/?error=%3Cscript%3Ealert(1)%3C%2Fscript%3E&error_description=%3Cb%3Ex&state={State}");

        Assert.DoesNotContain("alert", response);
        Assert.DoesNotContain("<b>", response);
    }

    [Fact]
    public async Task Shortens_a_long_error_description()
    {
        await using var listener = Start();

        await GetAsync(listener.Port, $"/callback/?error=x&error_description={new string('a', 2000)}&state={State}");

        Assert.Equal(300, (await WaitAsync(listener)).ErrorDescription!.Length);
    }

    [Fact]
    public async Task A_silent_connection_does_not_block_the_real_callback()
    {
        await using var listener = Start();

        // Browsers open spare connections like this one and send nothing on them.
        using var silent = new TcpClient();
        await silent.ConnectAsync(IPAddress.Loopback, listener.Port);

        await GetAsync(listener.Port, $"/callback/?code=abc&state={State}");

        Assert.Equal("abc", (await WaitAsync(listener)).Code);
    }

    [Fact]
    public async Task A_request_too_large_to_be_genuine_does_not_end_the_sign_in()
    {
        await using var listener = Start();

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, listener.Port);
            var stream = client.GetStream();
            await stream.WriteAsync(new byte[17 * 1024]);

            // Closing with unread data can reset the connection, so the reply may or may not arrive.
            try
            {
                using var reader = new StreamReader(stream);
                await reader.ReadToEndAsync().WaitAsync(Limit);
            }
            catch (IOException)
            {
            }
        }

        await AssertStillWaitingAsync(listener);

        await GetAsync(listener.Port, $"/callback/?code=real&state={State}");
        Assert.Equal("real", (await WaitAsync(listener)).Code);
    }

    [Fact]
    public async Task Waiting_can_be_canceled()
    {
        await using var listener = Start();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listener.WaitAsync(cancel.Token));
    }

    [Fact]
    public async Task Listens_only_on_loopback()
    {
        await using var listener = Start();

        var endpoint = await Task.Run(() =>
        {
            var properties = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
            return properties.GetActiveTcpListeners().Single(entry => entry.Port == listener.Port);
        });

        Assert.Equal(IPAddress.Loopback, endpoint.Address);
    }

    [Fact]
    public async Task Stops_listening_when_disposed()
    {
        var listener = Start();
        var port = listener.Port;

        await listener.DisposeAsync();

        using var client = new TcpClient();
        await Assert.ThrowsAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, port));
    }
}
