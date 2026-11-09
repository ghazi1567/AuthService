using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.BuiltIn;
using AuthService.Api.Features.Entitlements;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AuthService.IntegrationTests.Ent01;

/// <summary>
/// ENT-01 (AUTH-278) A product's limit definitions: a platform admin adds and changes them through
/// <c>POST /api/auth/admin/applications/{id}/limits</c>, reads them back, and the product reads its own with its service token.
/// Items 1 and 2 of the plans and entitlements proposal.
/// </summary>
public sealed class LimitDefinitionTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";

    [Fact]
    public async Task ENT01_AnAdminCreatesALimitAndItIsListed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "marketing", ct);

        var response = await PostAsync(LimitsRoute(product.Id), new
        {
            limits = new object[]
            {
                new { key = "contacts.reachable", label = "Reachable contacts", unit = "contacts", defaultValue = 0 },
                new { key = "seats", label = "Users", unit = "users", defaultValue = 1, description = "People with access" },
                new { key = "journeys.live", label = "Live journeys", unit = "journeys", defaultValue = (long?)null },
            },
        }, adminToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var registered = (await response.Content.ReadFromJsonAsync<RegisterLimitsResponse>(Json, ct))!;
        registered.Created.ShouldBe(3);
        registered.Limits.Select(limit => limit.Key).ShouldBe(["contacts.reachable", "journeys.live", "seats"]);

        var listed = (await (await GetAsync(LimitsRoute(product.Id), adminToken, ct)).Content.ReadPageItemsAsync<LimitDefinitionDto>(Json, ct))!;
        listed.Select(limit => limit.Key).ShouldBe(["contacts.reachable", "journeys.live", "seats"]);
        listed[0].ShouldSatisfyAllConditions(
            limit => limit.Label.ShouldBe("Reachable contacts"),
            limit => limit.Unit.ShouldBe("contacts"),
            limit => limit.DefaultValue.ShouldBe(0));
        listed[1].DefaultValue.ShouldBeNull("null is unlimited");
        listed[2].Description.ShouldBe("People with access");
    }

    [Fact]
    public async Task ENT01_SendingAKeyAgainUpdatesItInPlaceAndNeverAddsASecondOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "marketing", ct);
        await PostAsync(LimitsRoute(product.Id), new { limits = new[] { new { key = "storage.assets", label = "Storage", unit = "MB", defaultValue = 0 } } }, adminToken, ct);

        var again = await PostAsync(LimitsRoute(product.Id), new
        {
            limits = new[] { new { key = "Storage.Assets", label = "Asset storage", unit = "MB", defaultValue = 1024 } },
        }, adminToken, ct);

        again.StatusCode.ShouldBe(HttpStatusCode.OK, "nothing was added");
        var body = (await again.Content.ReadFromJsonAsync<RegisterLimitsResponse>(Json, ct))!;
        body.Updated.ShouldBe(1);
        var limit = body.Limits.ShouldHaveSingleItem();
        limit.Key.ShouldBe("storage.assets", "keys are lowercased, so a different case is the same limit");
        limit.Label.ShouldBe("Asset storage");
        limit.DefaultValue.ShouldBe(1024);
    }

    [Fact]
    public async Task ENT01_RepeatingALimitExactlyChangesNothingAndLeavesNoAuditEntry()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "marketing", ct);
        var request = new { limits = new[] { new { key = "apikeys.max", label = "API keys", unit = "keys", defaultValue = 0 } } };
        await PostAsync(LimitsRoute(product.Id), request, adminToken, ct);

        var again = await PostAsync(LimitsRoute(product.Id), request, adminToken, ct);

        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await again.Content.ReadFromJsonAsync<RegisterLimitsResponse>(Json, ct))!.Unchanged.ShouldBe(1);
        await WithDbAsync(async db =>
            (await db.AuditEntries.CountAsync(entry => entry.Action == AuditActions.LimitsRegistered && entry.Detail == "marketing", ct)).ShouldBe(1));
    }

    [Fact]
    public async Task ENT01_ADuplicateKeyInOneRequestIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "marketing", ct);

        var response = await PostAsync(LimitsRoute(product.Id), new
        {
            limits = new[]
            {
                new { key = "seats", label = "Users", unit = "users", defaultValue = 1 },
                new { key = "SEATS", label = "Users again", unit = "users", defaultValue = 2 },
            },
        }, adminToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(ct)).ShouldContain("appears more than once");
        await WithDbAsync(async db => (await db.LimitDefinitions.CountAsync(ct)).ShouldBe(0));
    }

    [Theory]
    [InlineData("Contacts Reachable")]
    [InlineData("contacts reachable")]
    [InlineData("contacts/reachable")]
    [InlineData(".contacts")]
    [InlineData("")]
    public async Task ENT01_AKeyThatBreaksTheRulesIsRefusedWithTheRule(string key)
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "marketing", ct);

        var response = await PostAsync(LimitsRoute(product.Id), new { limits = new[] { new { key, label = "Contacts", unit = "contacts", defaultValue = 0 } } }, adminToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(ct)).ShouldContain("lowercase letters, digits and . _ - characters");
        await WithDbAsync(async db => (await db.LimitDefinitions.CountAsync(ct)).ShouldBe(0));
    }

    [Theory]
    [InlineData("""{"key":"seats","label":"Users"}""", "DefaultValue is required")]
    [InlineData("""{"key":"seats","label":"Users","defaultValue":-1}""", "at least 0")]
    [InlineData("""{"key":"seats","label":"Users","defaultValue":1.5}""", "whole number")]
    [InlineData("""{"key":"seats","label":"Users","defaultValue":"5"}""", "whole number")]
    [InlineData("""{"key":"seats","defaultValue":5}""", "Label is required")]
    [InlineData("""{"key":"ai.assistant","label":"AI assistant","unit":"flag","defaultValue":2}""", "0 (off) or 1 (on)")]
    [InlineData("""{"key":"ai.assistant","label":"AI assistant","unit":"flag","defaultValue":null}""", "0 (off) or 1 (on)")]
    public async Task ENT01_AMissingOrWrongValueIsRefused(string limitJson, string expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "marketing", ct);

        var response = await PostAsync(LimitsRoute(product.Id), JsonDocument.Parse($$"""{"limits":[{{limitJson}}]}""").RootElement, adminToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(ct)).ShouldContain(expected);
    }

    [Fact]
    public async Task ENT01_AnOnOffFeatureIsALimitWithTheUnitFlag()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "marketing", ct);

        var response = await PostAsync(LimitsRoute(product.Id), new { limits = new[] { new { key = "ai.assistant", label = "AI assistant", unit = "flag", defaultValue = 0 } } }, adminToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task ENT01_AProductCannotHaveMoreThanAHundredLimits()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "marketing", ct);
        var hundred = Enumerable.Range(1, 100).Select(n => new { key = $"limit.{n:000}", label = $"Limit {n}", unit = "things", defaultValue = 0 }).ToArray();
        (await PostAsync(LimitsRoute(product.Id), new { limits = hundred }, adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var oneMore = await PostAsync(LimitsRoute(product.Id), new { limits = new[] { new { key = "limit.101", label = "One too many", unit = "things", defaultValue = 0 } } }, adminToken, ct);
        var relabel = await PostAsync(LimitsRoute(product.Id), new { limits = new[] { new { key = "limit.001", label = "Still allowed", unit = "things", defaultValue = 0 } } }, adminToken, ct);

        oneMore.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(oneMore, ct)).Code.ShouldBe("too_many_limits");
        relabel.StatusCode.ShouldBe(HttpStatusCode.OK, "changing a limit already there adds none");
    }

    [Fact]
    public async Task ENT01_AnUnknownProductAndTheBuiltInProductAreNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var body = new { limits = new[] { new { key = "seats", label = "Users", unit = "users", defaultValue = 1 } } };

        (await PostAsync(LimitsRoute(Guid.NewGuid()), body, adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PostAsync(LimitsRoute(BuiltInApplication.Id), body, adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await GetAsync(LimitsRoute(Guid.NewGuid()), adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await GetAsync(LimitsRoute(BuiltInApplication.Id), adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ENT01_OnlyAPlatformAdminCanAddOrReadLimits()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "marketing", ct);
        await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        var personToken = (await LoginForTokensAsync("owner@acme.com", ct)).AccessToken;
        var body = new { limits = new[] { new { key = "seats", label = "Users", unit = "users", defaultValue = 1 } } };

        (await PostAsync(LimitsRoute(product.Id), body, personToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PostAsync(LimitsRoute(product.Id), body, product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await GetAsync(LimitsRoute(product.Id), personToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PostAsync(LimitsRoute(product.Id), body, null, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ENT01_AProductReadsOnlyItsOwnLimitsWithItsServiceToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var marketing = await CreateActiveProductAsync(adminToken, "marketing", ct);
        var support = await CreateActiveProductAsync(adminToken, "support", ct);
        await PostAsync(LimitsRoute(marketing.Id), new { limits = new[] { new { key = "contacts.reachable", label = "Reachable contacts", unit = "contacts", defaultValue = 0 } } }, adminToken, ct);
        await PostAsync(LimitsRoute(support.Id), new { limits = new[] { new { key = "tickets.open", label = "Open tickets", unit = "tickets", defaultValue = 10 } } }, adminToken, ct);

        var own = await GetAsync(LimitDefinitionEndpoints.CurrentApplicationRoute, marketing.ServiceToken, ct);

        own.StatusCode.ShouldBe(HttpStatusCode.OK);
        var items = (await own.Content.ReadPageItemsAsync<LimitDefinitionDto>(Json, ct))!;
        items.ShouldHaveSingleItem().Key.ShouldBe("contacts.reachable");
        (await GetAsync(LimitDefinitionEndpoints.CurrentApplicationRoute, adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden,
            "a person's token is not a service token");
        (await GetAsync(LimitDefinitionEndpoints.CurrentApplicationRoute, null, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ENT01_TheListIsPagedByKey()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "marketing", ct);
        var three = new[] { "c.limit", "a.limit", "b.limit" }.Select(key => new { key, label = key, unit = "things", defaultValue = 0 }).ToArray();
        await PostAsync(LimitsRoute(product.Id), new { limits = three }, adminToken, ct);

        var first = await (await GetAsync($"{LimitsRoute(product.Id)}?limit=2", adminToken, ct)).Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        var next = first.GetProperty("next");
        var second = await (await GetAsync(
            $"{LimitsRoute(product.Id)}?limit=2&after={Uri.EscapeDataString(next.GetProperty("after").GetString()!)}&afterId={next.GetProperty("afterId").GetGuid()}",
            adminToken, ct)).Content.ReadFromJsonAsync<JsonElement>(Json, ct);

        first.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("key").GetString()).ShouldBe(["a.limit", "b.limit"]);
        second.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("key").GetString()).ShouldBe(["c.limit"]);
        second.GetProperty("next").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    private static string LimitsRoute(Guid applicationId) => $"/api/auth/admin/applications/{applicationId}/limits";
}
