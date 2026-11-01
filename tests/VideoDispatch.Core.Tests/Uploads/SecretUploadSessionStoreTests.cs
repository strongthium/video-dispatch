using VideoDispatch.Core.Tests.Accounts;
using VideoDispatch.Core.Uploads;

namespace VideoDispatch.Core.Tests.Uploads;

public sealed class SecretUploadSessionStoreTests
{
    [Fact]
    public void Keeps_each_jobs_session_apart_until_deleted()
    {
        var store = new SecretUploadSessionStore(new InMemorySecretStore());
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        store.Save(first, "https://one.test");
        store.Save(second, "https://two.test");

        Assert.Equal("https://one.test", store.Get(first));
        Assert.Equal("https://two.test", store.Get(second));

        store.Delete(first);

        Assert.Null(store.Get(first));
        Assert.Equal("https://two.test", store.Get(second));
    }
}
