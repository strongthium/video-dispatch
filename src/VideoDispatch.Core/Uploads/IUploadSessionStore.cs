using VideoDispatch.Core.Accounts.Secrets;

namespace VideoDispatch.Core.Uploads;

/// <summary>Remembers where an unfinished upload can carry on, including after the app restarts.</summary>
public interface IUploadSessionStore
{
    string? Get(Guid jobId);

    void Save(Guid jobId, string sessionUrl);

    void Delete(Guid jobId);
}

/// <summary>A session address may allow uploading without signing in, so it's kept with the secrets rather than in the job's file.</summary>
public sealed class SecretUploadSessionStore(ISecretStore store) : IUploadSessionStore
{
    public string? Get(Guid jobId) => store.Get(Key(jobId));

    public void Save(Guid jobId, string sessionUrl) => store.Set(Key(jobId), sessionUrl);

    public void Delete(Guid jobId) => store.Delete(Key(jobId));

    private static string Key(Guid jobId) => $"upload-session/{jobId:N}";
}
