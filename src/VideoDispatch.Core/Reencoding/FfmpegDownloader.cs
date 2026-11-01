using System.Buffers;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace VideoDispatch.Core.Reencoding;

/// <summary>How far a download has got.</summary>
/// <param name="Bytes">What has arrived.</param>
/// <param name="Total">How much is coming, if the server said.</param>
/// <param name="IsUnpacking">It has all arrived, and is being unpacked and checked.</param>
public sealed record FfmpegDownloadProgress(long Bytes, long? Total, bool IsUnpacking = false);

/// <summary>Fetches FFmpeg for those who ask for it. Nothing else in the app needs this.</summary>
public interface IFfmpegDownloads
{
    /// <exception cref="EncodeException">It couldn't be asked. The message is written for the user.</exception>
    Task<string> GetLatestVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>Fetches a version, unless it's already here, and returns the path of its <c>ffmpeg.exe</c>.</summary>
    /// <exception cref="EncodeException">It couldn't be done, and nothing was left behind. The message is written for the user.</exception>
    Task<string> InstallAsync(string version, IProgress<FfmpegDownloadProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Deletes every downloaded version but this one. Null deletes them all. One in use just stays for next time.</summary>
    void RemoveAllExcept(string? path);
}

/// <summary>
/// FFmpeg's own site offers only source code, and points Windows users to outside builders. This uses gyan.dev's release
/// builds, because they're stable releases, in a zip that .NET can unpack, with a published version and checksum. Each
/// download names one version, so the file and its checksum can't disagree if a new release appears along the way.
/// The checksum comes from the same site, so it catches a damaged download, not a tampered site. No copy of FFmpeg ever
/// ships with this app: it arrives only when the user asks for it.
/// </summary>
/// <param name="http">Must have no timeout of its own, since this file is large. It may follow redirects.</param>
/// <param name="folder">Where versions are kept, one folder each. Everything in it belongs to this class.</param>
/// <param name="inspect">Asks a program what it is. Replaced only in tests.</param>
public sealed partial class FfmpegDownloader(
    HttpClient http,
    string folder,
    Func<string, CancellationToken, Task<FfmpegInfo>>? inspect = null) : IFfmpegDownloads
{
    public const string SiteName = "gyan.dev";

    private const string Host = "www.gyan.dev";
    private const string Builds = "https://www.gyan.dev/ffmpeg/builds/";
    private const string ProgramName = "ffmpeg.exe";
    private const string LicenseName = "LICENSE";
    private const int MaxTextLength = 1024;
    private const int MoveTries = 6;

    // The zip is about 115 MB. Far more than this isn't what was asked for.
    private const long MaxBytes = 2L * 1024 * 1024 * 1024;

    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MoveWait = TimeSpan.FromMilliseconds(500);

    private readonly Func<string, CancellationToken, Task<FfmpegInfo>> _inspect = inspect ?? FfmpegVideoEncoder.InspectAsync;

    public async Task<string> GetLatestVersionAsync(CancellationToken cancellationToken = default)
    {
        var version = (await ReadTextAsync(Builds + "release-version", cancellationToken)).Trim();
        return IsVersion(version) ? version : throw new EncodeException($"{SiteName} sent an unexpected version number.");
    }

    public async Task<string> InstallAsync(string version, IProgress<FfmpegDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        // This goes in an address and a folder name, so it must be only what it says it is.
        if (!IsVersion(version))
        {
            throw new EncodeException($"{SiteName} sent an unexpected version number.");
        }

        var home = Path.Combine(folder, version);
        var program = Path.Combine(home, ProgramName);
        if (File.Exists(program))
        {
            return program;
        }

        var address = $"{Builds}packages/ffmpeg-{version}-essentials_build.zip";
        var archive = Path.Combine(folder, version + ".zip.part");
        var unpacking = Path.Combine(folder, version + ".part");

        try
        {
            Directory.CreateDirectory(folder);
            var expected = HashIn(await ReadTextAsync(address + ".sha256", cancellationToken));
            var actual = await DownloadAsync(address, archive, progress, cancellationToken);

            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new EncodeException($"The download from {SiteName} doesn't match its published checksum, so it wasn't used. Try again.");
            }

            var size = new FileInfo(archive).Length;
            progress?.Report(new FfmpegDownloadProgress(size, size, IsUnpacking: true));
            DeleteFolder(unpacking);
            Directory.CreateDirectory(unpacking);
            await Task.Run(() => Unpack(archive, unpacking), cancellationToken);

            // A program this app is about to rely on has to say what it is first.
            var info = await _inspect(Path.Combine(unpacking, ProgramName), cancellationToken);
            if (!info.HasH264)
            {
                throw new EncodeException($"The build from {SiteName} has no H.264 encoder, so it can't make the MP4s this app sends.");
            }

            await MoveIntoPlaceAsync(unpacking, home, cancellationToken);
            return program;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new EncodeException($"FFmpeg couldn't be saved: {exception.Message}", exception);
        }
        finally
        {
            TryDeleteFile(archive);
            DeleteFolder(unpacking);
        }
    }

    public void RemoveAllExcept(string? path)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        var keep = path is null ? null : Path.GetDirectoryName(Path.GetFullPath(path));
        foreach (var entry in Directory.EnumerateDirectories(folder))
        {
            if (!string.Equals(Path.GetFullPath(entry), keep, StringComparison.OrdinalIgnoreCase))
            {
                DeleteFolder(entry);
            }
        }

        foreach (var leftover in Directory.EnumerateFiles(folder))
        {
            TryDeleteFile(leftover);
        }
    }

    /// <summary>Only known names are taken, and written under fixed names, so a path inside the zip can't lead anywhere.</summary>
    private static void Unpack(string archive, string destination)
    {
        using var zip = ZipFile.OpenRead(archive);

        var programs = zip.Entries.Where(entry => entry.Name.Equals(ProgramName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (programs.Count != 1)
        {
            throw new EncodeException($"The download from {SiteName} isn't laid out as expected, so it wasn't used.");
        }

        if (programs[0].Length > MaxBytes)
        {
            throw new EncodeException($"The download from {SiteName} is far bigger than expected, so it wasn't used.");
        }

        programs[0].ExtractToFile(Path.Combine(destination, ProgramName));

        // FFmpeg's licence stays with the program it covers.
        var license = zip.Entries.FirstOrDefault(entry => entry.Name.Equals(LicenseName, StringComparison.OrdinalIgnoreCase) && entry.Length <= MaxBytes);
        license?.ExtractToFile(Path.Combine(destination, LicenseName));
    }

    /// <summary>A virus scanner can hold a new program for a moment, so a refusal is tried again before giving up.</summary>
    private static async Task MoveIntoPlaceAsync(string from, string to, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(from, to);
                return;
            }
            catch (Exception exception) when (attempt < MoveTries && exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(MoveWait, cancellationToken);
            }
        }
    }

    private static string HashIn(string text)
    {
        var match = HashPattern().Match(text);
        return match.Success ? match.Value : throw new EncodeException($"{SiteName} sent an unexpected checksum.");
    }

    private static bool IsVersion(string text) => VersionPattern().IsMatch(text);

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The address may redirect, but it has to end up on the same site, over https.</summary>
    private static void CheckEndedUpOnTheSite(HttpResponseMessage response)
    {
        var final = response.RequestMessage?.RequestUri;
        if (final is null || final.Scheme != Uri.UriSchemeHttps || !final.Host.Equals(Host, StringComparison.OrdinalIgnoreCase))
        {
            throw new EncodeException($"The download was sent somewhere other than {SiteName}, so it wasn't used.");
        }
    }

    [GeneratedRegex(@"\A\d{1,4}(\.\d{1,4}){1,3}\z")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"\b[0-9a-fA-F]{64}\b")]
    private static partial Regex HashPattern();

    private async Task<string> ReadTextAsync(string address, CancellationToken cancellationToken)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(StallTimeout);

        try
        {
            using var response = await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, stall.Token);
            CheckEndedUpOnTheSite(response);
            if (!response.IsSuccessStatusCode)
            {
                throw new EncodeException($"{SiteName} answered with an error (HTTP {(int)response.StatusCode}). Try again later.");
            }

            if (response.Content.Headers.ContentLength > MaxTextLength)
            {
                throw new EncodeException($"{SiteName} sent an unexpected answer.");
            }

            var text = await response.Content.ReadAsStringAsync(stall.Token);
            return text.Length > MaxTextLength ? throw new EncodeException($"{SiteName} sent an unexpected answer.") : text;
        }
        catch (HttpRequestException exception)
        {
            throw new EncodeException($"Couldn't reach {SiteName}. Check your connection and try again.", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EncodeException($"{SiteName} took too long to respond. Try again later.", exception);
        }
    }

    /// <summary>Returns the file's SHA-256, worked out as it arrives.</summary>
    private async Task<string> DownloadAsync(string address, string path, IProgress<FfmpegDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(StallTimeout);

        try
        {
            using var response = await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, stall.Token);
            CheckEndedUpOnTheSite(response);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new EncodeException($"{SiteName} no longer has that version. Try again.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new EncodeException($"{SiteName} answered with an error (HTTP {(int)response.StatusCode}). Try again later.");
            }

            var total = response.Content.Headers.ContentLength;
            if (total > MaxBytes)
            {
                throw new EncodeException($"The download from {SiteName} is far bigger than expected, so it wasn't fetched.");
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var source = await response.Content.ReadAsStreamAsync(stall.Token);
            await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);

            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                long received = 0;
                while (true)
                {
                    stall.CancelAfter(StallTimeout);
                    var read = await source.ReadAsync(buffer, stall.Token);
                    if (read == 0)
                    {
                        break;
                    }

                    received += read;
                    if (received > MaxBytes)
                    {
                        throw new EncodeException($"The download from {SiteName} is far bigger than expected, so it wasn't used.");
                    }

                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), stall.Token);
                    progress?.Report(new FfmpegDownloadProgress(received, total));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (HttpRequestException exception)
        {
            throw new EncodeException($"The connection to {SiteName} failed. Check your connection and try again.", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EncodeException($"{SiteName} stopped responding. Try again later.", exception);
        }
    }
}
