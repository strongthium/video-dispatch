using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using VideoDispatch.Core.Reencoding;

namespace VideoDispatch.Core.Tests.Reencoding;

/// <summary>Against a made-up site, shaped as gyan.dev answered on 2026-11-01. A real download hasn't been part of these.</summary>
public sealed class FfmpegDownloaderTests : IDisposable
{
    private const string Version = "9.0.2";
    private const string Builds = "https://www.gyan.dev/ffmpeg/builds/";
    private const string Package = Builds + "packages/ffmpeg-9.0.2-essentials_build.zip";

    private static readonly byte[] ProgramBytes = Encoding.ASCII.GetBytes("the program");

    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "ffmpeg-download-" + Guid.NewGuid().ToString("N"));
    private readonly string _folder;
    private readonly Site _site = new();
    private readonly List<string> _inspected = [];
    private FfmpegInfo _info = new("ffmpeg version 9.0.2", true);
    private readonly FfmpegDownloader _downloader;

    public FfmpegDownloaderTests()
    {
        _folder = Path.Combine(_root, "ffmpeg");
        Directory.CreateDirectory(_root);
        _downloader = new FfmpegDownloader(new HttpClient(_site) { Timeout = Timeout.InfiniteTimeSpan }, _folder, (path, _) =>
        {
            Assert.Equal(ProgramBytes, File.ReadAllBytes(path));
            _inspected.Add(path);
            return Task.FromResult(_info);
        });
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static byte[] Zip(params (string Name, byte[] Bytes)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(bytes);
            }
        }

        return memory.ToArray();
    }

    private static byte[] GoodZip() => Zip(
        ("ffmpeg-9.0.2-essentials_build/bin/ffmpeg.exe", ProgramBytes),
        ("ffmpeg-9.0.2-essentials_build/bin/ffprobe.exe", [1, 2, 3]),
        ("ffmpeg-9.0.2-essentials_build/LICENSE", Encoding.ASCII.GetBytes("GPLv3")),
        ("ffmpeg-9.0.2-essentials_build/doc/ffmpeg.html", [4, 5]));

    private void Publish(byte[] zip, string? checksum = null)
    {
        _site.Answer(Package, zip);
        _site.Answer(Package + ".sha256", Encoding.ASCII.GetBytes((checksum ?? Convert.ToHexStringLower(SHA256.HashData(zip))) + "\n"));
    }

    private void AssertNothingLeftBehind() =>
        Assert.Empty(Directory.Exists(_folder) ? Directory.EnumerateFileSystemEntries(_folder) : []);

    [Fact]
    public async Task Reads_the_newest_version()
    {
        _site.Answer(Builds + "release-version", "9.0.2\n"u8.ToArray());

        Assert.Equal(Version, await _downloader.GetLatestVersionAsync());
    }

    [Theory]
    [InlineData("<html>Not what was asked for</html>")]
    [InlineData("..\\..\\evil")]
    [InlineData("9.0.2/../../evil")]
    [InlineData("")]
    public async Task A_version_that_is_not_one_is_refused(string text)
    {
        _site.Answer(Builds + "release-version", Encoding.ASCII.GetBytes(text));

        await Assert.ThrowsAsync<EncodeException>(() => _downloader.GetLatestVersionAsync());
    }

    [Theory]
    [InlineData("..\\..\\evil")]
    [InlineData("9.0.2/../../evil")]
    [InlineData("")]
    public async Task Installing_a_version_that_is_not_one_asks_for_nothing(string version)
    {
        await Assert.ThrowsAsync<EncodeException>(() => _downloader.InstallAsync(version));

        Assert.Empty(_site.Asked);
    }

    [Fact]
    public async Task Fetches_unpacks_checks_and_keeps_only_what_it_needs()
    {
        Publish(GoodZip());

        var path = await _downloader.InstallAsync(Version);

        Assert.Equal(Path.Combine(_folder, Version, "ffmpeg.exe"), path);
        Assert.Equal(ProgramBytes, File.ReadAllBytes(path));
        Assert.Equal(["ffmpeg.exe", "LICENSE"], Directory.EnumerateFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName).Order());
        Assert.Equal([Version], Directory.EnumerateFileSystemEntries(_folder).Select(Path.GetFileName));
        Assert.Single(_inspected);
    }

    [Fact]
    public async Task Asks_for_that_version_by_name_so_a_new_release_cannot_change_the_file_midway()
    {
        Publish(GoodZip());

        await _downloader.InstallAsync(Version);

        Assert.Equal([Package + ".sha256", Package], _site.Asked);
    }

    [Fact]
    public async Task Reports_what_has_arrived_then_that_it_is_unpacking()
    {
        Publish(GoodZip());
        var seen = new List<FfmpegDownloadProgress>();

        await _downloader.InstallAsync(Version, new Collect(seen));

        Assert.Contains(seen, progress => !progress.IsUnpacking && progress.Bytes > 0 && progress.Total == GoodZip().Length);
        Assert.True(seen[^1].IsUnpacking);
    }

    [Fact]
    public async Task A_version_already_here_is_not_fetched_again()
    {
        Publish(GoodZip());
        var first = await _downloader.InstallAsync(Version);
        _site.Asked.Clear();

        var second = await _downloader.InstallAsync(Version);

        Assert.Equal(first, second);
        Assert.Empty(_site.Asked);
    }

    [Fact]
    public async Task A_checksum_that_does_not_match_leaves_nothing_and_runs_nothing()
    {
        Publish(GoodZip(), new string('0', 64));

        var problem = await Assert.ThrowsAsync<EncodeException>(() => _downloader.InstallAsync(Version));

        Assert.Contains("doesn't match", problem.Message);
        Assert.Empty(_inspected);
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task A_page_where_the_checksum_should_be_is_refused()
    {
        Publish(GoodZip(), "<html>Not found</html>");

        await Assert.ThrowsAsync<EncodeException>(() => _downloader.InstallAsync(Version));

        Assert.Empty(_inspected);
    }

    [Fact]
    public async Task A_zip_without_the_program_is_refused()
    {
        Publish(Zip(("readme.txt", [1])));

        var problem = await Assert.ThrowsAsync<EncodeException>(() => _downloader.InstallAsync(Version));

        Assert.Contains("isn't laid out as expected", problem.Message);
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task A_zip_with_two_programs_is_refused()
    {
        Publish(Zip(("a/ffmpeg.exe", ProgramBytes), ("b/ffmpeg.exe", ProgramBytes)));

        await Assert.ThrowsAsync<EncodeException>(() => _downloader.InstallAsync(Version));
    }

    [Fact]
    public async Task A_path_inside_the_zip_cannot_lead_anywhere_else()
    {
        Publish(Zip(("../../../ffmpeg.exe", ProgramBytes), ("../../evil.exe", [6]), ("../../LICENSE", [7])));

        var path = await _downloader.InstallAsync(Version);

        Assert.Equal(Path.Combine(_folder, Version, "ffmpeg.exe"), path);
        Assert.Equal(
            [path, Path.Combine(_folder, Version, "LICENSE")],
            Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Order());
    }

    [Fact]
    public async Task A_zip_that_is_not_one_says_so()
    {
        Publish([1, 2, 3, 4]);

        await Assert.ThrowsAsync<EncodeException>(() => _downloader.InstallAsync(Version));

        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task A_build_without_H264_is_not_kept()
    {
        Publish(GoodZip());
        _info = new FfmpegInfo("ffmpeg version 9.0.2", false);

        var problem = await Assert.ThrowsAsync<EncodeException>(() => _downloader.InstallAsync(Version));

        Assert.Contains("no H.264 encoder", problem.Message);
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task Something_that_is_not_FFmpeg_is_not_kept()
    {
        Publish(GoodZip());
        var downloader = new FfmpegDownloader(
            new HttpClient(_site) { Timeout = Timeout.InfiniteTimeSpan },
            _folder,
            (_, _) => throw new EncodeException("That program doesn't say it's FFmpeg."));

        await Assert.ThrowsAsync<EncodeException>(() => downloader.InstallAsync(Version));

        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task Ending_up_on_another_site_is_refused()
    {
        Publish(GoodZip());
        _site.EndUpAt = new Uri("https://example.com/somewhere-else.zip");

        var problem = await Assert.ThrowsAsync<EncodeException>(() => _downloader.InstallAsync(Version));

        Assert.Contains("somewhere other than", problem.Message);
    }

    [Fact]
    public async Task Ending_up_on_plain_http_is_refused()
    {
        Publish(GoodZip());
        _site.EndUpAt = new Uri("http://www.gyan.dev/ffmpeg/builds/packages/x.zip");

        await Assert.ThrowsAsync<EncodeException>(() => _downloader.InstallAsync(Version));
    }

    [Fact]
    public async Task A_version_the_site_no_longer_has_says_so()
    {
        _site.Answer(Package + ".sha256", Encoding.ASCII.GetBytes(new string('0', 64)));
        _site.Status[Package] = HttpStatusCode.NotFound;

        var problem = await Assert.ThrowsAsync<EncodeException>(() => _downloader.InstallAsync(Version));

        Assert.Contains("no longer has", problem.Message);
    }

    [Fact]
    public async Task No_connection_says_so_in_plain_words()
    {
        _site.Throw = new HttpRequestException("A long technical message");

        var problem = await Assert.ThrowsAsync<EncodeException>(() => _downloader.GetLatestVersionAsync());

        Assert.Contains("Check your connection", problem.Message);
    }

    [Fact]
    public async Task Cancelling_leaves_nothing_behind()
    {
        Publish(GoodZip());
        using var stop = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _downloader.InstallAsync(Version, new OnFirstReport(stop), stop.Token));

        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task Removes_every_version_but_the_one_in_use_and_any_leftovers()
    {
        Publish(GoodZip());
        var kept = await _downloader.InstallAsync(Version);
        Directory.CreateDirectory(Path.Combine(_folder, "8.1"));
        Directory.CreateDirectory(Path.Combine(_folder, "9.0.3.part"));
        File.WriteAllBytes(Path.Combine(_folder, "9.0.3.zip.part"), [1]);

        _downloader.RemoveAllExcept(kept);

        Assert.Equal([Version], Directory.EnumerateFileSystemEntries(_folder).Select(Path.GetFileName));
        Assert.True(File.Exists(kept));
    }

    [Fact]
    public async Task Removing_them_all_leaves_the_rest_of_the_disk_alone()
    {
        Publish(GoodZip());
        await _downloader.InstallAsync(Version);
        var outside = Path.Combine(_root, "outside.txt");
        File.WriteAllText(outside, "mine");

        _downloader.RemoveAllExcept(null);

        AssertNothingLeftBehind();
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void Removing_before_anything_was_fetched_is_fine() => _downloader.RemoveAllExcept(null);

    private sealed class Collect(List<FfmpegDownloadProgress> seen) : IProgress<FfmpegDownloadProgress>
    {
        public void Report(FfmpegDownloadProgress value) => seen.Add(value);
    }

    private sealed class OnFirstReport(CancellationTokenSource stop) : IProgress<FfmpegDownloadProgress>
    {
        public void Report(FfmpegDownloadProgress value) => stop.Cancel();
    }

    private sealed class Site : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _answers = [];

        public List<string> Asked { get; } = [];

        public Dictionary<string, HttpStatusCode> Status { get; } = [];

        public Uri? EndUpAt { get; set; }

        public Exception? Throw { get; set; }

        public void Answer(string address, byte[] body) => _answers[address] = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            var address = request.RequestUri!.AbsoluteUri;
            Asked.Add(address);

            var status = Status.GetValueOrDefault(address, _answers.ContainsKey(address) ? HttpStatusCode.OK : HttpStatusCode.NotFound);
            if (EndUpAt is not null)
            {
                request.RequestUri = EndUpAt;
            }

            return Task.FromResult(new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(_answers.GetValueOrDefault(address, [])),
            });
        }
    }
}
