using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuthService.Api.Features.Applications;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.BuiltIn;
using AuthService.Api.Features.Subscriptions;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AuthService.IntegrationTests.Onb02;

/// <summary>
/// ONB-02 (AUTH-273) A platform admin subscribes an organisation to a product, and creates a test organisation for it, through the real
/// <c>POST /api/auth/admin/applications/{id}/subscriptions</c> and <c>.../test-organisations</c>. The tests have no workbook Test Case ID:
/// the story's acceptance criteria cover them.
/// </summary>
public sealed class AdminSubscribeAndTestOrganisationTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";
    private const string OwnerEmail = "owner@acme.com";
    private const string DevEmail = "dev@marketing.test";

    [Fact]
    public async Task ONB02_AnAdminSubscribesAnExistingOrganisationAndTheProductHearsAboutIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (admin, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "marketing", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);

        var response = await PostAsync(SubscribeRoute(product.Id), new { tenantId = owner.TenantId, maxSeats = 5 }, adminToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var subscription = (await response.Content.ReadFromJsonAsync<SubscriptionResponse>(Json, ct))!;
        subscription.TenantId.ShouldBe(owner.TenantId);
        subscription.ApplicationId.ShouldBe(product.Id);
        subscription.Status.ShouldBe("Active");
        subscription.MaxSeats.ShouldBe(5);

        await WithDbAsync(async db =>
        {
            var message = await db.WebhookOutbox.AsNoTracking().SingleAsync(ct);
            message.ApplicationId.ShouldBe(product.Id);
            message.EventType.ShouldBe("ApplicationSubscribed");

            var entry = await db.AuditEntries.AsNoTracking().SingleAsync(candidate => candidate.Action == AuditActions.TenantSubscribed, ct);
            entry.ActorType.ShouldBe(AuditActorType.User, "the admin did it, not the product");
            entry.ActorId.ShouldBe(admin.UserId);
            entry.TenantId.ShouldBe(owner.TenantId);
            entry.Detail.ShouldBe("marketing");

            (await db.ApplicationAccess.CountAsync(access => access.ApplicationId == product.Id, ct)).ShouldBe(1, "the Owner has the first seat");
        });
    }

    [Fact]
    public async Task ONB02_SubscribingAgainChangesNothingAndASuspendedSubscriptionIsActiveAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "marketing", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        await PostAsync(SubscribeRoute(product.Id), new { tenantId = owner.TenantId, maxSeats = 5 }, adminToken, ct);

        var again = await PostAsync(SubscribeRoute(product.Id), new { tenantId = owner.TenantId, maxSeats = 50 }, adminToken, ct);

        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await again.Content.ReadFromJsonAsync<SubscriptionResponse>(Json, ct))!.MaxSeats.ShouldBe(5, "an active subscription is left as it is");

        (await PatchJsonAsync(SubscriptionEndpoints.StatusRouteFor(owner.TenantId, product.Id), """{"status":"Suspended"}""", product.ServiceToken, ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var back = await PostAsync(SubscribeRoute(product.Id), new { tenantId = owner.TenantId, maxSeats = 8 }, adminToken, ct);

        back.StatusCode.ShouldBe(HttpStatusCode.OK);
        var restored = (await back.Content.ReadFromJsonAsync<SubscriptionResponse>(Json, ct))!;
        restored.Status.ShouldBe("Active");
        restored.MaxSeats.ShouldBe(8);
    }

    [Fact]
    public async Task ONB02_ASubscriptionNeedsAnOrganisationAndASensibleSeatLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "marketing", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);

        var missing = await PostAsync(SubscribeRoute(product.Id), new { }, adminToken, ct);
        var empty = await PostAsync(SubscribeRoute(product.Id), new { tenantId = Guid.Empty }, adminToken, ct);
        var noSeats = await PostAsync(SubscribeRoute(product.Id), new { tenantId = owner.TenantId, maxSeats = 0 }, adminToken, ct);

        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await missing.Content.ReadAsStringAsync(ct)).ShouldContain("TenantId");
        empty.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        noSeats.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await WithDbAsync(async db => (await db.Subscriptions.CountAsync(ct)).ShouldBe(0));
    }

    [Fact]
    public async Task ONB02_AnUnknownOrganisationASuspendedOneAndAProductThatIsNotAvailableAreRefusedInPlainTerms()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "marketing", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        var draft = await PostAsync(ApplicationAdminEndpoints.Route, new { key = "draft", name = "Draft", jwtAudience = "draft-api" }, adminToken, ct);
        var draftId = (await draft.Content.ReadFromJsonAsync<RegisterApplicationResponse>(Json, ct))!.Id;

        var unknown = await PostAsync(SubscribeRoute(product.Id), new { tenantId = Guid.NewGuid() }, adminToken, ct);
        (await ReadProblemAsync(unknown, ct)).Code.ShouldBe("tenant_not_found");
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var notActive = await PostAsync(SubscribeRoute(draftId), new { tenantId = owner.TenantId }, adminToken, ct);
        notActive.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(notActive, ct)).Code.ShouldBe("application_not_active");

        await WithDbAsync(db => db.Tenants.Where(tenant => tenant.Id == owner.TenantId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(tenant => tenant.Status, TenantStatus.Suspended), ct));
        var suspended = await PostAsync(SubscribeRoute(product.Id), new { tenantId = owner.TenantId }, adminToken, ct);
        suspended.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(suspended, ct)).Code.ShouldBe("tenant_suspended");
    }

    [Fact]
    public async Task ONB02_AnUnknownProductAndTheBuiltInProductAreNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);

        foreach (var applicationId in new[] { Guid.NewGuid(), BuiltInApplication.Id })
        {
            var subscribe = await PostAsync(SubscribeRoute(applicationId), new { tenantId = owner.TenantId }, adminToken, ct);
            subscribe.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await ReadProblemAsync(subscribe, ct)).Code.ShouldBe("application_not_found");

            var testOrganisation = await PostAsync(TestOrganisationRoute(applicationId), new { name = "Sandbox", ownerEmail = OwnerEmail }, adminToken, ct);
            testOrganisation.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await ReadProblemAsync(testOrganisation, ct)).Code.ShouldBe("application_not_found");
        }
    }

    [Fact]
    public async Task ONB02_OnlyAPlatformAdminCanSubscribeAnOrganisationOrCreateATestOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "marketing", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        var ownerToken = (await LoginForTokensAsync(OwnerEmail, ct)).AccessToken;
        var subscribe = new { tenantId = owner.TenantId };
        var sandbox = new { name = "Sandbox", ownerEmail = OwnerEmail };

        (await PostAsync(SubscribeRoute(product.Id), subscribe, ownerToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PostAsync(SubscribeRoute(product.Id), subscribe, null, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await PostAsync(TestOrganisationRoute(product.Id), sandbox, ownerToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PostAsync(TestOrganisationRoute(product.Id), sandbox, null, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await PostAsync(SubscribeRoute(product.Id), subscribe, product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "a product's own token is not an admin's");

        await WithDbAsync(async db =>
        {
            (await db.Subscriptions.CountAsync(ct)).ShouldBe(0);
            (await db.Tenants.CountAsync(tenant => tenant.Name == "Sandbox", ct)).ShouldBe(0, "no test organisation was made");
        });
    }

    [Fact]
    public async Task ONB02_ATestOrganisationIsCreatedForAnExistingPersonAndSubscribedInOneStep()
    {
        var ct = TestContext.Current.CancellationToken;
        var (admin, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "marketing", ct);
        var dev = await SignUpVerifiedAsync("Dev Home", DevEmail, ct);

        var response = await PostAsync(TestOrganisationRoute(product.Id), new { name = "  Marketing sandbox  ", ownerEmail = " Dev@Marketing.TEST ", maxSeats = 3 }, adminToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = (await response.Content.ReadFromJsonAsync<TestOrganisationResponse>(Json, ct))!;
        created.TenantName.ShouldBe("Marketing sandbox");
        created.OwnerEmail.ShouldBe(DevEmail);
        created.TenantId.ShouldNotBe(dev.TenantId, "it is a new organisation, not the person's own");
        created.Subscription.Status.ShouldBe("Active");
        created.Subscription.MaxSeats.ShouldBe(3);
        created.Subscription.ApplicationId.ShouldBe(product.Id);

        await WithDbAsync(async db =>
        {
            var membership = await db.TenantMemberships.AsNoTracking().SingleAsync(candidate => candidate.TenantId == created.TenantId, ct);
            membership.UserId.ShouldBe(dev.UserId);
            membership.Status.ShouldBe(MembershipStatus.Active);
            (await db.Roles.AsNoTracking().SingleAsync(role => role.TenantId == created.TenantId, ct)).Name.ShouldBe(Role.OwnerName);
            (await db.ApplicationAccess.CountAsync(access => access.TenantId == created.TenantId && access.ApplicationId == product.Id, ct)).ShouldBe(1);

            var createdEntry = await db.AuditEntries.AsNoTracking().SingleAsync(entry => entry.Action == AuditActions.TenantCreated && entry.TenantId == created.TenantId, ct);
            createdEntry.ActorType.ShouldBe(AuditActorType.User);
            createdEntry.ActorId.ShouldBe(admin.UserId);
            var subscribedEntry = await db.AuditEntries.AsNoTracking().SingleAsync(entry => entry.Action == AuditActions.TenantSubscribed && entry.TenantId == created.TenantId, ct);
            subscribedEntry.ActorId.ShouldBe(admin.UserId);
            (await db.WebhookOutbox.CountAsync(message => message.ApplicationId == product.Id, ct)).ShouldBe(1);
        });
    }

    [Fact]
    public async Task ONB02_TheOwnerOfATestOrganisationCanOpenTheProductInIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "marketing", ct);
        await SignUpVerifiedAsync("Dev Home", DevEmail, ct);
        var created = (await (await PostAsync(TestOrganisationRoute(product.Id), new { name = "Marketing sandbox", ownerEmail = DevEmail }, adminToken, ct))
            .Content.ReadFromJsonAsync<TestOrganisationResponse>(Json, ct))!;
        var identityToken = (await LoginForTokensAsync(DevEmail, ct)).AccessToken;

        var exchange = await PostAsync("/api/auth/token/exchange", new { tenantId = created.TenantId, applicationId = product.Id }, identityToken, ct);

        exchange.StatusCode.ShouldBe(HttpStatusCode.OK, "an active membership, an active subscription and a seat are all there");
        (await exchange.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("accessToken").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ONB02_ATestOrganisationForSomeoneWithNoAccountVerifiedEmailOrAccessIsRefusedAndNothingIsCreated()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "marketing", ct);
        await SignUpAsync("Fresh Ltd", "fresh@acme.com", DefaultPassword, ct);
        await SignUpVerifiedAsync("Gone Ltd", "gone@acme.com", ct);
        await SetMembershipStatusesAsync("gone@acme.com", MembershipStatus.Disabled, ct);

        var stranger = await PostAsync(TestOrganisationRoute(product.Id), new { name = "Sandbox", ownerEmail = "nobody@acme.com" }, adminToken, ct);
        var unverified = await PostAsync(TestOrganisationRoute(product.Id), new { name = "Sandbox", ownerEmail = "fresh@acme.com" }, adminToken, ct);
        var disabled = await PostAsync(TestOrganisationRoute(product.Id), new { name = "Sandbox", ownerEmail = "gone@acme.com" }, adminToken, ct);

        stranger.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadProblemAsync(stranger, ct)).Code.ShouldBe("owner_not_found");
        unverified.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(unverified, ct)).Code.ShouldBe("owner_email_not_verified");
        disabled.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(disabled, ct)).Code.ShouldBe("owner_disabled");

        await WithDbAsync(async db =>
        {
            (await db.Tenants.CountAsync(tenant => tenant.Name == "Sandbox", ct)).ShouldBe(0);
            (await db.Subscriptions.CountAsync(ct)).ShouldBe(0);
        });
    }

    [Fact]
    public async Task ONB02_ATestOrganisationForAProductThatIsNotAvailableIsRefusedBeforeAnythingIsCreated()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        await SignUpVerifiedAsync("Dev Home", DevEmail, ct);
        var draft = await PostAsync(ApplicationAdminEndpoints.Route, new { key = "draft", name = "Draft", jwtAudience = "draft-api" }, adminToken, ct);
        var draftId = (await draft.Content.ReadFromJsonAsync<RegisterApplicationResponse>(Json, ct))!.Id;

        var response = await PostAsync(TestOrganisationRoute(draftId), new { name = "Sandbox", ownerEmail = DevEmail }, adminToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("application_not_active");
        await WithDbAsync(async db => (await db.Tenants.CountAsync(tenant => tenant.Name == "Sandbox", ct)).ShouldBe(0));
    }

    [Theory]
    [InlineData("", "dev@marketing.test", 3, "Name")]
    [InlineData("   ", "dev@marketing.test", 3, "Name")]
    [InlineData("Sandbox", "", 3, "OwnerEmail")]
    [InlineData("Sandbox", "dev@marketing.test", 0, "MaxSeats")]
    public async Task ONB02_ATestOrganisationNeedsANameAnOwnerAndASensibleSeatLimit(string name, string ownerEmail, int maxSeats, string field)
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "marketing", ct);
        await SignUpVerifiedAsync("Dev Home", DevEmail, ct);

        var response = await PostAsync(TestOrganisationRoute(product.Id), new { name, ownerEmail, maxSeats }, adminToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(ct)).ShouldContain(field);
        await WithDbAsync(async db => (await db.Tenants.CountAsync(tenant => tenant.Name == name.Trim() && name.Trim() != "", ct)).ShouldBe(0));
    }

    [Fact]
    public async Task ONB02_ATestOrganisationNameThatIsTooLongIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "marketing", ct);
        await SignUpVerifiedAsync("Dev Home", DevEmail, ct);

        var response = await PostAsync(TestOrganisationRoute(product.Id), new { name = new string('a', 300), ownerEmail = DevEmail }, adminToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(ct)).ShouldContain("Name must be at most");
    }

    private static string SubscribeRoute(Guid applicationId) => $"{ApplicationAdminEndpoints.Route}/{applicationId}/subscriptions";

    private static string TestOrganisationRoute(Guid applicationId) => $"{ApplicationAdminEndpoints.Route}/{applicationId}/test-organisations";
}
