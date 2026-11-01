namespace VideoDispatch.Core.Reencoding;

/// <summary>
/// Looks after a copy of FFmpeg this app downloaded: getting it, noticing newer versions, and removing it. It asks about
/// new versions only while such a copy is in use, and never installs one until it's told to. A copy the user pointed at
/// is theirs, and is left alone.
/// </summary>
public sealed class FfmpegManager(AppSettingsStore settings, IFfmpegDownloads downloads, TimeProvider time)
{
    /// <summary>How often the site is asked, at most. It's one small request.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    // Often enough that a PC left on doesn't go long past a check, and a check that failed is tried again.
    private static readonly TimeSpan LookInterval = TimeSpan.FromHours(6);

    /// <summary>What's known has changed. This can come from any thread.</summary>
    public event Action? Changed;

    public bool UsesDownloadedCopy => settings.Current.FfmpegIsDownloaded;

    public string? InstalledVersion => settings.Current.FfmpegIsDownloaded ? settings.Current.FfmpegVersion : null;

    /// <summary>A newer version than the downloaded copy, once one has been seen.</summary>
    public string? AvailableUpdate
    {
        get
        {
            var current = settings.Current;
            return current.FfmpegIsDownloaded && IsNewer(current.FfmpegLatest, current.FfmpegVersion) ? current.FfmpegLatest : null;
        }
    }

    /// <summary>Gets the newest version and starts using it. The old one goes, if nothing is using it.</summary>
    public async Task<string> InstallLatestAsync(IProgress<FfmpegDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var version = await downloads.GetLatestVersionAsync(cancellationToken);
        var path = await downloads.InstallAsync(version, progress, cancellationToken);

        settings.Save(new AppSettings(path, FfmpegIsDownloaded: true, version, version, time.GetUtcNow()));
        downloads.RemoveAllExcept(path);
        Changed?.Invoke();
        return version;
    }

    /// <summary>Asks about a newer version, if a downloaded copy is in use and it's time to. Returns whether it asked.</summary>
    /// <exception cref="EncodeException">It couldn't ask.</exception>
    public async Task<bool> CheckForUpdateIfDueAsync(CancellationToken cancellationToken = default)
    {
        var current = settings.Current;
        if (!current.FfmpegIsDownloaded || (current.FfmpegCheckedAt is { } last && time.GetUtcNow() - last < CheckInterval))
        {
            return false;
        }

        var latest = await downloads.GetLatestVersionAsync(cancellationToken);

        // The choice may have changed while the question was out.
        if (settings.Current.FfmpegIsDownloaded)
        {
            settings.Save(settings.Current with { FfmpegLatest = latest, FfmpegCheckedAt = time.GetUtcNow() });
            Changed?.Invoke();
        }

        return true;
    }

    /// <summary>Keeps checking until stopped. A check that fails is quietly tried again later.</summary>
    public async Task RunChecksAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                try
                {
                    await CheckForUpdateIfDueAsync(cancellationToken);
                }
                catch (Exception exception) when (exception is EncodeException or IOException or UnauthorizedAccessException)
                {
                }

                await Task.Delay(LookInterval, time, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>The user chose their own copy. A downloaded one is no longer needed, and is removed.</summary>
    public void UseOwnCopy(string path)
    {
        settings.Save(new AppSettings(path));
        downloads.RemoveAllExcept(null);
        Changed?.Invoke();
    }

    /// <summary>Back to Windows' tools. A downloaded copy is removed.</summary>
    public void StopUsingFfmpeg()
    {
        settings.Save(new AppSettings());
        downloads.RemoveAllExcept(null);
        Changed?.Invoke();
    }

    /// <summary>The app has started. Anything a crash left behind can go.</summary>
    public void Tidy()
    {
        var current = settings.Current;
        downloads.RemoveAllExcept(current.FfmpegIsDownloaded ? current.FfmpegPath : null);
    }

    private static bool IsNewer(string? candidate, string? than) =>
        Version.TryParse(candidate, out var a) && (!Version.TryParse(than, out var b) || a > b);
}
