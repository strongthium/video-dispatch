using System.Text.Json;
using VideoDispatch.Core.Accounts;
using VideoDispatch.Core.Accounts.Secrets;
using VideoDispatch.Core.Model;

namespace VideoDispatch.Core.Tests.Accounts;

/// <summary>Uses the real Credential Manager under a throwaway service name, and removes what it wrote.</summary>
public sealed class WindowsCredentialStoreTests : IDisposable
{
    private readonly WindowsCredentialStore _store = new($"video-dispatch-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        _store.Delete("key");
        _store.Delete("other");
    }

    [Fact]
    public void Missing_key_reads_as_null() => Assert.Null(_store.Get("key"));

    [Fact]
    public void Stores_and_reads_a_value()
    {
        _store.Set("key", "value");

        Assert.Equal("value", _store.Get("key"));
    }

    [Fact]
    public void Keeps_non_ASCII_text_intact()
    {
        const string value = "naïve 日本語 😀";

        _store.Set("key", value);

        Assert.Equal(value, _store.Get("key"));
    }

    [Fact]
    public void Overwrites_an_existing_value()
    {
        _store.Set("key", "first value that is longer");
        _store.Set("key", "second");

        Assert.Equal("second", _store.Get("key"));
    }

    [Fact]
    public void Keeps_keys_separate()
    {
        _store.Set("key", "a");
        _store.Set("other", "b");

        Assert.Equal("a", _store.Get("key"));
        Assert.Equal("b", _store.Get("other"));
    }

    [Fact]
    public void Stores_an_empty_value()
    {
        _store.Set("key", "");

        Assert.Equal("", _store.Get("key"));
    }

    [Fact]
    public void Delete_removes_the_value()
    {
        _store.Set("key", "value");

        _store.Delete("key");

        Assert.Null(_store.Get("key"));
    }

    [Fact]
    public void Delete_of_a_missing_key_does_nothing() => _store.Delete("key");

    [Fact]
    public void Stores_the_largest_allowed_value()
    {
        var value = new string('a', 2560);

        _store.Set("key", value);

        Assert.Equal(value, _store.Get("key"));
    }

    [Fact]
    public void Refuses_a_value_too_large_without_showing_it()
    {
        var value = new string('s', 2561);

        var exception = Assert.Throws<SecretStoreException>(() => _store.Set("key", value));

        Assert.DoesNotContain("sss", exception.Message);
        Assert.Null(_store.Get("key"));
    }

    [Fact]
    public void A_full_size_stored_account_fits()
    {
        // Google documents refresh tokens up to 512 bytes, and escaped non-ASCII names grow sixfold.
        var account = new StoredAccount
        {
            RefreshToken = new string('r', 512),
            RefreshTokenExpiresAt = DateTimeOffset.UnixEpoch,
            Scopes = [GoogleOAuthProvider.UploadScope, GoogleOAuthProvider.ReadOnlyScope, GoogleOAuthProvider.PlaylistScope],
            AccountId = new string('i', 64),
            DisplayName = new string('名', 100),
        };

        var json = JsonSerializer.Serialize(account, StoredJson.Options);

        _store.Set("key", json);

        Assert.Equal(json, _store.Get("key"));
    }
}
