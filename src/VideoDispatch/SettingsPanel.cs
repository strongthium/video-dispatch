using System.ComponentModel;
using VideoDispatch.Core.Reencoding;

namespace VideoDispatch.App;

/// <summary>What the Settings screen shows and does. Nothing needs FFmpeg, so leaving this alone is always fine.</summary>
public sealed class SettingsPanel(AppSettingsStore settings, Func<string, CancellationToken, Task<FfmpegInfo>> inspect) : INotifyPropertyChanged
{
    private string _ffmpegPath = "";

    public event PropertyChangedEventHandler? PropertyChanged;

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

    public string EngineText => IsUsingFfmpeg
        ? "Re-encoding uses FFmpeg, from the program below."
        : "Re-encoding uses Windows' own tools. There's nothing to install.";

    public bool IsBusy { get; private set; }

    public bool CanUse => !IsBusy && _ffmpegPath.Trim().Length > 0;

    public bool CanStopUsing => !IsBusy && IsUsingFfmpeg;

    public string? Message { get; private set; }

    public bool MessageIsError { get; private set; }

    public bool HasMessage => Message is not null;

    public async Task LoadAsync()
    {
        var current = await Task.Run(() => settings.Current);
        _ffmpegPath = current.FfmpegPath ?? "";
        IsUsingFfmpeg = current.FfmpegPath is not null;
        Refresh();
    }

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
            var info = await Task.Run(() => inspect(path, CancellationToken.None));
            if (info.HasH264)
            {
                await Task.Run(() => settings.Save(new AppSettings(path)));
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

    public async Task StopUsingAsync()
    {
        Message = null;
        MessageIsError = false;

        try
        {
            await Task.Run(() => settings.Save(new AppSettings()));
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

    private void Fail(string message)
    {
        Message = message;
        MessageIsError = true;
    }

    private void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
}
