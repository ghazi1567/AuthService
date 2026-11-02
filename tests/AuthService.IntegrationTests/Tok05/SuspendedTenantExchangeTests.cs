using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AuthService.Api.Features.Applications;
using AuthService.Api.Features.BuiltIn;
using AuthService.Api.Features.Subscriptions;
using AuthService.Api.Features.TokenExchange;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AuthService.IntegrationTests.Tok05;

/// <summary>
/// TOK-05 (AUTH-261) An Active member of a suspended organisation is told so (<c>tenant_suspended</c>) by the token exchange and the
/// "my products" read; everyone else is refused exactly as before. The tests have no workbook Test Case ID: the story's acceptance criteria cover them.
/// </summary>
public sealed class SuspendedTenantExchangeTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";
    private const string OwnerEmail = "owner@acme.com";

    [Fact]
    public async Task TOK05_AnActiveMemberOfASuspendedOrganisationIsToldItIsSuspendedForTheConsoleAndForAProduct()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await SetUpAsync(ct);
        await SetTenantStatusAsync(s.TenantId, TenantStatus.Suspended, ct);

        foreach (var application in new[] { BuiltInApplication.Id, s.Product.Id })
        {
            var response = await ExchangeAsync(s.OwnerIdentityToken, s.TenantId, application, ct);

            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await ReadProblemAsync(response, ct)).Code.ShouldBe("tenant_suspended");
        }
    }

    [Fact]
    public async Task TOK05_AnOutsiderStillGetsTheSameRefusalAsForAnInventedOrganisation()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await SetUpAsync(ct);
        await SetTenantStatusAsync(s.TenantId, TenantStatus.Suspended, ct);
        await SignUpVerifiedAsync("Outsider Co", "outsider@other.com", ct);
        var outsider = (await LoginForTokensAsync("outsider@other.com", ct)).AccessToken;

        var suspendedNotMine = await ExchangeAsync(outsider, s.TenantId, s.Product.Id, ct);
        var invented = await ExchangeAsync(outsider, Guid.NewGuid(), s.Product.Id, ct);

        (await ReadProblemAsync(suspendedNotMine, ct)).Code.ShouldBe("exchange_denied");
        (await BodyWithoutTraceAsync(suspendedNotMine, ct)).ShouldBe(await BodyWithoutTraceAsync(invented, ct), "a suspended organisation is not revealed to a non-member");
    }

    [Fact]
    public async Task TOK05_AMemberWhoIsNotActiveGetsTheOrdinaryRefusalEvenInASuspendedOrganisation()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await SetUpAsync(ct);
        var person = await SignUpVerifiedAsync("Member Co", "gone@other.com", ct);
        await AddMembershipAsync(person.UserId, s.TenantId, MembershipStatus.Disabled, ct);
        var token = (await LoginForTokensAsync("gone@other.com", ct)).AccessToken;
        await SetTenantStatusAsync(s.TenantId, TenantStatus.Suspended, ct);

        var response = await ExchangeAsync(token, s.TenantId, s.Product.Id, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("exchange_denied");
    }

    [Fact]
    public async Task TOK05_WhenTheOrganisationIsActiveAgainTheExchangeWorksAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await SetUpAsync(ct);
        await SetTenantStatusAsync(s.TenantId, TenantStatus.Suspended, ct);
        (await ExchangeAsync(s.OwnerIdentityToken, s.TenantId, s.Product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await SetTenantStatusAsync(s.TenantId, TenantStatus.Active, ct);

        (await ExchangeAsync(s.OwnerIdentityToken, s.TenantId, s.Product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TOK05_TheMyProductsReadTellsAnActiveMemberAndNoOneElse()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await SetUpAsync(ct);
        await SetTenantStatusAsync(s.TenantId, TenantStatus.Suspended, ct);
        await SignUpVerifiedAsync("Outsider Co", "outsider@other.com", ct);
        var outsider = (await LoginForTokensAsync("outsider@other.com", ct)).AccessToken;

        var member = await GetAsync($"{MyApplicationsEndpoints.Route}?tenantId={s.TenantId}", s.OwnerIdentityToken, ct);
        var stranger = await GetAsync($"{MyApplicationsEndpoints.Route}?tenantId={s.TenantId}", outsider, ct);

        member.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadProblemAsync(member, ct)).Code.ShouldBe("tenant_suspended");
        stranger.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadProblemAsync(stranger, ct)).Code.ShouldBe("tenant_access_denied");
    }

    private Task SetTenantStatusAsync(Guid tenantId, TenantStatus status, CancellationToken ct) =>
        WithDbAsync(db => db.Tenants.Where(tenant => tenant.Id == tenantId).ExecuteUpdateAsync(setters => setters.SetProperty(tenant => tenant.Status, status), ct));

    private Task<HttpResponseMessage> ExchangeAsync(string identityToken, Guid tenantId, Guid applicationId, CancellationToken ct) =>
        PostAsync(TokenExchangeEndpoints.Route, new { tenantId, applicationId }, identityToken, ct);

    private static async Task<string> BodyWithoutTraceAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>(ct))!;
        body.Remove("traceId");
        return body.ToJsonString();
    }

    private async Task<Setup> SetUpAsync(CancellationToken ct)
    {
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "helpdesk", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        (await PostAsync(SubscriptionEndpoints.RouteFor(owner.TenantId), new SubscribeRequest(null), product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        return new Setup(product, owner.TenantId, (await LoginForTokensAsync(OwnerEmail, ct)).AccessToken);
    }

    private sealed record Setup(TestProduct Product, Guid TenantId, string OwnerIdentityToken);
}
