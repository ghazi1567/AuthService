using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using AuthService.Api.Features.Admin;
using AuthService.Api.Features.Applications;
using AuthService.Api.Features.CurrentUser;
using AuthService.Api.Features.Tenants;
using AuthService.Api.Tokens;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.IntegrationTests.App04;

/// <summary>
/// APP-04 (AUTH-131) Platform-admin Access: an <c>IsPlatformAdmin</c> flag, settable only in the database, that opens everything
/// under <c>/api/auth/admin</c> and nothing else.
/// Test method names start with their Test Case ID (APP_TC9..APP_TC11) per the workbook convention.
/// </summary>
public sealed class PlatformAdminTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";
    private const string UserEmail = "owner@acme.com";

    [Fact]
    public async Task APP_TC9_APlatformAdminRegistersAnApplication()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, token) = await CreatePlatformAdminAsync(AdminEmail, ct);

        var response = await PostAsync(ApplicationAdminEndpoints.Route,
            new { key = "whatsapp-saas", name = "WhatsApp SaaS", jwtAudience = "whatsapp-saas-api" }, token, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task APP_TC10_TheFlagCannotBeSetThroughAnyApi()
    {
        var ct = TestContext.Current.CancellationToken;

        // Signup, with the flag smuggled into the body under both spellings.
        var rawSignup = $$"""
            {"tenantName":"Acme Ltd","email":"{{UserEmail}}","password":"{{DefaultPassword}}","isPlatformAdmin":true,"IsPlatformAdmin":true}
            """;
        var signup = await Client.PostAsync("/api/auth/signup", new StringContent(rawSignup, Encoding.UTF8, "application/json"), ct);
        signup.StatusCode.ShouldBe(HttpStatusCode.Created);
        await VerifyEmailAsync(UserEmail, ct);
        (await FlagAsync(UserEmail)).ShouldBeFalse("a new user is never a platform admin");

        // Every other endpoint that takes a body or a query string from a logged-in user.
        var token = (await LoginForTokensAsync(UserEmail, ct)).AccessToken;
        (await PostAsync(CreateTenantEndpoints.Route, new { tenantName = "Second Co", isPlatformAdmin = true }, token, ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        (await GetAsync($"{CurrentUserEndpoints.Route}?isPlatformAdmin=true", token, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Client.PostAsJsonAsync("/api/auth/login", new { email = UserEmail, password = DefaultPassword, isPlatformAdmin = true }, ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await FlagAsync(UserEmail)).ShouldBeFalse("no request may change the flag");
        (await GetAsync(AuditAdminEndpoints.Route, token, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // And no request or response type even has a field for it.
        typeof(Program).Assembly.GetExportedTypes()
            .Where(type => type.Name.EndsWith("Request", StringComparison.Ordinal) || type.Name.EndsWith("Response", StringComparison.Ordinal))
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance), (type, property) => $"{type.Name}.{property.Name}")
            .ShouldNotContain(name => name.Contains("PlatformAdmin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact(Skip = "Enabled once TOK-01 (token exchange) exists: a platform admin must get 403 asking for a token for a tenant they are not in.")]
    public Task APP_TC11_APlatformAdminCannotGetATokenForATenantTheyAreNotIn() => Task.CompletedTask;

    // The tests below have no workbook Test Case ID yet: the spec's acceptance criteria cover them.

    [Fact]
    public async Task APP04_AnAdminTokenOpensTheAdminEndpoints()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, token) = await CreatePlatformAdminAsync(AdminEmail, ct);

        (await GetAsync(AuditAdminEndpoints.Route, token, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task APP04_APersonWhoIsNotAnAdminIsRefusedWith403AndNothingHappens()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", UserEmail, ct);
        var token = (await LoginForTokensAsync(UserEmail, ct)).AccessToken;
        var keyBefore = ServiceSigningKey().KeyId;

        (await GetAsync(AuditAdminEndpoints.Route, token, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PostAsync(SigningKeyAdminEndpoints.RotateRoute, new { }, token, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        ServiceSigningKey().KeyId.ShouldBe(keyBefore, "a refused request must not rotate the key");
    }

    [Fact]
    public async Task APP04_NoTokenIsRefusedWith401()
    {
        var ct = TestContext.Current.CancellationToken;

        (await GetAsync(AuditAdminEndpoints.Route, bearerToken: null, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await PostAsync(SigningKeyAdminEndpoints.RotateRoute, new { }, bearerToken: null, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task APP04_AProductTokenIsRefusedEvenForAnAdmin()
    {
        var ct = TestContext.Current.CancellationToken;
        var (admin, _) = await CreatePlatformAdminAsync(AdminEmail, ct);

        var productToken = CreateToken(admin.UserId, ServiceSigningKey(), TimeSpan.FromMinutes(5), audience: "whatsapp-saas");

        (await GetAsync(AuditAdminEndpoints.Route, productToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task APP04_GrantingAndRevokingTheFlagTakesEffectAtOnceOnTheSameToken()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Ops", AdminEmail, ct);
        var token = (await LoginForTokensAsync(AdminEmail, ct)).AccessToken;

        (await GetAsync(AuditAdminEndpoints.Route, token, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await SetPlatformAdminAsync(AdminEmail, true, ct);
        (await GetAsync(AuditAdminEndpoints.Route, token, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The token never changed: the flag is read from the database, not carried in the token.
        await SetPlatformAdminAsync(AdminEmail, false, ct);
        (await GetAsync(AuditAdminEndpoints.Route, token, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public void APP04_EveryEndpointUnderTheAdminPathRequiresThePlatformAdminPolicy()
    {
        using var scope = Harness.CreateScope();
        var adminEndpoints = scope.ServiceProvider.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith(AdminEndpoints.BasePath, StringComparison.OrdinalIgnoreCase))
            .ToList();

        adminEndpoints.Count.ShouldBeGreaterThanOrEqualTo(2, "the admin endpoints exist");
        foreach (var endpoint in adminEndpoints)
        {
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .ShouldContain(data => data.Policy == IdentityAuthentication.PlatformAdminPolicyName, endpoint.RoutePattern.RawText);
            endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldBeNull($"{endpoint.RoutePattern.RawText} must not be anonymous");
        }
    }

    [Fact]
    public async Task APP04_TheFlagIsFalseForEveryNewUserAndGrantsNothingOutsideTheAdminArea()
    {
        var ct = TestContext.Current.CancellationToken;
        var (admin, token) = await CreatePlatformAdminAsync(AdminEmail, ct);

        // An admin is an ordinary user everywhere else: their own profile and nothing of anyone else's tenant.
        var profile = (await (await GetAsync(CurrentUserEndpoints.Route, token, ct)).Content.ReadFromJsonAsync<CurrentUserResponse>(Json, ct))!;
        profile.Id.ShouldBe(admin.UserId);
        profile.Memberships.Select(membership => membership.TenantId).ShouldBe([admin.TenantId]);
    }

    private async Task<bool> FlagAsync(string email)
    {
        var flag = false;
        await WithDbAsync(async db => flag = await db.Users.AsNoTracking()
            .Where(user => user.Email == email)
            .Select(user => user.IsPlatformAdmin)
            .SingleAsync(TestContext.Current.CancellationToken));
        return flag;
    }
}
