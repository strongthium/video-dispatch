using VideoDispatch.Core.Reencoding;
using VideoDispatch.Core.Tests.Queue;

namespace VideoDispatch.Core.Tests.Reencoding;

public sealed class FfmpegManagerTests
{
    private readonly AppSettingsStore _settings = new(null);
    private readonly FakeDownloads _downloads = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly FfmpegManager _manager;

    public FfmpegManagerTests() => _manager = new FfmpegManager(_settings, _downloads, _time);

    private async Task InstallAsync(string version = "9.0.2")
    {
        _downloads.Latest = version;
        await _manager.InstallLatestAsync();
    }

    [Fact]
    public async Task Installing_starts_using_it_and_clears_older_copies()
    {
        await InstallAsync();

        Assert.True(_manager.UsesDownloadedCopy);
        Assert.Equal("9.0.2", _manager.InstalledVersion);
        Assert.Equal(_downloads.PathOf("9.0.2"), _settings.Current.FfmpegPath);
        Assert.Equal(_downloads.PathOf("9.0.2"), _downloads.KeptLast);
        Assert.Null(_manager.AvailableUpdate);
    }

    [Fact]
    public async Task A_failed_install_changes_nothing()
    {
        _settings.Save(new AppSettings(@"C:\Mine\ffmpeg.exe"));
        _downloads.InstallFails = new EncodeException("It didn't work.");

        await Assert.ThrowsAsync<EncodeException>(() => InstallAsync());

        Assert.Equal(new AppSettings(@"C:\Mine\ffmpeg.exe"), _settings.Current);
    }

    [Fact]
    public async Task Without_a_downloaded_copy_nothing_is_ever_asked()
    {
        Assert.False(await _manager.CheckForUpdateIfDueAsync());

        _settings.Save(new AppSettings(@"C:\Mine\ffmpeg.exe"));
        _time.Advance(TimeSpan.FromDays(30));

        Assert.False(await _manager.CheckForUpdateIfDueAsync());
        Assert.Equal(0, _downloads.VersionRequests);
    }

    [Fact]
    public async Task Asks_again_only_after_a_day()
    {
        await InstallAsync();
        var afterInstall = _downloads.VersionRequests;

        _time.Advance(TimeSpan.FromHours(23));
        Assert.False(await _manager.CheckForUpdateIfDueAsync());
        Assert.Equal(afterInstall, _downloads.VersionRequests);

        _time.Advance(TimeSpan.FromHours(1));
        Assert.True(await _manager.CheckForUpdateIfDueAsync());
        Assert.Equal(afterInstall + 1, _downloads.VersionRequests);

        Assert.False(await _manager.CheckForUpdateIfDueAsync());
    }

    [Fact]
    public async Task A_newer_version_is_offered_and_not_installed_until_asked()
    {
        await InstallAsync();
        var changed = 0;
        _manager.Changed += () => changed++;
        _downloads.Latest = "9.1";
        _time.Advance(FfmpegManager.CheckInterval);

        await _manager.CheckForUpdateIfDueAsync();

        Assert.Equal("9.1", _manager.AvailableUpdate);
        Assert.Equal("9.0.2", _manager.InstalledVersion);
        Assert.Equal(1, _downloads.Installs.Count(version => version == "9.0.2"));
        Assert.DoesNotContain("9.1", _downloads.Installs);
        Assert.Equal(1, changed);
    }

    [Theory]
    [InlineData("9.0.2")]
    [InlineData("9.0.1")]
    [InlineData("not a version")]
    public async Task The_same_or_an_older_version_is_not_offered(string latest)
    {
        await InstallAsync();
        _downloads.Latest = latest;
        _time.Advance(FfmpegManager.CheckInterval);

        await _manager.CheckForUpdateIfDueAsync();

        Assert.Null(_manager.AvailableUpdate);
    }

    [Fact]
    public async Task Versions_compare_as_numbers_not_as_text()
    {
        await InstallAsync("9.9");
        _downloads.Latest = "9.10";
        _time.Advance(FfmpegManager.CheckInterval);

        await _manager.CheckForUpdateIfDueAsync();

        Assert.Equal("9.10", _manager.AvailableUpdate);
    }

    [Fact]
    public async Task Updating_moves_to_the_new_version_and_clears_the_offer()
    {
        await InstallAsync();
        _downloads.Latest = "9.1";
        _time.Advance(FfmpegManager.CheckInterval);
        await _manager.CheckForUpdateIfDueAsync();

        await _manager.InstallLatestAsync();

        Assert.Equal("9.1", _manager.InstalledVersion);
        Assert.Null(_manager.AvailableUpdate);
        Assert.Equal(_downloads.PathOf("9.1"), _settings.Current.FfmpegPath);
        Assert.Equal(_downloads.PathOf("9.1"), _downloads.KeptLast);
    }

    [Fact]
    public async Task A_check_while_the_copy_was_given_up_does_not_bring_it_back()
    {
        await InstallAsync();
        _time.Advance(FfmpegManager.CheckInterval);
        _downloads.DuringVersionRequest = _manager.StopUsingFfmpeg;

        await _manager.CheckForUpdateIfDueAsync();

        Assert.False(_manager.UsesDownloadedCopy);
        Assert.Null(_settings.Current.FfmpegPath);
    }

    [Fact]
    public async Task Choosing_your_own_copy_removes_the_downloaded_one_and_stops_the_checks()
    {
        await InstallAsync();

        _manager.UseOwnCopy(@"C:\Mine\ffmpeg.exe");

        Assert.False(_manager.UsesDownloadedCopy);
        Assert.Null(_manager.InstalledVersion);
        Assert.Equal(@"C:\Mine\ffmpeg.exe", _settings.Current.FfmpegPath);
        Assert.True(_downloads.RemovedAll);

        _time.Advance(TimeSpan.FromDays(30));
        Assert.False(await _manager.CheckForUpdateIfDueAsync());
    }

    [Fact]
    public async Task Going_back_to_Windows_tools_removes_the_downloaded_one()
    {
        await InstallAsync();

        _manager.StopUsingFfmpeg();

        Assert.Null(_settings.Current.FfmpegPath);
        Assert.False(_manager.UsesDownloadedCopy);
        Assert.True(_downloads.RemovedAll);
    }

    [Fact]
    public async Task Tidying_keeps_the_copy_in_use()
    {
        await InstallAsync();
        _downloads.KeptLast = null;

        _manager.Tidy();

        Assert.Equal(_downloads.PathOf("9.0.2"), _downloads.KeptLast);
        Assert.False(_downloads.RemovedAll);
    }

    [Fact]
    public void Tidying_your_own_copy_is_never_touched()
    {
        _settings.Save(new AppSettings(@"C:\Mine\ffmpeg.exe"));

        _manager.Tidy();

        // Only this app's own folder is ever cleared, and nothing there is in use.
        Assert.True(_downloads.RemovedAll);
        Assert.NotEqual(@"C:\Mine\ffmpeg.exe", _downloads.KeptLast);
    }

    [Fact]
    public async Task Keeps_checking_quietly_through_failures_until_stopped()
    {
        await InstallAsync();
        _time.Advance(FfmpegManager.CheckInterval);
        _downloads.VersionFails = new EncodeException("No connection.");
        var asked = _downloads.VersionRequests;
        using var stop = new CancellationTokenSource();

        var running = _manager.RunChecksAsync(stop.Token);
        await UntilAsync(() => _downloads.VersionRequests > asked);

        _downloads.VersionFails = null;
        _downloads.Latest = "9.1";
        await AdvanceUntilAsync(() => _manager.AvailableUpdate is not null);
        Assert.Equal("9.1", _manager.AvailableUpdate);

        await stop.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "It never happened.");
            await Task.Delay(5);
        }
    }

    private async Task AdvanceUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "It never happened.");
            _time.Advance(TimeSpan.FromHours(6));
            await Task.Delay(10);
        }
    }

    private sealed class FakeDownloads : IFfmpegDownloads
    {
        public string Latest { get; set; } = "9.0.2";

        public int VersionRequests { get; private set; }

        public Exception? VersionFails { get; set; }

        public Exception? InstallFails { get; set; }

        public Action? DuringVersionRequest { get; set; }

        public List<string> Installs { get; } = [];

        public string? KeptLast { get; set; }

        public bool RemovedAll { get; private set; }

        public string PathOf(string version) => $@"C:\App\ffmpeg\{version}\ffmpeg.exe";

        public Task<string> GetLatestVersionAsync(CancellationToken cancellationToken = default)
        {
            VersionRequests++;
            DuringVersionRequest?.Invoke();
            return VersionFails is not null ? Task.FromException<string>(VersionFails) : Task.FromResult(Latest);
        }

        public Task<string> InstallAsync(string version, IProgress<FfmpegDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            if (InstallFails is not null)
            {
                return Task.FromException<string>(InstallFails);
            }

            Installs.Add(version);
            return Task.FromResult(PathOf(version));
        }

        public void RemoveAllExcept(string? path)
        {
            KeptLast = path;
            RemovedAll = path is null;
        }
    }
}
