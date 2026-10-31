using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace VideoDispatch.Core.Accounts;

public static class Pkce
{
    /// <summary>A new random verifier: 64 characters from the set PKCE allows.</summary>
    public static string CreateVerifier() => CreateRandomToken(48);

    public static string CreateState() => CreateRandomToken(32);

    /// <summary>The challenge TikTok's desktop flow requires: lowercase hex, not base64url.</summary>
    public static string ToHexChallenge(string verifier) => Convert.ToHexStringLower(Hash(verifier));

    /// <summary>The standard S256 challenge from RFC 7636, which Google uses.</summary>
    public static string ToBase64UrlChallenge(string verifier) => Base64Url.EncodeToString(Hash(verifier));

    private static byte[] Hash(string verifier) => SHA256.HashData(Encoding.ASCII.GetBytes(verifier));

    private static string CreateRandomToken(int bytes) => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(bytes));
}
