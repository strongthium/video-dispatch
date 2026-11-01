using System.ComponentModel;
using System.Globalization;
using VideoDispatch.Core.Reencoding;

namespace VideoDispatch.App;

/// <summary>What the Settings screen shows and does. Nothing needs FFmpeg, so leaving this alone is always fine.</summary>
public sealed class SettingsPanel : INotifyPropertyChanged
{
    private const double Mega = 1024 * 1024;

    private readonly AppSettingsStore _settings;
    private readonly FfmpegManager _ffmpeg;
    private readonly Func<string, CancellationToken, Task<FfmpegInfo>> _inspect;
    private string _ffmpegPath = "";
    private CancellationTokenSource? _download;

    /// <param name="settings">Keeps the choice.</param>
    /// <param name="ffmpeg">Looks after a downloaded copy.</param>
    /// <param name="inspect">Asks a program what it is.</param>
    /// <param name="post">Runs something on the UI thread.</param>
    public SettingsPanel(
        AppSettingsStore settings,
        FfmpegManager ffmpeg,
        Func<string, CancellationToken, Task<FfmpegInfo>> inspect,
        Action<Action> post)
    {
        _settings = settings;
        _ffmpeg = ffmpeg;
        _inspect = inspect;
        ffmpeg.Changed += () => post(Refresh);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>A copy of the user's own. A downloaded copy doesn't show here, since there's nothing to choose about it.</summary>
    public string FfmpegPath
    {
        get => _ffmpegPath;
        set
        {
            _ffmpegPath = value;
            Refresh();
        }
    }

    public bool IsUsingFfmpeg { get; private set; }

    public bool UsesDownloadedCopy => _ffmpeg.UsesDownloadedCopy;

    public string EngineText => (IsUsingFfmpeg, UsesDownloadedCopy) switch
    {
        (true, true) => $"Re-encoding uses FFmpeg {_ffmpeg.InstalledVersion}, downloaded by this app.",
        (true, false) => "Re-encoding uses your own copy of FFmpeg, from the program below.",
        _ => "Re-encoding uses Windows' own tools. There's nothing to install.",
    };

    public bool IsBusy { get; private set; }

    public bool IsDownloading { get; private set; }

    public double DownloadPercent { get; private set; }

    public bool DownloadProgressIsUnknown { get; private set; }

    public string? DownloadText { get; private set; }

    public bool CanDownload => !IsBusy && !UsesDownloadedCopy;

    public string? UpdateText => _ffmpeg.AvailableUpdate is { } latest ? $"FFmpeg {latest} is available. You have {_ffmpeg.InstalledVersion}." : null;

    public bool HasUpdate => UpdateText is not null;

    public bool CanUpdate => !IsBusy && HasUpdate;

    public bool CanUse => !IsBusy && _ffmpegPath.Trim().Length > 0;

    public bool CanStopUsing => !IsBusy && IsUsingFfmpeg;

    public string? Message { get; private set; }

    public bool MessageIsError { get; private set; }

    public bool HasMessage => Message is not null;

    public async Task LoadAsync()
    {
        var current = await Task.Run(() => _settings.Current);
        _ffmpegPath = current.FfmpegIsDownloaded ? "" : current.FfmpegPath ?? "";
        IsUsingFfmpeg = current.FfmpegPath is not null;
        Refresh();
    }

    /// <summary>Fetches FFmpeg, or a newer one, unpacks it, checks it, and starts using it.</summary>
    public async Task DownloadAsync()
    {
        using var download = new CancellationTokenSource();
        _download = download;
        IsBusy = true;
        IsDownloading = true;
        DownloadPercent = 0;
        DownloadProgressIsUnknown = true;
        DownloadText = "Asking for the newest version…";
        Message = null;
        MessageIsError = false;
        Refresh();

        try
        {
            var progress = new Progress<FfmpegDownloadProgress>(ShowProgress);
            var version = await Task.Run(() => _ffmpeg.InstallLatestAsync(progress, download.Token));
            _ffmpegPath = "";
            IsUsingFfmpeg = true;
            Message = $"Using FFmpeg {version}.";
        }
        catch (OperationCanceledException)
        {
            Message = "Download cancelled.";
        }
        catch (Exception exception) when (exception is EncodeException or IOException or UnauthorizedAccessException)
        {
            Fail(exception.Message);
        }
        finally
        {
            _download = null;
            IsBusy = false;
            IsDownloading = false;
            DownloadText = null;
            Refresh();
        }
    }

    public void CancelDownload() => _download?.Cancel();

    /// <summary>Checks that the program is FFmpeg and can do the work, and only then uses it.</summary>
    public async Task UseAsync()
    {
        var path = _ffmpegPath.Trim().Trim('"');
        IsBusy = true;
        Message = null;
        MessageIsError = false;
        Refresh();

        try
        {
            var info = await Task.Run(() => _inspect(path, CancellationToken.None));
            if (info.HasH264)
            {
                await Task.Run(() => _ffmpeg.UseOwnCopy(path));
                _ffmpegPath = path;
                IsUsingFfmpeg = true;
                Message = $"Using {info.Version}.";
            }
            else
            {
                Fail("This FFmpeg has no H.264 encoder (libx264), so it can't make the MP4s this app sends. Choose a build that has one.");
            }
        }
        catch (Exception exception) when (exception is EncodeException or IOException or UnauthorizedAccessException)
        {
            Fail(exception.Message);
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    /// <summary>Back to Windows' tools. A downloaded copy is removed, and can be fetched again.</summary>
    public async Task StopUsingAsync()
    {
        Message = null;
        MessageIsError = false;

        try
        {
            await Task.Run(_ffmpeg.StopUsingFfmpeg);
            _ffmpegPath = "";
            IsUsingFfmpeg = false;
            Message = "Back to Windows' own tools.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Fail(exception.Message);
        }

        Refresh();
    }

    private static string Mb(long bytes) => string.Format(CultureInfo.CurrentCulture, "{0:N0} MB", bytes / Mega);

    private void ShowProgress(FfmpegDownloadProgress progress)
    {
        DownloadProgressIsUnknown = progress.IsUnpacking || progress.Total is not > 0;
        DownloadPercent = progress.Total is { } total and > 0 ? progress.Bytes * 100.0 / total : 0;
        DownloadText = progress switch
        {
            { IsUnpacking: true } => "Unpacking and checking…",
            { Total: > 0 } => $"Downloading: {Mb(progress.Bytes)} of {Mb(progress.Total.Value)}",
            _ => $"Downloading: {Mb(progress.Bytes)}",
        };
        Refresh();
    }

    private void Fail(string message)
    {
        Message = message;
        MessageIsError = true;
    }

    private void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
}
