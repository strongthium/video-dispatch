using System.ComponentModel;
using System.Diagnostics;
using VideoDispatch.Core.Accounts;
using VideoDispatch.Core.Model;

namespace VideoDispatch.App;

/// <summary>Opens web pages in the user's default browser.</summary>
public static class Browser
{
    /// <summary>Only https links open. Handing Windows anything else could start another program.</summary>
    public static bool CanOpen(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps;

    public static bool TryOpen(Uri uri)
    {
        if (!CanOpen(uri))
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    public static Task OpenSignInAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!TryOpen(uri))
        {
            throw new BrowserException();
        }

        return Task.CompletedTask;
    }
}

/// <summary>Windows couldn't open the sign-in page.</summary>
public sealed class BrowserException() : Exception("Windows couldn't open your browser for the sign-in.");
