using System.Net;
using AuthService.Api.Features.Webhooks;

namespace AuthService.IntegrationTests.Sub06;

/// <summary>The pure parts of SUB-06: the signature and which addresses count as private. No database or server is needed.</summary>
public sealed class WebhookSigningUnitTests
{
    [Fact]
    public void TheSignatureIsTheHmacSha256OfTimestampDotBodyInLowercaseHexWithAVersionPrefix()
    {
        // A reference value computed outside the code: printf '1700000000.{"a":1}' | openssl dgst -sha256 -hmac 'whsec_test' -hex
        var signature = WebhookSigner.Sign("whsec_test", 1700000000, """{"a":1}""");

        signature.ShouldBe("v1=38877139021993b830af32feea6e18a8da83eb2f6e49ee50bd9e4cf4ca4d3789");
    }

    [Fact]
    public void TheSignatureChangesWithTheSecretTheTimestampAndTheBody()
    {
        var baseline = WebhookSigner.Sign("secret", 100, "body");

        WebhookSigner.Sign("secret", 100, "body").ShouldBe(baseline, "it is deterministic");
        WebhookSigner.Sign("other", 100, "body").ShouldNotBe(baseline);
        WebhookSigner.Sign("secret", 101, "body").ShouldNotBe(baseline);
        WebhookSigner.Sign("secret", 100, "body ").ShouldNotBe(baseline);
    }

    [Fact]
    public void NewSecretsAreLongRandomAndSafeToCopy()
    {
        var secrets = Enumerable.Range(0, 20).Select(_ => WebhookSigner.NewSecret()).ToList();

        secrets.Distinct().Count().ShouldBe(20);
        secrets.All(IsSafeToCopy).ShouldBeTrue();

        static bool IsSafeToCopy(string secret) =>
            secret.StartsWith("whsec_", StringComparison.Ordinal) && secret.Length >= 6 + 43
            && secret.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    }

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.0.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("224.0.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("93.184.216.34", false)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fc00::1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData("::ffff:8.8.8.8", false)]
    [InlineData("2606:4700:4700::1111", false)]
    public void PrivateLoopbackAndLinkLocalAddressesAreRecognisedIncludingWhenWrappedInIPv6(string address, bool expectedPrivate)
    {
        WebhookUrlValidator.IsPrivate(IPAddress.Parse(address)).ShouldBe(expectedPrivate);
    }

    [Theory]
    [InlineData("https://hooks.example.com/a", false, true)]
    [InlineData("http://hooks.example.com/a", false, false)]
    [InlineData("http://hooks.example.com/a", true, true)]
    [InlineData("https://localhost:5001/a", true, true)]
    [InlineData("https://localhost:5001/a", false, false)]
    public void TheSchemeRuleDependsOnWhetherPrivateAddressesAreAllowed(string url, bool allowPrivate, bool expectedValid)
    {
        (WebhookUrlValidator.TryValidate(url, allowPrivate, out _) is not null).ShouldBe(expectedValid);
    }
}
