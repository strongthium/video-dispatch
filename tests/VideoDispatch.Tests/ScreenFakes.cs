using System.Collections.Concurrent;
using VideoDispatch.Core.Accounts;
using VideoDispatch.Core.Model;
using VideoDispatch.Core.Queue;
using VideoDispatch.Core.Uploads;
using VideoDispatch.Core.YouTube;

namespace VideoDispatch.Tests;

internal sealed class HeldUploader(Platform platform) : IVideoUploader
{
    public Platform Platform => platform;

    public TaskCompletionSource<UploadReceipt> Upload { get; set; } = new();

    public TaskCompletionSource<JobState> Check { get; set; } = new();

    public Task<UploadReceipt> UploadAsync(UploadJob job, IProgress<long>? progress = null, CancellationToken cancellationToken = default) =>
        Upload.Task.WaitAsync(cancellationToken);

    public Task<JobState> WaitForResultAsync(string remoteId, CancellationToken cancellationToken = default) =>
        Check.Task.WaitAsync(cancellationToken);
}

internal sealed class Records<T> : IRecordStore<T>
{
    public ConcurrentDictionary<Guid, T> Saved { get; } = new();

    public List<string> Damaged { get; } = [];

    public StoreLoad<T> LoadAll() => new(Saved.Values.ToList(), Damaged);

    public void Save(Guid id, T item) => Saved[id] = item;

    public void Delete(Guid id) => Saved.TryRemove(id, out _);
}

internal sealed class NoSessions : IUploadSessionStore
{
    public string? Get(Guid jobId) => null;

    public void Save(Guid jobId, string sessionUrl)
    {
    }

    public void Delete(Guid jobId)
    {
    }
}

internal sealed class StubYouTube : IYouTubeApi
{
    public int CategoryCalls { get; private set; }

    public Exception? Throw { get; set; }

    public Task<IReadOnlyList<YouTubePlaylist>> ListPlaylistsAsync(CancellationToken cancellationToken = default) =>
        Throw is not null
            ? Task.FromException<IReadOnlyList<YouTubePlaylist>>(Throw)
            : Task.FromResult<IReadOnlyList<YouTubePlaylist>>([new("PL1", "Trail Notes")]);

    public Task<IReadOnlyList<YouTubeCategory>> ListCategoriesAsync(string regionCode, CancellationToken cancellationToken = default)
    {
        CategoryCalls++;
        return Throw is not null
            ? Task.FromException<IReadOnlyList<YouTubeCategory>>(Throw)
            : Task.FromResult<IReadOnlyList<YouTubeCategory>>([new("19", "Travel & Events")]);
    }

    public Task AddToPlaylistAsync(string playlistId, string videoId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

internal static class Accounts
{
    public static AccountStatus Connected(Platform platform) =>
        new(platform, true, ConnectionState.Connected, "Sample", [GoogleOAuthProvider.PlaylistScope]);

    public static AccountStatus ConnectedWithoutPlaylists(Platform platform) =>
        new(platform, true, ConnectionState.Connected, "Sample", [GoogleOAuthProvider.UploadScope]);

    public static AccountStatus NotConnected(Platform platform) =>
        new(platform, false, ConnectionState.NotConnected, null, []);
}
