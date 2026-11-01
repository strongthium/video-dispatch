using System.Text.Json;
using VideoDispatch.Core.Model;

namespace VideoDispatch.Core.Reencoding;

/// <param name="FfmpegPath">The <c>ffmpeg.exe</c> the user chose. Null means Windows' own tools do the work.</param>
public sealed record AppSettings(string? FfmpegPath = null);

/// <summary>The app's settings, with no secrets in them. A file that can't be read just means the defaults.</summary>
/// <param name="path">Where to keep them. Null keeps them in memory, for the sample.</param>
public sealed class AppSettingsStore(string? path)
{
    private readonly object _lock = new();
    private AppSettings? _current;

    public AppSettings Current
    {
        get
        {
            lock (_lock)
            {
                return _current ??= Read();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_lock)
        {
            if (path is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporary = path + ".tmp";
                File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(settings, StoredJson.Options));
                File.Move(temporary, path, overwrite: true);
            }

            _current = settings;
        }
    }

    private AppSettings Read()
    {
        if (path is null)
        {
            return new AppSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllBytes(path), StoredJson.Options) ?? new AppSettings();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }
}
