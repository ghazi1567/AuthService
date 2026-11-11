using AuthService.Authorization;
using AuthService.Entitlements.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AuthService.Entitlements.Tests;

/// <summary>ENT-07 (AUTH-284) The store keeps only newer versions, the reads work through the product's client, and a limit check. Proposal items 14 and 15.</summary>
public sealed class StoreClientAndLimitTests
{
    private static readonly Guid Acme = Guid.Parse("7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11");
    private static readonly Guid Beta = Guid.Parse("7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d12");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ENT07_TheStoreKeepsOnlyAHigherVersionAndKeepsTheStatusAnEventDoesNotCarry()
    {
        var store = new InMemoryEntitlementStore();
        var auth = new FakeAuthService().AddLimit("seats", "Users", 1);
        var first = auth.SetTenant(Acme, new Dictionary<string, long?> { ["seats"] = 5 }, status: "Active");
        var second = auth.SetTenant(Acme, new Dictionary<string, long?> { ["seats"] = 8 });

        (await store.ApplyAsync(second with { Status = null }, Token)).ShouldBeTrue();
        await store.SetStatusAsync(Acme, "Suspended", Token);
        (await store.ApplyAsync(first, Token)).ShouldBeFalse("an older version is old news");
        (await store.ApplyAsync(second, Token)).ShouldBeFalse("the same version is a duplicate");

        var kept = (await store.GetAsync(Acme, Token))!;
        kept.Version.ShouldBe(2);
        kept.Get("seats").ShouldBe(new LimitValue(8));
        kept.Status.ShouldBe("Suspended");
        var third = auth.SetTenant(Acme, new Dictionary<string, long?> { ["seats"] = 9 });
        (await store.ApplyAsync(third with { Status = null }, Token)).ShouldBeTrue();
        (await store.GetAsync(Acme, Token))!.Status.ShouldBe("Suspended", "an event without a status keeps the one known");
    }

    [Fact]
    public async Task ENT07_TheReadsAnswerLimitsPlansOneTenantAndTheChangesInOrder()
    {
        var auth = Auth();
        auth.SetTenant(Beta, new Dictionary<string, long?> { ["contacts.reachable"] = 5_000 });
        var client = Services(auth).GetRequiredService<IEntitlementsClient>();

        var limits = await client.GetLimitsAsync(Token);
        var plans = await client.GetPlansAsync(Token);
        var acme = await client.GetTenantAsync(Acme, Token);
        var page = await client.GetChangesAsync(limit: 1, cancellationToken: Token);
        var rest = await client.GetChangesAsync(page.Next, cancellationToken: Token);

        limits.Select(limit => limit.Key).ShouldBe(["contacts.reachable", "journeys.ai", "storage.assets"]);
        limits.Single(limit => limit.Key == "storage.assets").Default.IsUnlimited.ShouldBeTrue();
        plans.ShouldHaveSingleItem().Values["contacts.reachable"].ShouldBe(new LimitValue(50_000));
        acme!.PlanKey.ShouldBe("growth");
        acme.Status.ShouldBe("Active");
        acme.ChangeNumber.ShouldBe(1);
        page.Items.ShouldHaveSingleItem().TenantId.ShouldBe(Acme);
        page.Next.ShouldBe(1);
        rest.Items.ShouldHaveSingleItem().TenantId.ShouldBe(Beta);
        rest.Next.ShouldBeNull();
        (await client.GetTenantAsync(Guid.NewGuid(), Token)).ShouldBeNull();
        auth.Requests.ShouldContain("GET /api/auth/applications/me/entitlements?limit=500&after=1");
    }

    [Fact]
    public async Task ENT07_CatchingUpAppliesEverythingSinceAndReturnsTheNumberToKeep()
    {
        var auth = Auth();
        var services = Services(auth);
        var client = services.GetRequiredService<IEntitlementsClient>();
        var store = services.GetRequiredService<IEntitlementStore>();

        var kept = await client.CatchUpAsync(store, cancellationToken: Token);
        auth.SetTenant(Beta, new Dictionary<string, long?> { ["contacts.reachable"] = 5_000 });
        auth.SetTenant(Acme, new Dictionary<string, long?> { ["contacts.reachable"] = 60_000 }, planKey: "growth", planName: "Growth");
        var next = await client.CatchUpAsync(store, kept, Token);

        kept.ShouldBe(1);
        next.ShouldBe(3);
        (await store.GetAsync(Acme, Token))!.Get("contacts.reachable").ShouldBe(new LimitValue(60_000));
        (await store.GetAsync(Beta, Token))!.Version.ShouldBe(1);
        (await client.CatchUpAsync(store, next, Token)).ShouldBe(3, "nothing changed");
    }

    [Fact]
    public async Task ENT07_ALimitComesFromTheStoreAfterOneReadAndAMissingKeyIsItsDefaultNeverUnlimited()
    {
        var auth = Auth();
        var entitlements = Services(auth).GetRequiredService<IEntitlements>();

        (await entitlements.GetLimitAsync(Acme, "contacts.reachable", Token)).ShouldBe(new LimitValue(50_000));
        (await entitlements.GetLimitAsync(Acme, "contacts.reachable", Token)).ShouldBe(new LimitValue(50_000));
        auth.Requests.Count(request => request.Contains($"/tenants/{Acme}/", StringComparison.Ordinal)).ShouldBe(1, "then answered from the store");

        auth.AddLimit("journeys.live", "Live journeys", 3);
        (await entitlements.GetLimitAsync(Acme, "journeys.live", Token)).ShouldBe(new LimitValue(3), "added after the snapshot: its default");
        await Should.ThrowAsync<KeyNotFoundException>(() => entitlements.GetLimitAsync(Acme, "sms.monthly", Token));
        await Should.ThrowAsync<InvalidOperationException>(() => entitlements.GetLimitAsync(Guid.NewGuid(), "contacts.reachable", Token));
    }

    [Fact]
    public async Task ENT07_AnAuthServiceRefusalIsAnAuthServiceExceptionWithItsCode()
    {
        var auth = Auth();
        auth.Respond = (_, _) => new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"status":403,"title":"Forbidden","detail":"Not allowed.","code":"forbidden"}"""),
        };
        var client = Services(auth).GetRequiredService<IEntitlementsClient>();

        var refused = await Should.ThrowAsync<AuthServiceException>(() => client.GetLimitsAsync(Token));

        refused.StatusCode.ShouldBe(System.Net.HttpStatusCode.Forbidden);
        refused.Code.ShouldBe("forbidden");
    }

    [Fact]
    public void ENT07_WithoutTheProductsClientTheReadsSayWhatIsMissing()
    {
        var services = new ServiceCollection().AddLogging().AddAuthServiceEntitlements(options => options.WebhookSecrets.Add("whsec_test")).BuildServiceProvider();

        Should.Throw<InvalidOperationException>(() => services.GetRequiredService<IEntitlementsClient>()).Message.ShouldContain("AddAuthServiceProduct");
    }

    internal static FakeAuthService Auth()
    {
        var auth = new FakeAuthService()
            .AddLimit("contacts.reachable", "Reachable contacts", 1_000, "contacts")
            .AddLimit("journeys.ai", "AI journeys", 0, "flag")
            .AddLimit("storage.assets", "Asset storage", null, "MB")
            .AddPlan("growth", "Growth", new Dictionary<string, long?> { ["contacts.reachable"] = 50_000 });
        auth.SetTenant(Acme, new Dictionary<string, long?> { ["contacts.reachable"] = 50_000 }, planKey: "growth", planName: "Growth");
        return auth;
    }

    private static ServiceProvider Services(FakeAuthService auth) => new ServiceCollection()
        .AddLogging(logging => logging.ClearProviders())
        .AddSingleton<IAuthServiceClient>(auth)
        .AddAuthServiceEntitlements(options => options.WebhookSecrets.Add("whsec_test"))
        .BuildServiceProvider();
}
