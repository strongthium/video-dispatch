using System.Net.Http;
using VideoDispatch.Core;
using VideoDispatch.Core.Accounts;
using VideoDispatch.Core.Accounts.Secrets;
using VideoDispatch.Core.Media;
using VideoDispatch.Core.Model;
using VideoDispatch.Core.Presets;
using VideoDispatch.Core.Queue;
using VideoDispatch.Core.Reencoding;
using VideoDispatch.Core.TikTok;
using VideoDispatch.Core.Uploads;
using VideoDispatch.Core.YouTube;
using VideoDispatch.Editing;

namespace VideoDispatch.App;

/// <summary>Everything the screens work with, real or made up.</summary>
public sealed record Services(
    AccountService Accounts,
    Func<Uri, CancellationToken, Task> OpenBrowser,
    UploadQueue Queue,
    PresetLibrary Presets,
    IYouTubeApi YouTube,
    Func<string, VideoMetadata?> ReadMetadata,
    AppSettingsStore Settings,
    FfmpegManager Ffmpeg,
    Func<string, CancellationToken, Task<FfmpegInfo>> InspectFfmpeg)
{
    public static Services CreateReal() =>
        Create(new WindowsCredentialStore(), AppFolders.ForThisUser, Browser.OpenSignInAsync, handler => handler);

    /// <summary>The real parts, put together.</summary>
    /// <param name="secrets">Keeps the keys, sign-ins and unfinished uploads.</param>
    /// <param name="folders">Keeps the jobs and presets.</param>
    /// <param name="openBrowser">Shows a sign-in page.</param>
    /// <param name="network">Lets the end-to-end test send the real requests to its local servers.</param>
    public static Services Create(
        ISecretStore secrets,
        AppFolders folders,
        Func<Uri, CancellationToken, Task> openBrowser,
        Func<HttpMessageHandler, HttpMessageHandler> network)
    {
        var time = TimeProvider.System;
        var signIn = new HttpClient(network(new SocketsHttpHandler()));
        var accounts = new AccountService(
            secrets,
            [new TikTokOAuthProvider(signIn), new GoogleOAuthProvider(signIn)],
            time);

        // A chunk on a slow line can take far longer than the default timeout, and YouTube's "keep going" is a 308,
        // which isn't a redirect, so the upload clients have one of their own.
        var uploads = new HttpClient(network(new SocketsHttpHandler { AllowAutoRedirect = false })) { Timeout = Timeout.InfiniteTimeSpan };
        var sessions = new SecretUploadSessionStore(secrets);
        var youTube = new YouTubeApi(uploads, accounts, time);
        var settings = new AppSettingsStore(folders.Settings);

        // FFmpeg is only ever the user's choice. Without it, Windows' own tools do the work.
        IVideoEncoder Encoder() => settings.Current.FfmpegPath is { Length: > 0 } ffmpeg
            ? new FfmpegVideoEncoder(ffmpeg)
            : new WindowsVideoEncoder();

        var queue = new UploadQueue(
            new JsonFolderStore<UploadJob>(folders.Jobs),
            [new TikTokUploader(uploads, accounts, time), new YouTubeUploader(uploads, accounts, sessions, time)],
            sessions,
            youTube,
            new EncodeRenderStep(Encoder, folders.Rendered, Mp4MetadataReader.Read),
            accounts.GetStatus,
            time);

        var presets = new PresetLibrary(
            new JsonFolderStore<TikTokPreset>(folders.TikTokPresets),
            new JsonFolderStore<YouTubePreset>(folders.YouTubePresets));

        var downloads = new HttpClient(network(new SocketsHttpHandler())) { Timeout = Timeout.InfiniteTimeSpan };
        downloads.DefaultRequestHeaders.UserAgent.ParseAdd($"video-dispatch/{Updater.RunningVersion.ToString(4)}");
        var ffmpeg = new FfmpegManager(settings, new FfmpegDownloader(downloads, folders.Ffmpeg), time);

        return new Services(accounts, openBrowser, queue, presets, youTube, Mp4MetadataReader.Read, settings, ffmpeg, FfmpegVideoEncoder.InspectAsync);
    }
}
