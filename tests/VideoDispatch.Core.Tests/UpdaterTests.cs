using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace VideoDispatch.Core.Tests;

public sealed class UpdaterTests : IDisposable
{
    private const string Repository = "strongthium/video-dispatch";
    private const string Downloads = "https://github.com/strongthium/video-dispatch/releases/download/v9.9.9/";

    private readonly string _folder = Path.Combine(AppContext.BaseDirectory, "updater-" + Guid.NewGuid().ToString("N"));

    public UpdaterTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static Updater NewUpdater(Func<HttpRequestMessage, HttpResponseMessage> answer) => new(
        new HttpClient(new Answering(answer)),
        Repository,
        version => $"video-dispatch-{version}-win-x64-needs-dotnet.zip",
        "video-dispatch.exe");

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static byte[] Zip(string name, string content)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(Encoding.UTF8.GetBytes(content));
        }

        return memory.ToArray();
    }

    [Fact]
    public async Task Reads_the_latest_release_and_keeps_only_this_repositorys_files()
    {
        var updater = NewUpdater(_ => Json($$"""
            {
              "tag_name": "v9.9.9",
              "html_url": "https://github.com/{{Repository}}/releases/tag/v9.9.9",
              "assets": [
                { "name": "SHA256SUMS", "browser_download_url": "{{Downloads}}SHA256SUMS" },
                { "name": "evil.zip", "browser_download_url": "https://elsewhere.test/evil.zip" }
              ]
            }
            """));

        var release = await updater.LatestAsync();

        Assert.Equal(new Version(9, 9, 9), release.Version);
        Assert.Equal(["SHA256SUMS"], release.Files.Keys);
    }

    [Fact]
    public async Task Says_plainly_when_there_is_no_release_yet()
    {
        var updater = NewUpdater(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => updater.LatestAsync());

        Assert.Contains("no release", failure.Message);
    }

    [Fact]
    public async Task Refuses_a_tag_that_isnt_a_version()
    {
        var updater = NewUpdater(_ => Json("""{"tag_name":"latest","html_url":"x","assets":[]}"""));

        await Assert.ThrowsAsync<InvalidDataException>(() => updater.LatestAsync());
    }

    [Fact]
    public async Task Replaces_nothing_when_the_download_doesnt_match_its_checksum()
    {
        var name = "video-dispatch-9.9.9-win-x64-needs-dotnet.zip";
        var updater = NewUpdater(request => request.RequestUri!.AbsoluteUri.EndsWith("SHA256SUMS", StringComparison.Ordinal)
            ? Json($"{new string('0', 64)}  {name}\n")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip("video-dispatch.exe", "new")) });
        var release = new Release(new Version(9, 9, 9), "page", new Dictionary<string, string>
        {
            [name] = Downloads + name,
            ["SHA256SUMS"] = Downloads + "SHA256SUMS",
        });
        var running = Path.Combine(_folder, "video-dispatch.exe");
        File.WriteAllText(running, "old");

        await Assert.ThrowsAsync<InvalidDataException>(() => updater.InstallAsync(release, running));

        Assert.Equal("old", File.ReadAllText(running));
    }

    [Fact]
    public async Task Installs_a_download_that_matches_and_keeps_the_old_one_beside_it()
    {
        var name = "video-dispatch-9.9.9-win-x64-needs-dotnet.zip";
        var zip = Zip("video-dispatch.exe", "new");
        var hash = Convert.ToHexString(SHA256.HashData(zip));
        var updater = NewUpdater(request => request.RequestUri!.AbsoluteUri.EndsWith("SHA256SUMS", StringComparison.Ordinal)
            ? Json($"{hash}  {name}\n")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });
        var release = new Release(new Version(9, 9, 9), "page", new Dictionary<string, string>
        {
            [name] = Downloads + name,
            ["SHA256SUMS"] = Downloads + "SHA256SUMS",
        });
        var running = Path.Combine(_folder, "video-dispatch.exe");
        File.WriteAllText(running, "old");

        await updater.InstallAsync(release, running);

        Assert.Equal("new", File.ReadAllText(running));
        Assert.Equal("old", File.ReadAllText(running + ".old"));
        Assert.False(File.Exists(running + ".new"));

        Updater.Undo(running);

        Assert.Equal("old", File.ReadAllText(running));
    }

    [Theory]
    [InlineData("abc  file.zip", "file.zip", "abc")]
    [InlineData("abc *file.zip", "file.zip", "abc")]
    [InlineData("abc  other.zip", "file.zip", null)]
    [InlineData("", "file.zip", null)]
    public void Reads_a_checksum_file(string sums, string name, string? expected)
    {
        Assert.Equal(expected, Updater.HashIn(sums, name));
    }

    private sealed class Answering(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }
}
