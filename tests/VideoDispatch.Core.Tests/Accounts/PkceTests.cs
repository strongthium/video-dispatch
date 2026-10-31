using System.Text.RegularExpressions;
using VideoDispatch.Core.Accounts;

namespace VideoDispatch.Core.Tests.Accounts;

public class PkceTests
{
    // The test vector from RFC 7636, Appendix B.
    private const string RfcVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";

    [Fact]
    public void Base64Url_challenge_matches_the_RFC_example()
    {
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.ToBase64UrlChallenge(RfcVerifier));
    }

    [Fact]
    public void Hex_challenge_is_the_same_hash_in_lowercase_hex()
    {
        // The RFC example's hash, written in hex.
        Assert.Equal(
            "13d31e961a1ad8ec2f16b10c4c982e0876a878ad6df144566ee1894acb70f9c3",
            Pkce.ToHexChallenge(RfcVerifier));
    }

    [Fact]
    public void Verifier_meets_the_PKCE_rules()
    {
        var verifier = Pkce.CreateVerifier();

        Assert.InRange(verifier.Length, 43, 128);
        Assert.Matches(new Regex("^[A-Za-z0-9._~-]+$"), verifier);
    }

    [Fact]
    public void Verifiers_and_states_are_not_reused()
    {
        Assert.NotEqual(Pkce.CreateVerifier(), Pkce.CreateVerifier());
        Assert.NotEqual(Pkce.CreateState(), Pkce.CreateState());
    }
}
