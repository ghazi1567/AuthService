using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.BuiltIn;
using AuthService.Api.Features.Entitlements;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AuthService.IntegrationTests.Ent02;

/// <summary>
/// ENT-02 (AUTH-279) Plans: named templates of limit values for one product. A platform admin creates, replaces, activates or deactivates
/// and deletes them through <c>/api/auth/admin/applications/{id}/plans</c>; the product reads its own. Item 10 of the proposal (deleting a
/// plan that was assigned) is completed in ENT-03, where assignment exists.
/// </summary>
public sealed class PlanTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";

    [Fact]
    public async Task ENT02_AnAdminCreatesAPlanWithValuesAndReadsItBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adminToken, product) = await ProductWithLimitsAsync(ct);

        var created = await PostAsync(PlansRoute(product.Id), new
        {
            key = "Growth",
            name = "Growth",
            description = "For growing teams",
            values = new Dictionary<string, long?> { ["contacts.reachable"] = 50_000, ["seats"] = 5, ["journeys.live"] = null },
        }, adminToken, ct);

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var plan = (await created.Content.ReadFromJsonAsync<PlanDto>(Json, ct))!;
        plan.Key.ShouldBe("growth", "plan keys are lowercased like limit keys");
        plan.IsActive.ShouldBeTrue("a new plan is active unless it says otherwise");
        plan.CanDelete.ShouldBeTrue();
        plan.Values.Keys.ShouldBe(["contacts.reachable", "journeys.live", "seats"]);
        plan.Values["contacts.reachable"].ShouldBe(50_000);
        plan.Values["journeys.live"].ShouldBeNull("null is unlimited");

        var read = (await (await GetAsync($"{PlansRoute(product.Id)}/growth", adminToken, ct)).Content.ReadFromJsonAsync<PlanDto>(Json, ct))!;
        read.Description.ShouldBe("For growing teams");
        read.Values.ShouldBe(plan.Values);
        var listed = (await (await GetAsync(PlansRoute(product.Id), adminToken, ct)).Content.ReadPageItemsAsync<PlanDto>(Json, ct))!;
        listed.ShouldHaveSingleItem().Values["seats"].ShouldBe(5);
        await WithDbAsync(async db =>
            (await db.AuditEntries.CountAsync(entry => entry.Action == AuditActions.PlanCreated && entry.Detail == "marketing.growth", ct)).ShouldBe(1));
    }

    [Fact]
    public async Task ENT02_APlanThatSetsNothingIsAllowedAndALimitItLeavesOutKeepsItsDefault()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adminToken, product) = await ProductWithLimitsAsync(ct);

        var created = await PostAsync(PlansRoute(product.Id), new { key = "free", name = "Free", values = new { } }, adminToken, ct);

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await created.Content.ReadFromJsonAsync<PlanDto>(Json, ct))!.Values.ShouldBeEmpty();
    }

    [Fact]
    public async Task ENT02_ReplacingAPlanReplacesItsWholeSetOfValues()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adminToken, product) = await ProductWithLimitsAsync(ct);
        await PostAsync(PlansRoute(product.Id), new { key = "growth", name = "Growth",
            values = new Dictionary<string, long?> { ["contacts.reachable"] = 50_000, ["seats"] = 5 } }, adminToken, ct);

        var replaced = await PutAsync($"{PlansRoute(product.Id)}/growth", new { name = "Growth 2026",
            values = new Dictionary<string, long?> { ["contacts.reachable"] = 60_000 } }, adminToken, ct);

        replaced.StatusCode.ShouldBe(HttpStatusCode.OK);
        var plan = (await replaced.Content.ReadFromJsonAsync<PlanDto>(Json, ct))!;
        plan.Name.ShouldBe("Growth 2026");
        plan.Description.ShouldBeNull("a left-out description is cleared");
        plan.Values.ShouldBe(new Dictionary<string, long?> { ["contacts.reachable"] = 60_000 }, "seats was not sent, so it falls back to its default");
        await WithDbAsync(async db => (await db.PlanValues.CountAsync(ct)).ShouldBe(1));
    }

    [Fact]
    public async Task ENT02_ReplacingAPlanWithTheSameContentLeavesNoAuditEntry()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adminToken, product) = await ProductWithLimitsAsync(ct);
        var body = new { name = "Growth", values = new Dictionary<string, long?> { ["seats"] = 5 } };
        await PostAsync(PlansRoute(product.Id), new { key = "growth", body.name, body.values }, adminToken, ct);

        (await PutAsync($"{PlansRoute(product.Id)}/growth", body, adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await WithDbAsync(async db => (await db.AuditEntries.CountAsync(entry => entry.Action == AuditActions.PlanUpdated, ct)).ShouldBe(0));
    }

    [Fact]
    public async Task ENT02_AValueForALimitTheProductDoesNotHaveIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adminToken, product) = await ProductWithLimitsAsync(ct);

        var response = await PostAsync(PlansRoute(product.Id), new { key = "growth", name = "Growth",
            values = new Dictionary<string, long?> { ["seats"] = 5, ["rockets.launched"] = 3 } }, adminToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await ReadProblemAsync(response, ct);
        problem.Code.ShouldBe("limit_unknown");
        problem.Detail!.ShouldContain("rockets.launched");
        await WithDbAsync(async db => (await db.Plans.CountAsync(ct)).ShouldBe(0));
    }

    [Theory]
    [InlineData("""{"key":"growth","name":"Growth","values":{"seats":-1}}""", "Values.seats")]
    [InlineData("""{"key":"growth","name":"Growth","values":{"seats":2.5}}""", "Values.seats")]
    [InlineData("""{"key":"growth","name":"Growth"}""", "Values is required")]
    [InlineData("""{"key":"growth","values":{}}""", "Name is required")]
    [InlineData("""{"key":"Growth Plan","name":"Growth","values":{}}""", "lowercase letters")]
    public async Task ENT02_AMalformedPlanIsRefused(string json, string expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var (adminToken, product) = await ProductWithLimitsAsync(ct);

        var response = await PostAsync(PlansRoute(product.Id), JsonDocument.Parse(json).RootElement, adminToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(ct)).ShouldContain(expected);
    }

    [Fact]
    public async Task ENT02_AnOnOffFeatureInAPlanMustBeZeroOrOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adminToken, product) = await ProductWithLimitsAsync(ct);

        var refused = await PostAsync(PlansRoute(product.Id), new { key = "growth", name = "Growth",
            values = new Dictionary<string, long?> { ["ai.assistant"] = 5 } }, adminToken, ct);
        var allowed = await PostAsync(PlansRoute(product.Id), new { key = "scale", name = "Scale",
            values = new Dictionary<string, long?> { ["ai.assistant"] = 1 } }, adminToken, ct);

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(refused, ct)).Code.ShouldBe("value_invalid");
        allowed.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task ENT02_SeatsCanNeverBeZeroBecauseTheyBecomeTheSeatLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adminToken, product) = await ProductWithLimitsAsync(ct);

        var plan = await PostAsync(PlansRoute(product.Id), new { key = "growth", name = "Growth",
            values = new Dictionary<string, long?> { ["seats"] = 0 } }, adminToken, ct);
        var limit = await PostAsync($"/api/auth/admin/applications/{product.Id}/limits",
            new { limits = new[] { new { key = "seats", label = "Users", unit = "users", defaultValue = 0 } } }, adminToken, ct);

        plan.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(plan, ct)).Code.ShouldBe("value_invalid");
        limit.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await limit.Content.ReadAsStringAsync(ct)).ShouldContain("at least 1, or null for unlimited");
    }

    [Fact]
    public async Task ENT02_APlanKeyIsUniqueWithinItsProductOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adminToken, marketing) = await ProductWithLimitsAsync(ct);
        var support = await CreateActiveProductAsync(adminToken, "support", ct);
        await PostAsync(PlansRoute(marketing.Id), new { key = "growth", name = "Growth", values = new { } }, adminToken, ct);

        var again = await PostAsync(PlansRoute(marketing.Id), new { key = "GROWTH", name = "Growth again", values = new { } }, adminToken, ct);
        var otherProduct = await PostAsync(PlansRoute(support.Id), new { key = "growth", name = "Growth", values = new { } }, adminToken, ct);

        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(again, ct)).Code.ShouldBe("plan_key_taken");
        otherProduct.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task ENT02_AProductCannotHaveMoreThanTwentyPlans()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adminToken, product) = await ProductWithLimitsAsync(ct);
        for (var n = 1; n <= 20; n++)
        {
            (await PostAsync(PlansRoute(product.Id), new { key = $"plan-{n}", name = $"Plan {n}", values = new { } }, adminToken, ct))
                .StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        var oneMore = await PostAsync(PlansRoute(product.Id), new { key = "plan-21", name = "Plan 21", values = new { } }, adminToken, ct);

        oneMore.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(oneMore, ct)).Code.ShouldBe("too_many_plans");
    }

    [Fact]
    public async Task ENT02_APlanCanBeDeactivatedAndActivatedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adminToken, product) = await ProductWithLimitsAsync(ct);
        await PostAsync(PlansRoute(product.Id), new { key = "growth", name = "Growth", values = new { } }, adminToken, ct);

        var off = await PutAsync($"{PlansRoute(product.Id)}/growth/active", new { isActive = false }, adminToken, ct);
        var offAgain = await PutAsync($"{PlansRoute(product.Id)}/growth/active", new { isActive = false }, adminToken, ct);
        var on = await PutAsync($"{PlansRoute(product.Id)}/growth/active", new { isActive = true }, adminToken, ct);

        (await off.Content.ReadFromJsonAsync<PlanDto>(Json, ct))!.IsActive.ShouldBeFalse();
        offAgain.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await on.Content.ReadFromJsonAsync<PlanDto>(Json, ct))!.IsActive.ShouldBeTrue();
        await WithDbAsync(async db =>
        {
            (await db.AuditEntries.CountAsync(entry => entry.Action == AuditActions.PlanDeactivated, ct)).ShouldBe(1, "repeating a state is not audited");
            (await db.AuditEntries.CountAsync(entry => entry.Action == AuditActions.PlanActivated, ct)).ShouldBe(1);
        });
    }

    [Fact]
    public async Task ENT02_APlanNeverAssignedCanBeDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adminToken, product) = await ProductWithLimitsAsync(ct);
        await PostAsync(PlansRoute(product.Id), new { key = "growth", name = "Growth", values = new Dictionary<string, long?> { ["seats"] = 5 } }, adminToken, ct);

        var deleted = await SendAsync(HttpMethod.Delete, $"{PlansRoute(product.Id)}/growth", adminToken, ct);

        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await GetAsync($"{PlansRoute(product.Id)}/growth", adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await WithDbAsync(async db => (await db.PlanValues.CountAsync(ct)).ShouldBe(0, "a plan's values go with it"));
    }

    [Fact]
    public async Task ENT02_APlanThatWasEverAssignedCannotBeDeletedButCanBeDeactivated()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adminToken, product) = await ProductWithLimitsAsync(ct);
        await PostAsync(PlansRoute(product.Id), new { key = "growth", name = "Growth", values = new { } }, adminToken, ct);

        // Assignment arrives with ENT-03; until then the flag it sets is written directly.
        await WithDbAsync(db => db.Plans.ExecuteUpdateAsync(setters => setters.SetProperty(plan => plan.EverAssigned, true), ct));

        var deleted = await SendAsync(HttpMethod.Delete, $"{PlansRoute(product.Id)}/growth", adminToken, ct);
        var deactivated = await PutAsync($"{PlansRoute(product.Id)}/growth/active", new { isActive = false }, adminToken, ct);

        deleted.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(deleted, ct)).Code.ShouldBe("plan_in_use");
        deactivated.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await deactivated.Content.ReadFromJsonAsync<PlanDto>(Json, ct))!.CanDelete.ShouldBeFalse();
    }

    [Fact]
    public async Task ENT02_AnUnknownPlanAndAnUnknownProductAreNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adminToken, product) = await ProductWithLimitsAsync(ct);

        var plan = await PutAsync($"{PlansRoute(product.Id)}/nope", new { name = "Nope", values = new { } }, adminToken, ct);
        (await ReadProblemAsync(plan, ct)).Code.ShouldBe("plan_not_found");
        (await SendAsync(HttpMethod.Delete, $"{PlansRoute(product.Id)}/nope", adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await GetAsync(PlansRoute(Guid.NewGuid()), adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PostAsync(PlansRoute(BuiltInApplication.Id), new { key = "growth", name = "Growth", values = new { } }, adminToken, ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ENT02_OnlyAPlatformAdminManagesPlansAndAProductReadsOnlyItsOwn()
    {
        var ct = TestContext.Current.CancellationToken;
        var (adminToken, marketing) = await ProductWithLimitsAsync(ct);
        var support = await CreateActiveProductAsync(adminToken, "support", ct);
        await PostAsync(PlansRoute(marketing.Id), new { key = "growth", name = "Growth", values = new Dictionary<string, long?> { ["seats"] = 5 } }, adminToken, ct);
        await PostAsync(PlansRoute(support.Id), new { key = "basic", name = "Basic", values = new { } }, adminToken, ct);

        (await PostAsync(PlansRoute(marketing.Id), new { key = "x", name = "X", values = new { } }, marketing.ServiceToken, ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var own = (await (await GetAsync(PlanEndpoints.CurrentApplicationRoute, marketing.ServiceToken, ct)).Content.ReadPageItemsAsync<PlanDto>(Json, ct))!;

        var plan = own.ShouldHaveSingleItem();
        plan.Key.ShouldBe("growth");
        plan.Values["seats"].ShouldBe(5);
        (await GetAsync(PlanEndpoints.CurrentApplicationRoute, adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private async Task<(string AdminToken, TestProduct Product)> ProductWithLimitsAsync(CancellationToken ct)
    {
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "marketing", ct);
        (await PostAsync($"/api/auth/admin/applications/{product.Id}/limits", new
        {
            limits = new object[]
            {
                new { key = "contacts.reachable", label = "Reachable contacts", unit = "contacts", defaultValue = 0 },
                new { key = "seats", label = "Users", unit = "users", defaultValue = 1 },
                new { key = "journeys.live", label = "Live journeys", unit = "journeys", defaultValue = 0 },
                new { key = "ai.assistant", label = "AI assistant", unit = "flag", defaultValue = 0 },
            },
        }, adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        return (adminToken, product);
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string bearerToken, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
        return Client.SendAsync(request, ct);
    }

    private static string PlansRoute(Guid applicationId) => $"/api/auth/admin/applications/{applicationId}/plans";
}
