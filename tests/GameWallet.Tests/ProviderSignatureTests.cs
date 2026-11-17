using GameWallet.Api.Security;

namespace GameWallet.Tests;

public class ProviderSignatureTests
{
    private const string Secret = "secret";
    private const string Body = """{"playerId":"player-1","currency":"EUR"}""";

    [Fact]
    public void Signature_is_lowercase_hex_sha256()
    {
        var signature = ProviderSignature.Compute(Secret, "1700000000", Body);

        Assert.Equal(64, signature.Length);
        Assert.Equal(signature.ToLowerInvariant(), signature);
    }

    [Fact]
    public void Valid_signature_is_accepted_in_any_case()
    {
        var signature = ProviderSignature.Compute(Secret, "1700000000", Body);

        Assert.True(ProviderSignature.IsValid(Secret, "1700000000", Body, signature));
        Assert.True(ProviderSignature.IsValid(Secret, "1700000000", Body, signature.ToUpperInvariant()));
    }

    [Fact]
    public void Changing_body_timestamp_or_secret_breaks_the_signature()
    {
        var signature = ProviderSignature.Compute(Secret, "1700000000", Body);

        Assert.False(ProviderSignature.IsValid(Secret, "1700000000", Body.Replace("EUR", "USD"), signature));
        Assert.False(ProviderSignature.IsValid(Secret, "1700000001", Body, signature));
        Assert.False(ProviderSignature.IsValid("other", "1700000000", Body, signature));
    }

    [Theory]
    [InlineData("1700000000", true)]
    [InlineData("1700000299", true)]
    [InlineData("1699999600", false)]
    [InlineData("not-a-number", false)]
    [InlineData("", false)]
    public void Timestamps_must_be_recent(string timestamp, bool fresh)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

        Assert.Equal(fresh, ProviderSignature.IsFresh(timestamp, now, maxSkewSeconds: 300));
    }
}
