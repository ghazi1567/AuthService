using System.Net;
using System.Net.Http.Json;
using AuthService.Api.Features.Accounts;
using AuthService.Api.Features.CurrentUser;
using AuthService.Api.Features.Tenants;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AuthService.IntegrationTests.Ten09;

/// <summary>
/// TEN-09 (AUTH-118) Create Additional Tenant, through the real <c>POST /api/auth/tenants</c> endpoint with the access
/// token a real login returns.
/// Test method names start with their Test Case ID (AUTH_TC24, AUTH_TC33, AUTH_TC34) per the workbook convention.
/// </summary>
public sealed class CreateTenantTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string Email = "owner@acme.com";

    [Fact]
    public async Task AUTH_TC24_AnExistingUserCreatesAnAdditionalTenantAndBecomesItsOwner()
    {
        var ct = TestContext.Current.CancellationToken;
        var acme = await SignUpVerifiedAsync("Acme Ltd", Email, ct);
        var login = await LoginForTokensAsync(Email, ct);

        var response = await PostAsync(CreateTenantEndpoints.Route, new { tenantName = "  Second Co  " }, login.AccessToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = (await response.Content.ReadFromJsonAsync<CreateTenantResponse>(Json, ct))!;
        created.TenantName.ShouldBe("Second Co", "the name is trimmed");
        created.TenantId.ShouldNotBe(acme.TenantId);

        await WithDbAsync(async db =>
        {
            var tenant = await db.Tenants.SingleAsync(candidate => candidate.Id == created.TenantId, ct);
            tenant.Status.ShouldBe(TenantStatus.Active);

            // The caller is the Owner of the new tenant: an Active membership holding its built-in Owner role.
            var membership = await db.TenantMemberships.SingleAsync(candidate => candidate.Id == created.MembershipId, ct);
            membership.TenantId.ShouldBe(created.TenantId);
            membership.UserId.ShouldBe(acme.UserId);
            membership.Status.ShouldBe(MembershipStatus.Active);

            var ownerRole = await db.Roles.SingleAsync(candidate => candidate.TenantId == created.TenantId, ct);
            ownerRole.Name.ShouldBe(Role.OwnerName);
            ownerRole.IsBuiltIn.ShouldBeTrue();
            (await db.MembershipRoles.SingleAsync(candidate => candidate.MembershipId == membership.Id, ct))
                .RoleId.ShouldBe(ownerRole.Id);

            // The caller's existing membership is exactly as it was.
            var original = await db.TenantMemberships.SingleAsync(candidate => candidate.Id == acme.MembershipId, ct);
            original.TenantId.ShouldBe(acme.TenantId);
            original.Status.ShouldBe(MembershipStatus.Active);
            (await db.MembershipRoles.CountAsync(candidate => candidate.MembershipId == acme.MembershipId, ct)).ShouldBe(1);
        });

        // And the person's own profile now lists both tenants.
        var profile = (await (await GetAsync(CurrentUserEndpoints.Route, login.AccessToken, ct))
            .Content.ReadFromJsonAsync<CurrentUserResponse>(Json, ct))!;
        profile.Memberships.ShouldBe(
        [
            new MembershipSummary(acme.TenantId, "Acme Ltd", MembershipStatus.Active),
            new MembershipSummary(created.TenantId, "Second Co", MembershipStatus.Active),
        ]);
    }

    [Fact]
    public async Task AUTH_TC33_CreatingATenantWithNoIdentityTokenIsRejectedWith401AndCreatesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", Email, ct);
        var before = await CountRowsAsync(ct);

        var response = await PostAsync(CreateTenantEndpoints.Route, new { tenantName = "Second Co" }, bearerToken: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("unauthorized");
        (await CountRowsAsync(ct)).ShouldBe(before);
    }

    [Theory]
    [InlineData("roles")]
    [InlineData("tenant_memberships")]
    [InlineData("membership_roles")]
    public async Task AUTH_TC34_FailureAfterTheTenantRowIsCreatedRollsEverythingBack(string failingTable)
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", Email, ct);
        var login = await LoginForTokensAsync(Email, ct);
        var before = await CountRowsAsync(ct);

        // A real database failure: inserting into one of the tables raises an error after the tenant is inserted.
        await PostgresContainerFixture.ExecuteAsync(
            Database.ConnectionString,
            $"""
            CREATE FUNCTION fail_insert() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'simulated failure'; END $$;
            CREATE TRIGGER fail_insert BEFORE INSERT ON {failingTable} FOR EACH ROW EXECUTE FUNCTION fail_insert();
            """,
            ct);

        var response = await PostAsync(CreateTenantEndpoints.Route, new { tenantName = "Second Co" }, login.AccessToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await CountRowsAsync(ct)).ShouldBe(before, "no orphaned tenant, role or membership may survive");
    }

    // The tests below have no workbook Test Case ID yet: the spec's acceptance criteria cover them.

    [Fact]
    public async Task TEN09_EachCallCreatesAnIndependentTenantWithItsOwnOwnerRole()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", Email, ct);
        var login = await LoginForTokensAsync(Email, ct);

        (await PostAsync(CreateTenantEndpoints.Route, new { tenantName = "Second Co" }, login.AccessToken, ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        (await PostAsync(CreateTenantEndpoints.Route, new { tenantName = "Second Co" }, login.AccessToken, ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        // Names need not be unique; every tenant has its own Owner role and its own membership for the caller.
        (await CountRowsAsync(ct)).ShouldBe(new RowCounts(Tenants: 3, Memberships: 3, Roles: 3, Assignments: 3));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TEN09_Validation_AMissingOrBlankNameIsRejectedWith400(string? tenantName)
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", Email, ct);
        var login = await LoginForTokensAsync(Email, ct);
        var before = await CountRowsAsync(ct);

        var response = await PostAsync(CreateTenantEndpoints.Route, new { tenantName }, login.AccessToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CountRowsAsync(ct)).ShouldBe(before);
    }

    [Fact]
    public async Task TEN09_Validation_ANameLongerThan200CharactersIsRejectedWith400()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", Email, ct);
        var login = await LoginForTokensAsync(Email, ct);

        var response = await PostAsync(CreateTenantEndpoints.Route, new { tenantName = new string('x', 201) }, login.AccessToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CountRowsAsync(ct)).Tenants.ShouldBe(1);
    }

    [Fact]
    public async Task TEN09_AProductTokenWithAnAudienceIsRefusedWith403()
    {
        var ct = TestContext.Current.CancellationToken;
        var account = await SignUpVerifiedAsync("Acme Ltd", Email, ct);
        var productToken = CreateToken(account.UserId, ServiceSigningKey(), TimeSpan.FromMinutes(5), audience: "whatsapp-saas");

        var response = await PostAsync(CreateTenantEndpoints.Route, new { tenantName = "Second Co" }, productToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CountRowsAsync(ct)).Tenants.ShouldBe(1);
    }

    [Fact]
    public async Task TEN09_ATokenForAUserThatNoLongerExistsIsRejectedWith401()
    {
        var ct = TestContext.Current.CancellationToken;
        var token = CreateToken(Guid.NewGuid(), ServiceSigningKey(), TimeSpan.FromMinutes(5));

        var response = await PostAsync(CreateTenantEndpoints.Route, new { tenantName = "Second Co" }, token, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await CountRowsAsync(ct)).Tenants.ShouldBe(0);
    }

    [Fact]
    public async Task TEN09_AnAccountWithEveryMembershipDisabledCannotCreateATenant()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", Email, ct);
        var login = await LoginForTokensAsync(Email, ct);
        await SetMembershipStatusesAsync(Email, MembershipStatus.Disabled, ct);

        // The token is still valid, but creating a tenant would hand a disabled person a fresh Active membership.
        var response = await PostAsync(CreateTenantEndpoints.Route, new { tenantName = "Second Co" }, login.AccessToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("account_disabled");
        (await CountRowsAsync(ct)).Tenants.ShouldBe(1);
    }

    private async Task<RowCounts> CountRowsAsync(CancellationToken ct)
    {
        RowCounts counts = RowCounts.Empty;
        await WithDbAsync(async db => counts = new RowCounts(
            await db.Tenants.CountAsync(ct),
            await db.TenantMemberships.CountAsync(ct),
            await db.Roles.CountAsync(ct),
            await db.MembershipRoles.CountAsync(ct)));
        return counts;
    }

    private sealed record RowCounts(int Tenants, int Memberships, int Roles, int Assignments)
    {
        public static readonly RowCounts Empty = new(0, 0, 0, 0);
    }
}
