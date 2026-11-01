using VideoDispatch.Core.Reencoding;

namespace VideoDispatch.Core.Tests.Reencoding;

public sealed class AppSettingsStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(AppContext.BaseDirectory, "settings-" + Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_folder, "deeper", "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Starts_with_no_FFmpeg() => Assert.Null(new AppSettingsStore(SettingsPath).Current.FfmpegPath);

    [Fact]
    public void Saves_for_the_next_start()
    {
        new AppSettingsStore(SettingsPath).Save(new AppSettings(@"C:\Tools\ffmpeg.exe"));

        Assert.Equal(@"C:\Tools\ffmpeg.exe", new AppSettingsStore(SettingsPath).Current.FfmpegPath);
    }

    [Fact]
    public void A_damaged_file_just_means_the_defaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, "{ not json");

        Assert.Null(new AppSettingsStore(SettingsPath).Current.FfmpegPath);
    }

    [Fact]
    public void With_no_path_it_stays_in_memory_for_the_sample()
    {
        var store = new AppSettingsStore(null);

        store.Save(new AppSettings("sample"));

        Assert.Equal("sample", store.Current.FfmpegPath);
    }
}
