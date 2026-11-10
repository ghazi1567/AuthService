using AuthService.Entitlements.Testing;
using Microsoft.Extensions.Time.Testing;

namespace AuthService.Entitlements.Tests;

/// <summary>ENT-07 (AUTH-284) Limit values, webhook signatures and reading events. Proposal items 12 and 13.</summary>
public sealed class VerifierAndParserTests
{
    private const string Secret = "whsec_current";
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.Parse("7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11");

    [Fact]
    public void ENT07_ALimitIsWithinUpToItsValueAndUnlimitedAllowsAnything()
    {
        var limit = new LimitValue(50_000);

        limit.IsWithin(50_000).ShouldBeTrue();
        limit.IsWithin(50_001).ShouldBeFalse();
        limit.Remaining(49_000).ShouldBe(1_000);
        limit.Remaining(60_000).ShouldBe(0, "never below zero");
        LimitValue.Unlimited.IsWithin(long.MaxValue).ShouldBeTrue();
        LimitValue.Unlimited.Remaining(5).ShouldBeNull();
        new LimitValue(0).IsOn.ShouldBeFalse();
        new LimitValue(1).IsOn.ShouldBeTrue();
        LimitValue.Unlimited.ToString().ShouldBe("unlimited");
    }

    [Fact]
    public void ENT07_ASignatureMadeWithTheSecretOverTheTimeAndBodyIsAccepted()
    {
        var verifier = Verifier();
        var body = """{"eventId":"x"}""";
        var timestamp = Now.ToUnixTimeSeconds();

        verifier.Verify(timestamp.ToString(), AuthWebhookVerifier.Sign(Secret, timestamp, body), body).ShouldBeTrue();
        verifier.Verify(timestamp.ToString(), AuthWebhookVerifier.Sign(Secret, timestamp, body), body + " ").ShouldBeFalse("the body changed");
        verifier.Verify(timestamp.ToString(), AuthWebhookVerifier.Sign("whsec_other", timestamp, body), body).ShouldBeFalse("another secret");
        verifier.Verify((timestamp + 1).ToString(), AuthWebhookVerifier.Sign(Secret, timestamp, body), body).ShouldBeFalse("the time is signed too");
    }

    [Fact]
    public void ENT07_DuringARotationAnyOfTheSignaturesMatchingIsEnough()
    {
        var body = "{}";
        var timestamp = Now.ToUnixTimeSeconds();
        var header = $"{AuthWebhookVerifier.Sign("whsec_new", timestamp, body)},{AuthWebhookVerifier.Sign(Secret, timestamp, body)}";

        Verifier().Verify(timestamp.ToString(), header, body).ShouldBeTrue("the product still has the previous secret");
        new AuthWebhookVerifier(["whsec_new", Secret], new FakeTimeProvider(Now)).Verify(timestamp.ToString(), header, body).ShouldBeTrue();
    }

    [Theory]
    [InlineData(-301, false)]
    [InlineData(-299, true)]
    [InlineData(299, true)]
    [InlineData(301, false)]
    public void ENT07_ASignedTimeMoreThanFiveMinutesAwayIsRefused(int offsetSeconds, bool accepted)
    {
        var body = "{}";
        var timestamp = Now.AddSeconds(offsetSeconds).ToUnixTimeSeconds();

        Verifier().Verify(timestamp.ToString(), AuthWebhookVerifier.Sign(Secret, timestamp, body), body).ShouldBe(accepted);
    }

    [Theory]
    [InlineData(null, "v1=00")]
    [InlineData("abc", "v1=00")]
    [InlineData("1793268000", null)]
    [InlineData("1793268000", "v2=0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("1793268000", "v1=not-hex-not-hex-not-hex-not-hex-not-hex-not-hex-not-hex-not-hex")]
    public void ENT07_MissingOrMalformedHeadersAreRefusedWithoutThrowing(string? timestamp, string? signature) =>
        Verifier().Verify(timestamp, signature, "{}").ShouldBeFalse();

    [Fact]
    public void ENT07_AVerifierNeedsASecret() =>
        Should.Throw<ArgumentException>(() => new AuthWebhookVerifier([" ", ""]));

    [Fact]
    public void ENT07_AnEntitlementsChangedEventIsReadWithTheWholeSetAndUnknownFieldsAreIgnored()
    {
        var body = $$"""
            {"eventId":"00000000-0000-4000-8000-000000000001","eventType":"EntitlementsChanged","occurredAt":"2026-11-10T18:30:00Z","schemaVersion":1,
             "tenantId":"{{Tenant}}","applicationId":"10000000-0000-4000-8000-000000000001","planKey":"growth","planName":"Growth","version":7,
             "entitlements":{"contacts.reachable":75000,"storage.assets":null},"overridden":["contacts.reachable"],"somethingNew":{"a":1} }
            """;

        var changed = AuthWebhookParser.Parse(body).ShouldBeOfType<EntitlementsChangedEvent>();

        changed.TenantId.ShouldBe(Tenant);
        changed.SchemaVersion.ShouldBe(1);
        changed.Entitlements.Version.ShouldBe(7);
        changed.Entitlements.PlanName.ShouldBe("Growth");
        changed.Entitlements.Get("contacts.reachable").ShouldBe(new LimitValue(75_000));
        changed.Entitlements.Get("storage.assets").ShouldBe(LimitValue.Unlimited);
        changed.Entitlements.Get("journeys.live").ShouldBeNull();
        changed.Entitlements.IsOverridden("contacts.reachable").ShouldBeTrue();
    }

    [Fact]
    public void ENT07_AnApplicationSubscribedFromBeforeEntitlementsReadsAsVersionOneWithNoSnapshot()
    {
        var body = $$"""{"eventId":"00000000-0000-4000-8000-000000000002","eventType":"ApplicationSubscribed","occurredAt":"2026-11-01T10:00:00Z","tenantId":"{{Tenant}}","applicationId":"10000000-0000-4000-8000-000000000001","maxSeats":5}""";

        var subscribed = AuthWebhookParser.Parse(body).ShouldBeOfType<ApplicationSubscribedEvent>();

        subscribed.SchemaVersion.ShouldBe(1);
        subscribed.MaxSeats.ShouldBe(5);
        subscribed.Entitlements.ShouldBeNull();
    }

    [Fact]
    public void ENT07_UnsubscribedAndUnknownEventTypesAreReadAndANewerSchemaIsRefused()
    {
        var auth = new FakeAuthService();
        auth.SetTenant(Tenant);

        AuthWebhookParser.Parse(auth.ApplicationUnsubscribedEvent(Tenant, "Suspended")).ShouldBeOfType<ApplicationUnsubscribedEvent>().Status.ShouldBe("Suspended");
        var unknown = AuthWebhookParser.Parse($$"""{"eventId":"{{Guid.NewGuid()}}","eventType":"SomethingLater","occurredAt":"2026-11-10T18:30:00Z","tenantId":"{{Tenant}}","applicationId":"{{Guid.NewGuid()}}"}""");
        unknown.ShouldBeOfType<UnknownAuthWebhookEvent>().EventType.ShouldBe("SomethingLater");
        Should.Throw<AuthWebhookException>(() => AuthWebhookParser.Parse($$"""{"schemaVersion":2,"eventId":"{{Guid.NewGuid()}}"}""")).Message.ShouldContain("schema version 2");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"eventType":"EntitlementsChanged"}""")]
    public void ENT07_ABodyThatIsNotAnEventIsRefused(string body) =>
        Should.Throw<AuthWebhookException>(() => AuthWebhookParser.Parse(body));

    private static AuthWebhookVerifier Verifier() => new([Secret], new FakeTimeProvider(Now));
}
