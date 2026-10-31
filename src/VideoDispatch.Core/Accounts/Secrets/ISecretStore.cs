namespace VideoDispatch.Core.Accounts.Secrets;

/// <summary>The secure store for keys and sign-ins. There is deliberately no plain-file fallback.</summary>
public interface ISecretStore
{
    string? Get(string key);

    void Set(string key, string value);

    /// <summary>Does nothing if the key doesn't exist.</summary>
    void Delete(string key);
}

/// <summary>The store failed. The message never contains the secret.</summary>
public sealed class SecretStoreException(string message, Exception? innerException = null)
    : Exception(message, innerException);
