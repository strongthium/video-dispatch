using VideoDispatch.Core.Model;

namespace VideoDispatch.Core.Accounts;

/// <summary>Messages are shown to the user and logged, so they must never contain a token or secret.</summary>
public abstract class AccountException(Platform platform, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public Platform Platform { get; } = platform;
}

/// <summary>The saved sign-in no longer works, and the user must connect the account again.</summary>
public sealed class ReconnectRequiredException(Platform platform, string message)
    : AccountException(platform, message);

/// <summary>Something the user must fix: wrong keys, a denied sign-in or a missing permission.</summary>
public sealed class AccountSetupException(Platform platform, string message)
    : AccountException(platform, message);

/// <summary>A network or server problem. Trying again later may work.</summary>
public sealed class TransientAccountException(Platform platform, string message, Exception? innerException = null)
    : AccountException(platform, message, innerException);
