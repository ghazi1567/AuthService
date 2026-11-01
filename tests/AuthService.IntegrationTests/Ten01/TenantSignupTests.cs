using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuthService.Api.Features.Signup;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.IntegrationTests.Ten01;

/// <summary>
/// TEN-01 (AUTH-8) Tenant Signup, through the real <c>POST /api/auth/signup</c> endpoint.
/// Test method names start with their Test Case ID (AUTH_TC1..AUTH_TC4, AUTH_TC15) per the workbook convention.
/// </summary>
public sealed class TenantSignupTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string ValidPassword = "correct horse battery";

    [Fact]
    public async Task AUTH_TC1_SignUpWithNewEmailCreatesTenantUserAndOwnerMembership()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await SignUpAsync("Acme Ltd", "owner@acme.com", ValidPassword, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var raw = await response.Content.ReadAsStringAsync(ct);
        raw.Contains("password", StringComparison.OrdinalIgnoreCase).ShouldBeFalse("no credential material may be returned");
        var body = JsonSerializer.Deserialize<SignupResponse>(raw, JsonSerializerOptions.Web)!;

        await using var scope = Harness.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        var tenant = (await db.Tenants.ToListAsync(ct)).ShouldHaveSingleItem();
        var user = (await db.Users.ToListAsync(ct)).ShouldHaveSingleItem();
        var membership = (await db.TenantMemberships.ToListAsync(ct)).ShouldHaveSingleItem();
        var role = (await db.Roles.ToListAsync(ct)).ShouldHaveSingleItem();
        var assignment = (await db.MembershipRoles.ToListAsync(ct)).ShouldHaveSingleItem();

        body.ShouldBe(new SignupResponse(tenant.Id, user.Id, membership.Id, "owner@acme.com"));

        tenant.Name.ShouldBe("Acme Ltd");
        tenant.Status.ShouldBe(TenantStatus.Active);

        user.Email.ShouldBe("owner@acme.com");
        user.EmailVerifiedAt.ShouldBeNull("a new user starts unverified");
        user.PasswordHash.ShouldNotBeNullOrEmpty();
        user.PasswordHash.ShouldNotBe(ValidPassword);
        scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>()
            .VerifyHashedPassword(user, user.PasswordHash, ValidPassword)
            .ShouldBe(PasswordVerificationResult.Success);

        membership.Status.ShouldBe(MembershipStatus.Active);
        membership.TenantId.ShouldBe(tenant.Id);
        membership.UserId.ShouldBe(user.Id);

        role.Name.ShouldBe(Role.OwnerName);
        role.IsBuiltIn.ShouldBeTrue();
        role.TenantId.ShouldBe(tenant.Id);

        assignment.TenantId.ShouldBe(tenant.Id);
        assignment.MembershipId.ShouldBe(membership.Id);
        assignment.RoleId.ShouldBe(role.Id);

        // Signup also issued exactly one verification token and queued exactly one verification email (TEN-11).
        (await db.EmailVerificationTokens.ToListAsync(ct)).ShouldHaveSingleItem().UserId.ShouldBe(user.Id);
        (await db.EmailOutbox.ToListAsync(ct)).ShouldHaveSingleItem().Template.ShouldBe(EmailTemplate.Verification);
    }

    [Fact]
    public async Task AUTH_TC2_SignUpWithEmailAlreadyOnThePlatformIsRejectedWith409()
    {
        var ct = TestContext.Current.CancellationToken;
        (await SignUpAsync("Acme Ltd", "owner@acme.com", ValidPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var second = await SignUpAsync("Other Co", "owner@acme.com", ValidPassword, ct);

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var problem = JsonDocument.Parse(await second.Content.ReadAsStringAsync(ct));
        problem.RootElement.GetProperty("code").GetString().ShouldBe("email_already_registered");

        (await CountRowsAsync(ct)).ShouldBe(new RowCounts(Tenants: 1, Users: 1, Memberships: 1, Roles: 1, Assignments: 1));
    }

    [Theory]
    [InlineData("users")]
    [InlineData("tenant_memberships")]
    [InlineData("membership_roles")]
    public async Task AUTH_TC3_FailureDuringSignupLeavesNoOrphanedRows(string failingTable)
    {
        var ct = TestContext.Current.CancellationToken;

        // A real database failure: inserting into one of the tables raises an error after the tenant is inserted.
        await PostgresContainerFixture.ExecuteAsync(
            Database.ConnectionString,
            $"""
            CREATE FUNCTION fail_insert() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'simulated failure'; END $$;
            CREATE TRIGGER fail_insert BEFORE INSERT ON {failingTable} FOR EACH ROW EXECUTE FUNCTION fail_insert();
            """,
            ct);

        var response = await SignUpAsync("Acme Ltd", "owner@acme.com", ValidPassword, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await CountRowsAsync(ct)).ShouldBe(RowCounts.Empty, "the whole signup must roll back, not just part of it");

        // The verification token and the queued email belong to the same transaction, so they roll back too.
        await WithDbAsync(async db =>
        {
            (await db.EmailVerificationTokens.CountAsync(ct)).ShouldBe(0, "no verification token may survive a failed signup");
            (await db.EmailOutbox.CountAsync(ct)).ShouldBe(0, "no email may be queued for an account that was not created");
        });
    }

    [Fact]
    public async Task AUTH_TC4_OwnerRoleCannotBeEditedOrDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        var token = await TenantTokenAsync((await LoginForTokensAsync("owner@acme.com", ct)).AccessToken, owner.TenantId, ct);
        Guid ownerRoleId = Guid.Empty;
        await WithDbAsync(async db => ownerRoleId = (await db.Roles.AsNoTracking().SingleAsync(role => role.TenantId == owner.TenantId, ct)).Id);

        var edit = await PatchJsonAsync($"/api/auth/roles/{ownerRoleId}", """{"name": "Boss"}""", token, ct);
        var delete = await Client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/auth/roles/{ownerRoleId}")
        {
            Headers = { Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token) },
        }, ct);

        edit.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        delete.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await WithDbAsync(async db => (await db.Roles.AsNoTracking().SingleAsync(role => role.Id == ownerRoleId, ct)).Name.ShouldBe("Owner"));
    }

    [Fact]
    public async Task AUTH_TC15_SignUpWithEmailDifferingOnlyInCaseAndWhitespaceIsRejectedWith409()
    {
        var ct = TestContext.Current.CancellationToken;
        (await SignUpAsync("Acme Ltd", "owner@acme.com", ValidPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var second = await SignUpAsync("Other Co", " Owner@Acme.com ", ValidPassword, ct);

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CountRowsAsync(ct)).ShouldBe(new RowCounts(Tenants: 1, Users: 1, Memberships: 1, Roles: 1, Assignments: 1));

        await using var scope = Harness.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        (await db.Users.SingleAsync(ct)).Email.ShouldBe("owner@acme.com");
    }

    // No workbook Test Case ID yet: the spec does not list validation cases.
    [Theory]
    [InlineData(null, "owner@acme.com", ValidPassword)]
    [InlineData("   ", "owner@acme.com", ValidPassword)]
    [InlineData("Acme Ltd", null, ValidPassword)]
    [InlineData("Acme Ltd", "not-an-email", ValidPassword)]
    [InlineData("Acme Ltd", "owner@acme", ValidPassword)]
    [InlineData("Acme Ltd", "owner@acme.com", null)]
    [InlineData("Acme Ltd", "owner@acme.com", "short")]
    public async Task TEN01_Validation_InvalidRequestsAreRejectedAndCreateNothing(string? tenantName, string? email, string? password)
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await SignUpAsync(tenantName, email, password, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CountRowsAsync(ct)).ShouldBe(RowCounts.Empty);
    }

    // No workbook Test Case ID yet: exercises the unique index when two signups with the same email overlap.
    [Fact]
    public async Task TEN01_Concurrency_SimultaneousSignupsWithTheSameEmailCreateExactlyOneAccount()
    {
        var ct = TestContext.Current.CancellationToken;

        var responses = await Task.WhenAll(
            SignUpAsync("Acme Ltd", "owner@acme.com", ValidPassword, ct),
            SignUpAsync("Other Co", "owner@acme.com", ValidPassword, ct),
            SignUpAsync("Third Co", "OWNER@acme.com", ValidPassword, ct));

        responses.Count(response => response.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        responses.Count(response => response.StatusCode == HttpStatusCode.Conflict).ShouldBe(2);
        (await CountRowsAsync(ct)).ShouldBe(new RowCounts(Tenants: 1, Users: 1, Memberships: 1, Roles: 1, Assignments: 1));
    }

    private async Task<RowCounts> CountRowsAsync(CancellationToken ct)
    {
        await using var scope = Harness.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        return new RowCounts(
            await db.Tenants.CountAsync(ct),
            await db.Users.CountAsync(ct),
            await db.TenantMemberships.CountAsync(ct),
            await db.Roles.CountAsync(ct),
            await db.MembershipRoles.CountAsync(ct));
    }

    private sealed record RowCounts(int Tenants, int Users, int Memberships, int Roles, int Assignments)
    {
        public static readonly RowCounts Empty = new(0, 0, 0, 0, 0);
    }
}
