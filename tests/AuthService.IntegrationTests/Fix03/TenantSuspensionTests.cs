using System.Net;
using System.Net.Http.Json;
using AuthService.Api.Features.ApiKeys;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.Invitations;
using AuthService.Api.Features.Members;
using AuthService.Api.Features.Subscriptions;
using AuthService.Api.Features.Tenants;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using AuthService.IntegrationTests.Tok04;
using Microsoft.EntityFrameworkCore;

namespace AuthService.IntegrationTests.Fix03;

/// <summary>
/// AUTH-268: a platform admin suspends and reactivates an organisation through the API, each change is audited with the reason, and a suspended
/// organisation is refused where it is used, including by tenant-admin tokens that were issued before the suspension. These tests have no
/// workbook ID: they close a known gap (suspension was a hand edit in the database).
/// </summary>
public sealed class TenantSuspensionTests(PostgresContainerFixture postgres) : IsolationTestBase(postgres)
{
    private const string Reason = "non_payment";
    private const string NewPassword = "a-good-password";

    [Fact]
    public async Task AnAdminSuspendsAndReactivatesAndEachChangeIsAuditedWithTheReason()
    {
        var ct = TestContext.Current.CancellationToken;
        var w = await WorldAsync(ct);

        var suspended = await SetStatusAsync(w, w.Acme.Id, "Suspended", Reason, ct);
        suspended.StatusCode.ShouldBe(HttpStatusCode.OK);
        var item = (await suspended.Content.ReadFromJsonAsync<AdminTenantItem>(Json, ct))!;
        (item.Id, item.Name, item.Status, item.MemberCount).ShouldBe((w.Acme.Id, "Acme Ltd", "Suspended", 1));

        var again = await SetStatusAsync(w, w.Acme.Id, "suspended", "abuse", ct);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await again.Content.ReadFromJsonAsync<AdminTenantItem>(Json, ct))!.Status.ShouldBe("Suspended");

        var listed = await ListAsync(w, ct);
        listed.Select(tenant => (tenant.Name, tenant.Status)).ShouldBe([("Acme Ltd", "Suspended"), ("Other Ltd", "Active"), ("Platform Ops", "Active")], "by name, including the admin's own organisation");

        (await SetStatusAsync(w, w.Acme.Id, "Active", "resolved", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ListAsync(w, ct)).Single(tenant => tenant.Name == "Acme Ltd").Status.ShouldBe("Active");

        await WithDbAsync(async db =>
        {
            var entries = await db.AuditEntries.AsNoTracking()
                .Where(entry => entry.Action == AuditActions.TenantSuspended || entry.Action == AuditActions.TenantReactivated)
                .OrderBy(entry => entry.OccurredAt).ToListAsync(ct);
            entries.Select(entry => (entry.Action, entry.ActorId, entry.TenantId, entry.Detail)).ShouldBe(
            [
                (AuditActions.TenantSuspended, (Guid?)w.Admin.UserId, (Guid?)w.Acme.Id, Reason),
                (AuditActions.TenantReactivated, (Guid?)w.Admin.UserId, (Guid?)w.Acme.Id, "resolved"),
            ], "the repeated suspension changed nothing and so wrote nothing");
        });
    }

    [Theory]
    [InlineData(null, "non_payment")]
    [InlineData("", "non_payment")]
    [InlineData("Banned", "non_payment")]
    [InlineData("0", "non_payment")]
    [InlineData("Suspended", null)]
    [InlineData("Suspended", "   ")]
    [InlineData("Suspended", "Unpaid invoice, call Anna on 555 0100")]
    public async Task AStatusThatIsNotActiveOrSuspendedAndAMissingOrFreeTextReasonAreRefusedAndChangeNothing(string? status, string? reason)
    {
        var ct = TestContext.Current.CancellationToken;
        var w = await WorldAsync(ct);

        var response = await PutAsync(TenantAdminEndpoints.StatusRouteFor(w.Acme.Id), new { status, reason }, w.Admin.AccessToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ListAsync(w, ct)).ShouldAllBe(tenant => tenant.Status == "Active");
        await WithDbAsync(async db => (await db.AuditEntries.AnyAsync(entry => entry.Action == AuditActions.TenantSuspended, ct)).ShouldBeFalse());
    }

    [Fact]
    public async Task EveryListedReasonIsAcceptedInAnyCaseAndTheRefusalNamesThem()
    {
        var ct = TestContext.Current.CancellationToken;
        var w = await WorldAsync(ct);

        var refused = await SetStatusAsync(w, w.Acme.Id, "Suspended", "because", ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await refused.Content.ReadAsStringAsync(ct);
        foreach (var reason in TenantStatusValidator.Reasons)
        {
            body.ShouldContain(reason);
        }

        foreach (var reason in TenantStatusValidator.Reasons)
        {
            (await SetStatusAsync(w, w.Acme.Id, "Suspended", reason.ToUpperInvariant(), ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await SetStatusAsync(w, w.Acme.Id, "Active", reason, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await WithDbAsync(async db => (await db.AuditEntries.AsNoTracking().Where(entry => entry.Action == AuditActions.TenantSuspended).Select(entry => entry.Detail).ToListAsync(ct))
            .ShouldBe([.. TenantStatusValidator.Reasons.Select(reason => (string?)reason)], "stored as the code, whatever case was sent"));
    }

    [Fact]
    public async Task AnUnknownOrganisationIs404AndOnlyAPlatformAdminMayAsk()
    {
        var ct = TestContext.Current.CancellationToken;
        var w = await WorldAsync(ct);

        var unknown = await SetStatusAsync(w, Guid.NewGuid(), "Suspended", Reason, ct);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadProblemAsync(unknown, ct)).Code.ShouldBe("tenant_not_found");

        var ownerIdentity = await IdentityTokenAsync("boss@acme.com", ct);
        var path = TenantAdminEndpoints.StatusRouteFor(w.Acme.Id);
        (await PutAsync(path, new { status = "Suspended", reason = Reason }, ownerIdentity, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "a tenant Owner is not a platform admin");
        (await PutAsync(path, new { status = "Suspended", reason = Reason }, w.Acme.OwnerToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PutAsync(path, new { status = "Suspended", reason = Reason }, w.Product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PutAsync(path, new { status = "Suspended", reason = Reason }, null, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await GetAsync(TenantAdminEndpoints.Route, ownerIdentity, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ListAsync(w, ct)).ShouldAllBe(tenant => tenant.Status == "Active");
    }

    [Fact]
    public async Task ATenantAdminTokenIssuedBeforeTheSuspensionStopsWorkingAtOnceAndWorksAgainWhenReactivated()
    {
        var ct = TestContext.Current.CancellationToken;
        var w = await WorldAsync(ct);
        var role = await CreateRoleAsync(w.Acme.OwnerToken, "Support", ct);
        (await GetAsync(MemberEndpoints.Route, w.Acme.OwnerToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await SetStatusAsync(w, w.Acme.Id, "Suspended", Reason, ct);

        foreach (var response in new[]
                 {
                     await GetAsync(MemberEndpoints.Route, w.Acme.OwnerToken, ct),
                     await GetAsync(InvitationEndpoints.InviteRoute, w.Acme.OwnerToken, ct),
                     await GetAsync(ListSubscriptionsEndpoints.Route, w.Acme.OwnerToken, ct),
                     await PostAsync(InvitationEndpoints.InviteRoute, new InviteRequest("new@example.com", [role], []), w.Acme.OwnerToken, ct),
                 })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "the token's authority is withdrawn: trade for a new one, which says why");
            response.Headers.WwwAuthenticate.ToString().ShouldBe("Bearer");
            var problem = await ReadProblemAsync(response, ct);
            problem.Code.ShouldBe("tenant_suspended");
            problem.Title.ShouldBe("Organisation suspended");
        }

        await WithDbAsync(async db => (await db.Users.AnyAsync(user => user.Email == "new@example.com", ct)).ShouldBeFalse("the refused invitation created nothing"));
        (await GetAsync(MemberEndpoints.Route, w.Other.OwnerToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "another organisation is not affected");

        await SetStatusAsync(w, w.Acme.Id, "Active", "resolved", ct);

        (await GetAsync(MemberEndpoints.Route, w.Acme.OwnerToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "the same token, no new sign-in");
    }

    [Fact]
    public async Task ASuspendedOrganisationCannotTradeForATokenOrBeSubscribedOrHaveItsSeatLimitChanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var w = await WorldAsync(ct);
        var identity = await IdentityTokenAsync("boss@acme.com", ct);
        await SetStatusAsync(w, w.Acme.Id, "Suspended", Reason, ct);

        var exchange = await PostAsync("/api/auth/token/exchange", new { tenantId = w.Acme.Id, applicationId = w.Product.Id }, identity, ct);
        exchange.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadProblemAsync(exchange, ct)).Code.ShouldBe("tenant_suspended");

        var subscribe = await PostAsync(SubscriptionEndpoints.RouteFor(w.Acme.Id), new { }, w.Product.ServiceToken, ct);
        subscribe.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(subscribe, ct)).Code.ShouldBe("tenant_suspended");

        var seats = await PatchJsonAsync(SubscriptionEndpoints.SeatsRouteFor(w.Acme.Id, w.Product.Id), """{"maxSeats": 5}""", w.Product.ServiceToken, ct);
        seats.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(seats, ct)).Code.ShouldBe("tenant_suspended");
        await WithDbAsync(async db => (await db.Subscriptions.AsNoTracking().SingleAsync(s => s.TenantId == w.Acme.Id, ct)).MaxSeats.ShouldBeNull("the limit was not changed"));

        await SetStatusAsync(w, w.Acme.Id, "Active", "resolved", ct);

        (await PostAsync("/api/auth/token/exchange", new { tenantId = w.Acme.Id, applicationId = w.Product.Id }, identity, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PatchJsonAsync(SubscriptionEndpoints.SeatsRouteFor(w.Acme.Id, w.Product.Id), """{"maxSeats": 5}""", w.Product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnInvitationCannotBeAcceptedIntoASuspendedOrganisationAndTheLinkSurvivesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var w = await WorldAsync(ct);
        var role = await CreateRoleAsync(w.Acme.OwnerToken, "Support", ct);
        (await PostAsync(InvitationEndpoints.InviteRoute, new InviteRequest("new@example.com", [role], []), w.Acme.OwnerToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var token = await WaitForEmailTokenAsync("new@example.com", "You have been invited to Acme Ltd");
        await SetStatusAsync(w, w.Acme.Id, "Suspended", Reason, ct);

        var refused = await PostAsync(InvitationEndpoints.AcceptRoute, new AcceptInvitationRequest(token, NewPassword), null, ct);

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadProblemAsync(refused, ct)).Code.ShouldBe("tenant_suspended");
        (await LoginAsync("new@example.com", NewPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "no password was set");
        await WithDbAsync(async db => (await db.Invitations.AsNoTracking().SingleAsync(invitation => invitation.TenantId == w.Acme.Id, ct)).UsedAt.ShouldBeNull("the link was not used up"));

        await SetStatusAsync(w, w.Acme.Id, "Active", "resolved", ct);

        (await PostAsync(InvitationEndpoints.AcceptRoute, new AcceptInvitationRequest(token, NewPassword), null, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnApiKeyOfASuspendedOrganisationIsNotAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        var w = await WorldAsync(ct);
        var created = await PostAsync(ApiKeyEndpoints.Route,
            new CreateApiKeyRequest("Backend", w.Product.Id, [new ApiKeyPermissionInput("thing.use", "ReadOnly")]), w.Acme.OwnerToken, ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var rawKey = (await created.Content.ReadFromJsonAsync<CreatedApiKeyResponse>(Json, ct))!.RawKey;

        (await ValidateKeyAsync(w.Product, rawKey, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await SetStatusAsync(w, w.Acme.Id, "Suspended", Reason, ct);
        (await ValidateKeyAsync(w.Product, rawKey, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await SetStatusAsync(w, w.Acme.Id, "Active", "resolved", ct);
        (await ValidateKeyAsync(w.Product, rawKey, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ---- helpers ----

    private sealed record Admin(Guid UserId, string AccessToken);

    private sealed record World(Admin Admin, Tenant Acme, Tenant Other, TestProduct Product);

    private async Task<World> WorldAsync(CancellationToken ct)
    {
        var (account, token) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await ProductAsync(token, "whatsapp-saas", ct);
        var acme = await TenantAsync("Acme Ltd", "boss@acme.com", ct, product);
        var other = await TenantAsync("Other Ltd", "boss@other.com", ct, product);
        return new World(new Admin(account.UserId, token), acme, other, product);
    }

    private Task<HttpResponseMessage> SetStatusAsync(World w, Guid tenantId, string status, string reason, CancellationToken ct) =>
        PutAsync(TenantAdminEndpoints.StatusRouteFor(tenantId), new { status, reason }, w.Admin.AccessToken, ct);

    private async Task<List<AdminTenantItem>> ListAsync(World w, CancellationToken ct)
    {
        var response = await GetAsync(TenantAdminEndpoints.Route, w.Admin.AccessToken, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadPageItemsAsync<AdminTenantItem>(Json, ct))!;
    }

    private Task<HttpResponseMessage> ValidateKeyAsync(TestProduct product, string rawKey, CancellationToken ct) =>
        PostAsync(ApiKeyEndpoints.ValidateRoute, new ValidateApiKeyRequest(rawKey, null, null), product.ServiceToken, ct);
}
