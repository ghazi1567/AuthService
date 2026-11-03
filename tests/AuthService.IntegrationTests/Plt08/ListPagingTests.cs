using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuthService.Api.Features.Applications;
using AuthService.Api.Features.ApiKeys;
using AuthService.Api.Features.Invitations;
using AuthService.Api.Features.Members;
using AuthService.Api.Features.Roles;
using AuthService.Api.Features.Subscriptions;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AuthService.IntegrationTests.Plt08;

/// <summary>
/// PLT-08 (AUTH-205): every list endpoint takes <c>limit</c> and a cursor and answers <c>{ items, next }</c>. The tests have no workbook
/// Test Case ID: the story's acceptance criteria cover them. Roles and members stand for the lists ordered by name, pending invitations
/// for the newest-first ones; they all go through the same <c>PageQuery</c>.
/// </summary>
public sealed class ListPagingTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string OwnerEmail = "owner@acme.com";

    // ---- A list larger than one page, the last page, an empty list --------------------------------------------------------------

    [Fact]
    public async Task PLT08_ARolesListLargerThanOnePageIsReadPageByPageWithNoRowSkippedOrRepeated()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var names = await AddRolesAsync(t.TenantId, 6, ct);

        var seen = new List<string>();
        var pages = 0;
        PageBody<RoleListItem> page;
        string query = "?limit=2";
        do
        {
            page = await (await GetAsync(RoleEndpoints.Route + query, t.Token, ct)).Content.ReadPageAsync<RoleListItem>(Json, ct);
            pages++;
            page.Items.Count.ShouldBeLessThanOrEqualTo(2);
            seen.AddRange(page.Items.Select(role => role.Name));
            query = page.Next is { } next ? $"?limit=2&after={Uri.EscapeDataString(next.GetProperty("after").GetString()!)}&afterId={next.GetProperty("afterId").GetGuid()}" : "";
        }
        while (page.Next is not null);

        pages.ShouldBe(4, "the Owner role, the Invitee role and six more make eight, two at a time");
        seen.Count.ShouldBe(8);
        seen.Distinct().Count().ShouldBe(8, "no row repeated");
        seen[0].ShouldBe("Owner", "built-in roles lead");
        seen.Skip(1).ShouldBe(names.Append("Invitee").Order(StringComparer.Ordinal).ToList(), "then by name, none skipped");
    }

    [Fact]
    public async Task PLT08_ThePageThatEndsTheListHasNoNextCursor()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        await AddRolesAsync(t.TenantId, 1, ct);

        var all = await (await GetAsync(RoleEndpoints.Route + "?limit=3", t.Token, ct)).Content.ReadPageAsync<RoleListItem>(Json, ct);

        all.Items.Count.ShouldBe(3);
        all.Next.ShouldBeNull("exactly the last page: nothing more to read");
    }

    [Fact]
    public async Task PLT08_AnEmptyListIsAnEmptyPageWithNoCursor()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);

        var invitations = await (await GetAsync(InvitationEndpoints.InviteRoute, t.Token, ct)).Content.ReadPageAsync<PendingInvitation>(Json, ct);
        var keys = await (await GetAsync(ApiKeyEndpoints.Route, t.Token, ct)).Content.ReadPageAsync<ApiKeySummary>(Json, ct);

        invitations.Items.ShouldBeEmpty();
        invitations.Next.ShouldBeNull();
        keys.Items.ShouldBeEmpty();
        keys.Next.ShouldBeNull();
    }

    [Fact]
    public async Task PLT08_WithNoPagingParametersTheFirstFiftyComeBackAndTheResponseSaysMoreExist()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        await AddRolesAsync(t.TenantId, 54, ct);

        var first = await (await GetAsync(RoleEndpoints.Route, t.Token, ct)).Content.ReadPageAsync<RoleListItem>(Json, ct);

        first.Items.Count.ShouldBe(50, "the default limit");
        first.Next.ShouldNotBeNull("six more exist");

        var biggest = await (await GetAsync(RoleEndpoints.Route + "?limit=200", t.Token, ct)).Content.ReadPageAsync<RoleListItem>(Json, ct);
        biggest.Items.Count.ShouldBe(56, "Owner, Invitee and 54 more");
        biggest.Next.ShouldBeNull();
    }

    // ---- Newest first, and the order stays put while rows arrive -----------------------------------------------------------------

    [Fact]
    public async Task PLT08_PendingInvitationsPageNewestFirstAndAnInviteBetweenPagesIsNeitherRepeatedNorCausesASkip()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var emails = new List<string>();
        for (var i = 1; i <= 5; i++)
        {
            emails.Add($"person{i}@acme.com");
            (await PostAsync(InvitationEndpoints.InviteRoute, new InviteRequest(emails[^1], [t.InviteeRole], null), t.Token, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        var first = await (await GetAsync(InvitationEndpoints.InviteRoute + "?limit=2", t.Token, ct)).Content.ReadPageAsync<PendingInvitation>(Json, ct);
        first.Items.Select(item => item.Email).ShouldBe([emails[4], emails[3]], "newest first");

        // Someone is invited while the reader is between pages: they are newer than the cursor, so they belong to a page already passed.
        (await PostAsync(InvitationEndpoints.InviteRoute, new InviteRequest("late@acme.com", [t.InviteeRole], null), t.Token, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var seen = first.Items.Select(item => item.Email).ToList();
        var cursor = first.Next;
        while (cursor is { } next)
        {
            var query = $"?limit=2&before={Uri.EscapeDataString(next.GetProperty("before").GetString()!)}&beforeId={next.GetProperty("beforeId").GetGuid()}";
            var page = await (await GetAsync(InvitationEndpoints.InviteRoute + query, t.Token, ct)).Content.ReadPageAsync<PendingInvitation>(Json, ct);
            seen.AddRange(page.Items.Select(item => item.Email));
            cursor = page.Next;
        }

        seen.ShouldBe([emails[4], emails[3], emails[2], emails[1], emails[0]], "every invitation that existed, once, in order");
    }

    [Fact]
    public async Task PLT08_ARoleAddedBetweenPagesNeverMakesAnExistingRoleVanishOrRepeat()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        await AddRolesAsync(t.TenantId, 5, ct, prefix: "m");

        var first = await (await GetAsync(RoleEndpoints.Route + "?limit=3", t.Token, ct)).Content.ReadPageAsync<RoleListItem>(Json, ct);
        first.Items.Select(role => role.Name).ShouldBe(["Owner", "Invitee", "m1"]);

        // One sorts before the cursor, one after it. Only the one after belongs to a page still to come.
        await AddRolesAsync(t.TenantId, 1, ct, prefix: "a");
        await AddRolesAsync(t.TenantId, 1, ct, prefix: "z");

        var next = first.Next!.Value;
        var second = await (await GetAsync(
            RoleEndpoints.Route + $"?limit=10&after={Uri.EscapeDataString(next.GetProperty("after").GetString()!)}&afterId={next.GetProperty("afterId").GetGuid()}",
            t.Token, ct)).Content.ReadPageAsync<RoleListItem>(Json, ct);

        second.Items.Select(role => role.Name).ShouldBe(["m2", "m3", "m4", "m5", "z1"], "nothing skipped, nothing repeated, the late arrival that sorts after is included");
        second.Next.ShouldBeNull();
    }

    // ---- Lists ordered by something else ---------------------------------------------------------------------------------------

    [Fact]
    public async Task PLT08_MembersPageByEmail()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        foreach (var email in new[] { "carol@acme.com", "alice@acme.com", "bob@acme.com" })
        {
            (await PostAsync(InvitationEndpoints.InviteRoute, new InviteRequest(email, [t.InviteeRole], null), t.Token, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        var first = await (await GetAsync(MemberEndpoints.Route + "?limit=2", t.Token, ct)).Content.ReadPageAsync<MemberItem>(Json, ct);
        first.Items.Select(member => member.Email).ShouldBe(["alice@acme.com", "bob@acme.com"]);

        var next = first.Next!.Value;
        var second = await (await GetAsync(
            MemberEndpoints.Route + $"?limit=2&after={Uri.EscapeDataString(next.GetProperty("after").GetString()!)}&afterId={next.GetProperty("afterId").GetGuid()}",
            t.Token, ct)).Content.ReadPageAsync<MemberItem>(Json, ct);
        second.Items.Select(member => member.Email).ShouldBe(["carol@acme.com", OwnerEmail]);
        second.Next.ShouldBeNull();
        second.Items.Single(member => member.Email == OwnerEmail).Roles.Select(role => role.Name).ShouldBe(["Owner"], "roles load for the page's members");
    }

    [Fact]
    public async Task PLT08_PlatformProductsPageByName()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync("ops@platform.test", ct);
        foreach (var (key, name) in new[] { ("c-app", "Cargo"), ("a-app", "Analytics"), ("b-app", "Billing") })
        {
            (await PostAsync(ApplicationAdminEndpoints.Route, new { key, name, jwtAudience = key + "-api" }, adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        var first = await (await GetAsync(ApplicationAdminEndpoints.Route + "?limit=2", adminToken, ct)).Content.ReadPageAsync<AdminApplicationItem>(Json, ct);
        first.Items.Select(item => item.Name).ShouldBe(["Analytics", "Billing"]);

        var next = first.Next!.Value;
        var second = await (await GetAsync(
            ApplicationAdminEndpoints.Route + $"?limit=2&after={Uri.EscapeDataString(next.GetProperty("after").GetString()!)}&afterId={next.GetProperty("afterId").GetGuid()}",
            adminToken, ct)).Content.ReadPageAsync<AdminApplicationItem>(Json, ct);
        second.Items.Select(item => item.Name).ShouldBe(["Cargo"]);
        second.Next.ShouldBeNull();
    }

    // ---- Bad input and other tenants -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=-5")]
    [InlineData("limit=201")]
    [InlineData("limit=abc")]
    [InlineData("after=Owner")]
    [InlineData("afterId=2a3b4c5d-0000-4000-8000-000000000001")]
    [InlineData("after=Owner&afterId=not-a-guid")]
    [InlineData("before=2026-01-01T00:00:00Z")]
    [InlineData("beforeId=2a3b4c5d-0000-4000-8000-000000000001")]
    [InlineData("before=yesterday&beforeId=2a3b4c5d-0000-4000-8000-000000000001")]
    public async Task PLT08_AnInvalidLimitOrCursorIsRefusedWith400OnEveryList(string query)
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var (_, adminToken) = await CreatePlatformAdminAsync("ops@platform.test", ct);

        // Each list only reads the cursor of its own kind, so a parameter of the other kind is simply not a cursor for it.
        var byName = query.StartsWith("after", StringComparison.Ordinal) || query.StartsWith("limit", StringComparison.Ordinal);
        var byTime = query.StartsWith("before", StringComparison.Ordinal) || query.StartsWith("limit", StringComparison.Ordinal);

        var tenantRoutes = new List<string>();
        if (byName)
        {
            tenantRoutes.AddRange([MemberEndpoints.Route, RoleEndpoints.Route, ListSubscriptionsEndpoints.Route]);
        }

        if (byTime)
        {
            tenantRoutes.AddRange([InvitationEndpoints.InviteRoute, ApiKeyEndpoints.Route]);
        }

        foreach (var route in tenantRoutes)
        {
            var response = await GetAsync($"{route}?{query}", t.Token, ct);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"{route}?{query}");
        }

        if (byName)
        {
            foreach (var route in new[] { ApplicationAdminEndpoints.Route, ListApplicationsEndpoints.Route })
            {
                var token = route == ApplicationAdminEndpoints.Route ? adminToken : (await LoginForTokensAsync(OwnerEmail, ct)).AccessToken;
                (await GetAsync($"{route}?{query}", token, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"{route}?{query}");
            }
        }
    }

    [Fact]
    public async Task PLT08_ACursorFromAnotherTenantNeverShowsThatTenantsRows()
    {
        var ct = TestContext.Current.CancellationToken;
        var acme = await TenantAsync(ct);
        await AddRolesAsync(acme.TenantId, 3, ct, prefix: "zz-acme-");

        var globex = await SignUpVerifiedAsync("Globex Ltd", "boss@globex.com", ct);
        var globexToken = await TenantTokenAsync((await LoginForTokensAsync("boss@globex.com", ct)).AccessToken, globex.TenantId, ct);
        await AddRolesAsync(globex.TenantId, 3, ct, prefix: "aa-globex-");

        var theirs = await (await GetAsync(RoleEndpoints.Route + "?limit=2", globexToken, ct)).Content.ReadPageAsync<RoleListItem>(Json, ct);
        var cursor = theirs.Next!.Value;

        // Acme sends Globex's cursor: it is only a position in the list, so Acme gets Acme's own rows from there on.
        var response = await GetAsync(
            RoleEndpoints.Route + $"?limit=50&after={Uri.EscapeDataString(cursor.GetProperty("after").GetString()!)}&afterId={cursor.GetProperty("afterId").GetGuid()}",
            acme.Token, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(ct);
        body.ShouldNotContain("globex-");
        body.ShouldContain("zz-acme-");
    }

    [Fact]
    public async Task PLT08_ThePagedListsKeepTheirPermissionRules()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);

        foreach (var route in new[] { MemberEndpoints.Route, RoleEndpoints.Route, InvitationEndpoints.InviteRoute, ApiKeyEndpoints.Route, ListSubscriptionsEndpoints.Route })
        {
            (await GetAsync(route + "?limit=1", null, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, route);
        }

        (await GetAsync(ApplicationAdminEndpoints.Route + "?limit=1", (await LoginForTokensAsync(OwnerEmail, ct)).AccessToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ---- Set-up ------------------------------------------------------------------------------------------------------------------------

    private sealed record Tenant(Guid TenantId, string Token, Guid InviteeRole);

    private async Task<Tenant> TenantAsync(CancellationToken ct)
    {
        var account = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        var token = await TenantTokenAsync((await LoginForTokensAsync(OwnerEmail, ct)).AccessToken, account.TenantId, ct);

        // A role to invite people with: an invitation needs at least one.
        var inviteeRole = Guid.CreateVersion7();
        await WithDbAsync(async db =>
        {
            db.Roles.Add(new Role { Id = inviteeRole, TenantId = account.TenantId, Name = "Invitee", IsBuiltIn = false, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(ct);
        });
        return new Tenant(account.TenantId, token, inviteeRole);
    }

    /// <summary>Adds roles straight to the database, named <c>{prefix}1</c>, <c>{prefix}2</c>..., and returns their names.</summary>
    private async Task<List<string>> AddRolesAsync(Guid tenantId, int count, CancellationToken ct, string prefix = "role")
    {
        var names = Enumerable.Range(1, count).Select(number => $"{prefix}{number}").ToList();
        await WithDbAsync(async db =>
        {
            db.Roles.AddRange(names.Select(name => new Role
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                Name = name,
                IsBuiltIn = false,
                CreatedAt = DateTimeOffset.UtcNow,
            }));
            await db.SaveChangesAsync(ct);
        });
        return names;
    }
}
