using System.Net.Http;
using VideoDispatch.Core.Accounts;
using VideoDispatch.Core.Accounts.Secrets;
using VideoDispatch.Core.Media;
using VideoDispatch.Core.Model;
using VideoDispatch.Core.Presets;
using VideoDispatch.Core.Queue;
using VideoDispatch.Core.TikTok;
using VideoDispatch.Core.Uploads;
using VideoDispatch.Core.YouTube;

namespace VideoDispatch.App;

/// <summary>Everything the screens work with, real or made up.</summary>
public sealed record Services(
    AccountService Accounts,
    Func<Uri, CancellationToken, Task> OpenBrowser,
    UploadQueue Queue,
    PresetLibrary Presets,
    IYouTubeApi YouTube,
    Func<string, VideoMetadata?> ReadMetadata)
{
    private static readonly HttpClient SignInHttp = new();

    public static Services CreateReal()
    {
        var time = TimeProvider.System;
        var secrets = new WindowsCredentialStore();
        var accounts = new AccountService(
            secrets,
            [new TikTokOAuthProvider(SignInHttp), new GoogleOAuthProvider(SignInHttp)],
            time);

        // A chunk on a slow line can take far longer than the default timeout, and YouTube's "keep going" is a 308,
        // which isn't a redirect, so the upload clients have one of their own.
        var uploads = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        var sessions = new SecretUploadSessionStore(secrets);
        var youTube = new YouTubeApi(uploads, accounts, time);

        var queue = new UploadQueue(
            new JsonFolderStore<UploadJob>(AppFolders.Jobs),
            [new TikTokUploader(uploads, accounts, time), new YouTubeUploader(uploads, accounts, sessions, time)],
            sessions,
            youTube,
            new PassThroughRenderStep(),
            accounts.GetStatus,
            time);

        var presets = new PresetLibrary(
            new JsonFolderStore<TikTokPreset>(AppFolders.TikTokPresets),
            new JsonFolderStore<YouTubePreset>(AppFolders.YouTubePresets));

        return new Services(accounts, Browser.OpenSignInAsync, queue, presets, youTube, Mp4MetadataReader.Read);
    }
}
