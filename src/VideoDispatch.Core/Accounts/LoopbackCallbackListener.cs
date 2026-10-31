using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace VideoDispatch.Core.Accounts;

public sealed record AuthorizationCallback(string? Code, string? Error, string? ErrorDescription);

/// <summary>
/// Receives the browser's redirect after sign-in on a random loopback port. It uses a plain socket
/// because HttpListener can need admin rights on Windows, and it reads only the request line.
/// </summary>
public sealed class LoopbackCallbackListener : IAsyncDisposable
{
    private const int MaxRequestBytes = 16 * 1024;
    private const int MaxErrorDescriptionLength = 300;
    private static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(10);

    private const string SuccessPage =
        "<!doctype html><meta charset=\"utf-8\"><title>video-dispatch</title>" +
        "<p>Sign-in received. You can close this tab and return to video-dispatch.</p>";

    private const string FailurePage =
        "<!doctype html><meta charset=\"utf-8\"><title>video-dispatch</title>" +
        "<p>Sign-in was not completed. Return to video-dispatch for details.</p>";

    private readonly TcpListener _listener;
    private readonly string _path;
    private readonly byte[] _expectedState;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<AuthorizationCallback> _result =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private LoopbackCallbackListener(string path, string expectedState)
    {
        _path = path;
        _expectedState = Encoding.UTF8.GetBytes(expectedState);
        _listener = new TcpListener(IPAddress.Loopback, 0);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static LoopbackCallbackListener Start(string path, string expectedState)
    {
        var listener = new LoopbackCallbackListener(path, expectedState);
        listener._listener.Start();
        _ = listener.AcceptLoopAsync();
        return listener;
    }

    public async Task<AuthorizationCallback> WaitAsync(CancellationToken cancellationToken) =>
        await _result.Task.WaitAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);

                // One at a time would stall here, because browsers open spare connections that send nothing.
                _ = HandleConnectionAsync(client);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // The listener was stopped.
        }
    }

    private async Task HandleConnectionAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(RequestReadTimeout);

                var stream = client.GetStream();
                var requestLine = await ReadRequestLineAsync(stream, timeout.Token);
                var callback = requestLine is null ? null : ParseCallback(requestLine);

                if (callback is null)
                {
                    await WriteResponseAsync(stream, "404 Not Found", FailurePage, timeout.Token);
                    return;
                }

                var page = callback.Code is not null ? SuccessPage : FailurePage;
                await WriteResponseAsync(stream, "200 OK", page, timeout.Token);
                _result.TrySetResult(callback);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
                // A connection that stalls or drops doesn't affect the sign-in.
            }
        }
    }

    private static async Task<string?> ReadRequestLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxRequestBytes];
        var length = 0;

        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0)
            {
                return null;
            }

            length += read;

            // Wait for the whole request, so the browser isn't answered while it is still sending.
            if (buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) >= 0)
            {
                var lineEnd = buffer.AsSpan(0, length).IndexOf("\r\n"u8);
                return Encoding.ASCII.GetString(buffer, 0, lineEnd);
            }
        }

        return null;
    }

    /// <summary>Returns null for any request that isn't a genuine callback, so sign-in keeps waiting.</summary>
    private AuthorizationCallback? ParseCallback(string requestLine)
    {
        var parts = requestLine.Split(' ');
        if (parts is not ["GET", var target, _])
        {
            return null;
        }

        var queryStart = target.IndexOf('?');
        var path = queryStart < 0 ? target : target[..queryStart];
        if (path != _path || queryStart < 0)
        {
            return null;
        }

        var query = ParseQuery(target[(queryStart + 1)..]);
        query.TryGetValue("state", out var state);
        var stateMatches = state is not null &&
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), _expectedState);

        if (query.TryGetValue("error", out var error) && error.Length > 0)
        {
            // TikTok's docs list state only on success, so an error may arrive without one. The worst
            // a forged error can do is end this sign-in, so only a wrong state is ignored.
            if (state is not null && !stateMatches)
            {
                return null;
            }

            query.TryGetValue("error_description", out var description);
            return new AuthorizationCallback(null, error, Shorten(description));
        }

        return stateMatches && query.TryGetValue("code", out var code) && code.Length > 0
            ? new AuthorizationCallback(code, null, null)
            : null;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var name = separator < 0 ? pair : pair[..separator];
            var value = separator < 0 ? "" : pair[(separator + 1)..];
            values[Decode(name)] = Decode(value);
        }

        return values;
    }

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));

    private static string? Shorten(string? text) =>
        text is { Length: > MaxErrorDescriptionLength } ? text[..MaxErrorDescriptionLength] : text;

    private static async Task WriteResponseAsync(NetworkStream stream, string status, string page, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(page);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Referrer-Policy: no-referrer\r\n" +
            "Connection: close\r\n\r\n");

        await stream.WriteAsync(head, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
