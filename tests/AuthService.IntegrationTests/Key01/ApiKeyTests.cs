using System.Net;
using System.Net.Http.Json;
using AuthService.Api.Features.ApiKeys;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.BuiltIn;
using AuthService.Api.Features.Roles;
using AuthService.Api.Features.Signup;
using AuthService.Api.Features.Subscriptions;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AuthService.IntegrationTests.Key01;

/// <summary>
/// KEY-01, KEY-02 and KEY-03 (AUTH-30, 31, 32): a tenant creates, lists, rotates and revokes API keys with a tenant-admin token, and
/// a product validates a presented key with its service token. The rotation grace period has its own test class with a fake clock.
/// Test method names start with their Test Case ID (KEY_TC1..KEY_TC5, KEY_TC8, KEY_TC9) per the workbook convention.
/// </summary>
public sealed class ApiKeyTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";
    private const string OwnerEmail = "owner@acme.com";

    [Fact]
    public async Task KEY_TC1_AKeyScopedToOneApplicationAndOnePermissionIsCreatedAndTheRawKeyIsReturnedOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct, "message.send", "message.read");

        var response = await CreateAsync(t.OwnerToken, "Billing backend", t.Product.Id, ct, ("message.send", "Full"));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.CacheControl?.NoStore.ShouldBeTrue("the response holds a secret and must never be cached");
        var created = (await response.Content.ReadFromJsonAsync<CreatedApiKeyResponse>(Json, ct))!;
        created.RawKey.ShouldStartWith("ak_");
        (created.Key.Name, created.Key.ApplicationId, created.Key.ApplicationKey, created.Key.Status)
            .ShouldBe(("Billing backend", t.Product.Id, "whatsapp-saas", "Active"));
        created.Key.Permissions.ShouldBe([new ApiKeyScopeDto("message.send", "Full")]);
        created.Key.MaskedKey.ShouldNotContain(created.RawKey[^10..]);

        await WithDbAsync(async db =>
        {
            var stored = await db.ApiKeys.AsNoTracking().SingleAsync(ct);
            stored.TenantId.ShouldBe(t.Owner.TenantId);
            stored.CreatedByUserId.ShouldBe(t.Owner.UserId);
            var audit = await db.AuditEntries.AsNoTracking().SingleAsync(entry => entry.Action == AuditActions.ApiKeyCreated, ct);
            (audit.ActorId, audit.TenantId, audit.Detail).ShouldBe((t.Owner.UserId, t.Owner.TenantId, stored.Id.ToString()));
        });
    }

    [Fact]
    public async Task KEY_TC2_FetchingTheKeyAgainNeverReturnsTheRawValueOnlyAMaskedOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct, "message.send");
        var created = await CreateKeyAsync(t.OwnerToken, "Backend", t.Product.Id, ct, ("message.send", "Full"));
        var secret = created.RawKey[(created.RawKey.IndexOf('.') + 1)..];

        var one = await GetAsync(ApiKeyEndpoints.KeyRouteFor(created.Key.Id), t.OwnerToken, ct);
        var list = await GetAsync(ApiKeyEndpoints.Route, t.OwnerToken, ct);

        one.StatusCode.ShouldBe(HttpStatusCode.OK);
        var oneBody = await one.Content.ReadAsStringAsync(ct);
        var listBody = await list.Content.ReadAsStringAsync(ct);
        foreach (var body in new[] { oneBody, listBody })
        {
            body.ShouldNotContain(created.RawKey);
            body.ShouldNotContain(secret);
        }

        var summary = (await list.Content.ReadPageItemsAsync<ApiKeySummary>(Json, ct))!.Single();
        summary.MaskedKey.ShouldBe(created.Key.MaskedKey);
        summary.MaskedKey.ShouldEndWith("...");
        summary.Permissions.ShouldBe(created.Key.Permissions);
    }

    [Fact]
    public async Task KEY_TC3_RevokingAKeyMakesItsNextUseA401()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct, "message.send");
        var created = await CreateKeyAsync(t.OwnerToken, "Backend", t.Product.Id, ct, ("message.send", "Full"));
        (await ValidateAsync(t.Product.ServiceToken, created.RawKey, null, null, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await RevokeAsync(t.OwnerToken, created.Key.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var after = await ValidateAsync(t.Product.ServiceToken, created.RawKey, null, null, ct);
        after.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadProblemAsync(after, ct)).Code.ShouldBe("invalid_api_key");
        var listed = (await (await GetAsync(ApiKeyEndpoints.Route, t.OwnerToken, ct)).Content.ReadPageItemsAsync<ApiKeySummary>(Json, ct))!.Single();
        (listed.Status, listed.RevokedAt.HasValue).ShouldBe(("Revoked", true));
    }

    [Fact]
    public async Task KEY_TC4_AKeyUsedForAnActionOutsideItsDeclaredScopeIsRefusedWith403()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct, "message.send", "message.read");
        var created = await CreateKeyAsync(t.OwnerToken, "Backend", t.Product.Id, ct, ("message.send", "Full"));

        var inside = await ValidateAsync(t.Product.ServiceToken, created.RawKey, "message.send", "Full", ct);
        var outside = await ValidateAsync(t.Product.ServiceToken, created.RawKey, "message.read", null, ct);
        var unknownPermission = await ValidateAsync(t.Product.ServiceToken, created.RawKey, "something.else", null, ct);

        inside.StatusCode.ShouldBe(HttpStatusCode.OK);
        outside.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadProblemAsync(outside, ct)).Code.ShouldBe("outside_key_scope");
        unknownPermission.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task KEY_TC5_ARevokedOrUnknownKeyIsRefusedWith401AndNeverTreatedAsAnonymous()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct, "message.send");
        var created = await CreateKeyAsync(t.OwnerToken, "Backend", t.Product.Id, ct, ("message.send", "Full"));
        await RevokeAsync(t.OwnerToken, created.Key.Id, ct);
        var fabricated = created.RawKey[..(created.RawKey.IndexOf('.') + 1)] + new string('A', 43);

        foreach (var presented in new[] { created.RawKey, fabricated, "ak_" + new string('0', 24) + ".secret", "garbage", "ak_.", "", null })
        {
            var response = await ValidateAsync(t.Product.ServiceToken, presented, "message.send", null, ct);

            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, $"for '{presented}'");
            (await ReadProblemAsync(response, ct)).Code.ShouldBe("invalid_api_key");
        }
    }

    [Fact]
    public async Task KEY_TC8_RevokingAKeyWhileItsOwnerIsLoggedInLeavesTheirSessionsWorking()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct, "message.send");
        var login = await LoginForTokensAsync(OwnerEmail, ct);
        var created = await CreateKeyAsync(t.OwnerToken, "Backend", t.Product.Id, ct, ("message.send", "Full"));

        (await RevokeAsync(t.OwnerToken, created.Key.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await ValidateAsync(t.Product.ServiceToken, created.RawKey, null, null, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await GetAsync("/api/auth/users/me", login.AccessToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RefreshAsync(login.RefreshToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetAsync(ApiKeyEndpoints.Route, t.OwnerToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "and so does the tenant-admin token");
    }

    [Fact]
    public async Task KEY_TC9_TenantATryingToRotateOrRevokeTenantBsKeyGets404AndBsKeyIsUnaffected()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct, "message.send");
        var other = await SignUpVerifiedAsync("Other Co", "boss@other.com", ct);
        (await PostAsync(SubscriptionEndpoints.RouteFor(other.TenantId), new { }, t.Product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var otherToken = await TenantTokenAsync((await LoginForTokensAsync("boss@other.com", ct)).AccessToken, other.TenantId, ct);
        var theirs = await CreateKeyAsync(otherToken, "Theirs", t.Product.Id, ct, ("message.send", "Full"));

        (await RevokeAsync(t.OwnerToken, theirs.Key.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PostAsync(ApiKeyEndpoints.RotateRouteFor(theirs.Key.Id), new { }, t.OwnerToken, ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await GetAsync(ApiKeyEndpoints.KeyRouteFor(theirs.Key.Id), t.OwnerToken, ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await RevokeAsync(t.OwnerToken, Guid.NewGuid(), ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await ValidateAsync(t.Product.ServiceToken, theirs.RawKey, "message.send", null, ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "B's key still works");
        (await ListAsync(t.OwnerToken, ct)).ShouldBeEmpty("A sees none of B's keys");
    }

    // The tests below have no workbook Test Case ID yet: the spec's acceptance criteria cover them.

    [Fact]
    public async Task KEY01_OnlyASaltedHashIsStoredAndTheRawKeyIsNeverLoggedOrAudited()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct, "message.send");
        var first = await CreateKeyAsync(t.OwnerToken, "One", t.Product.Id, ct, ("message.send", "Full"));
        var second = await CreateKeyAsync(t.OwnerToken, "Two", t.Product.Id, ct, ("message.send", "Full"));
        await ValidateAsync(t.Product.ServiceToken, first.RawKey, null, null, ct);
        await ValidateAsync(t.Product.ServiceToken, "ak_" + new string('1', 24) + ".leaked-looking-secret-value", null, null, ct);

        await WithDbAsync(async db =>
        {
            var keys = await db.ApiKeys.AsNoTracking().ToListAsync(ct);
            var everything = System.Text.Json.JsonSerializer.Serialize(new
            {
                keys,
                audit = await db.AuditEntries.AsNoTracking().ToListAsync(ct),
            });
            foreach (var raw in new[] { first.RawKey, second.RawKey })
            {
                everything.ShouldNotContain(raw);
                everything.ShouldNotContain(raw[(raw.IndexOf('.') + 1)..]);
            }

            keys.Select(key => key.SecretSalt).Distinct().Count().ShouldBe(2, "each key has its own salt");
            keys.Select(key => key.SecretHash).Distinct().Count().ShouldBe(2);
        });
        Harness.Logs.Entries.ShouldNotContain(entry => entry.Contains(first.RawKey) || entry.Contains("leaked-looking-secret-value"));
    }

    [Fact]
    public async Task KEY01_AKeyForNoProductIdentifiesTheTenantButGrantsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct, "message.send");
        var created = await CreateKeyAsync(t.OwnerToken, "Identity only", null, ct);

        var plain = await ValidateAsync(t.Product.ServiceToken, created.RawKey, null, null, ct);
        var withAction = await ValidateAsync(t.Product.ServiceToken, created.RawKey, "message.send", null, ct);

        plain.StatusCode.ShouldBe(HttpStatusCode.OK);
        var validated = (await plain.Content.ReadFromJsonAsync<ValidatedApiKey>(Json, ct))!;
        (validated.TenantId, validated.ApplicationId, validated.Permissions.Count).ShouldBe((t.Owner.TenantId, null, 0));
        withAction.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "it can never do more than it was declared to do");
    }

    [Fact]
    public async Task KEY01_ValidationReportsTheTenantAndTheScopeAndHonoursTheLevelAsked()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct, "message.send", "message.read");
        var created = await CreateKeyAsync(t.OwnerToken, "Reader", t.Product.Id, ct, ("message.read", "ReadOnly"), ("message.send", null));

        var response = await ValidateAsync(t.Product.ServiceToken, created.RawKey, "message.read", "ReadOnly", ct);

        var validated = (await response.Content.ReadFromJsonAsync<ValidatedApiKey>(Json, ct))!;
        (validated.TenantId, validated.KeyId, validated.ApplicationId).ShouldBe((t.Owner.TenantId, created.Key.Id, t.Product.Id));
        validated.Permissions.ShouldBe([new ApiKeyScopeDto("message.read", "ReadOnly"), new ApiKeyScopeDto("message.send", "Full")], "a missing level means Full");
        (await ValidateAsync(t.Product.ServiceToken, created.RawKey, "message.read", "Full", ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "ReadOnly is not Full");
        (await ValidateAsync(t.Product.ServiceToken, created.RawKey, "message.read", "CreateRead", ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ValidateAsync(t.Product.ServiceToken, created.RawKey, "MESSAGE.READ", "ReadOnly", ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "the key is matched ignoring case");
        (await ValidateAsync(t.Product.ServiceToken, created.RawKey, "message.read", "nonsense", ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task KEY01_AKeyMadeForOneProductIsRefusedAs401ByAnotherProductAndRevealsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct, "message.send");
        var adminToken = (await LoginForTokensAsync(AdminEmail, ct)).AccessToken;
        var other = await CreateActiveProductAsync(adminToken, "other-product", ct);
        var created = await CreateKeyAsync(t.OwnerToken, "Backend", t.Product.Id, ct, ("message.send", "Full"));

        var response = await ValidateAsync(other.ServiceToken, created.RawKey, "message.send", null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("invalid_api_key");
    }

    [Fact]
    public async Task KEY01_AKeyStopsWorkingWhenItsSubscriptionIsSuspendedOrItsTenantIsSuspended()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct, "message.send");
        var created = await CreateKeyAsync(t.OwnerToken, "Backend", t.Product.Id, ct, ("message.send", "Full"));

        (await PatchJsonAsync(SubscriptionEndpoints.StatusRouteFor(t.Owner.TenantId, t.Product.Id), """{"status": "Suspended"}""", t.Product.ServiceToken, ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var suspendedSubscription = await ValidateAsync(t.Product.ServiceToken, created.RawKey, null, null, ct);
        suspendedSubscription.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadProblemAsync(suspendedSubscription, ct)).Code.ShouldBe("subscription_not_active");

        await PostAsync(SubscriptionEndpoints.RouteFor(t.Owner.TenantId), new { }, t.Product.ServiceToken, ct);
        (await ValidateAsync(t.Product.ServiceToken, created.RawKey, null, null, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await WithDbAsync(db => db.Tenants.ExecuteUpdateAsync(setters => setters.SetProperty(tenant => tenant.Status, TenantStatus.Suspended), ct));
        (await ValidateAsync(t.Product.ServiceToken, created.RawKey, null, null, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task KEY01_ABadScopeIsRejectedWith400AndCreatesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct, "message.send");
        var adminToken = (await LoginForTokensAsync(AdminEmail, ct)).AccessToken;
        var unsubscribed = await CreateActiveProductAsync(adminToken, "unsubscribed-product", ct);

        var cases = new (string? Name, Guid? ApplicationId, (string?, string?)[] Permissions, string Why)[]
        {
            (null, t.Product.Id, [("message.send", "Full")], "no name"),
            ("   ", t.Product.Id, [("message.send", "Full")], "blank name"),
            (new string('n', ApiKeyValidator.MaxNameLength + 1), t.Product.Id, [("message.send", "Full")], "name too long"),
            ("K", t.Product.Id, [], "a product needs at least one permission"),
            ("K", null, [("message.send", "Full")], "permissions need a product"),
            ("K", t.Product.Id, [("message.craete", "Full")], "unknown permission"),
            ("K", t.Product.Id, [("message.send", "Everything")], "bad level"),
            ("K", t.Product.Id, [("message.send", "1")], "numeric level"),
            ("K", t.Product.Id, [("message.send", "Full"), ("Message.Send", "ReadOnly")], "duplicate permission"),
            ("K", t.Product.Id, [(null, "Full")], "missing permission"),
            ("K", unsubscribed.Id, [("thing.use", "Full")], "unsubscribed product"),
            ("K", Guid.NewGuid(), [("thing.use", "Full")], "unknown product"),
            ("K", BuiltInApplication.Id, [("roles.manage", "Full")], "the Auth Service's own product"),
            ("K", Guid.Empty, [("message.send", "Full")], "empty product id"),
        };

        foreach (var (name, applicationId, permissions, why) in cases)
        {
            var response = await PostAsync(ApiKeyEndpoints.Route,
                new CreateApiKeyRequest(name, applicationId, [.. permissions.Select(p => new ApiKeyPermissionInput(p.Item1, p.Item2))]), t.OwnerToken, ct);

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, why);
        }

        await WithDbAsync(async db => (await db.ApiKeys.CountAsync(ct)).ShouldBe(0));
    }

    [Fact]
    public async Task KEY01_AKeyCanNeverHaveMoreThanThePersonWhoCreatesItHas()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct, "message.send", "message.read");
        var manager = await PostAsync(RoleEndpoints.Route, new CreateRoleRequest("Key manager", [
            new RoleGrantInput(BuiltInApplication.Id, "apikeys.manage", "Full"),
            new RoleGrantInput(t.Product.Id, "message.read", "ReadOnly"),
        ]), t.OwnerToken, ct);
        var roleId = (await manager.Content.ReadFromJsonAsync<RoleResponse>(Json, ct))!.Id;
        var member = await AddMemberAsync(t.Owner.TenantId, "member@other.com", ct);
        await PutAsync(RoleEndpoints.MemberRolesRouteFor(member.MembershipId), new AssignRolesRequest([roleId]), t.OwnerToken, ct);
        var memberToken = await TenantTokenAsync(member.IdentityToken, t.Owner.TenantId, ct);

        var within = await CreateAsync(memberToken, "Within", t.Product.Id, ct, ("message.read", "ReadOnly"));
        var higherLevel = await CreateAsync(memberToken, "Higher", t.Product.Id, ct, ("message.read", "Full"));
        var notHeld = await CreateAsync(memberToken, "Not held", t.Product.Id, ct, ("message.send", "ReadOnly"), ("message.read", "ReadOnly"));

        within.StatusCode.ShouldBe(HttpStatusCode.Created);
        higherLevel.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        notHeld.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var problem = await ReadProblemAsync(notHeld, ct);
        problem.Code.ShouldBe("scope_exceeds_permissions");
        problem.Detail!.ShouldContain("message.send");
        (await CreateAsync(t.OwnerToken, "Owner can", t.Product.Id, ct, ("message.send", "Full"), ("message.read", "Full"))).StatusCode
            .ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task KEY01_ListingNeedsApiKeysReadAndChangingNeedsApiKeysManageFull()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct, "message.send");
        var target = await CreateKeyAsync(t.OwnerToken, "Target", t.Product.Id, ct, ("message.send", "Full"));
        var reader = await PostAsync(RoleEndpoints.Route, new CreateRoleRequest("Reader", [new RoleGrantInput(BuiltInApplication.Id, "apikeys.read", "ReadOnly")]), t.OwnerToken, ct);
        var readerRole = (await reader.Content.ReadFromJsonAsync<RoleResponse>(Json, ct))!.Id;
        var readerMember = await AddMemberAsync(t.Owner.TenantId, "reader@other.com", ct);
        var plain = await AddMemberAsync(t.Owner.TenantId, "plain@other.com", ct);
        await PutAsync(RoleEndpoints.MemberRolesRouteFor(readerMember.MembershipId), new AssignRolesRequest([readerRole]), t.OwnerToken, ct);
        var readerToken = await TenantTokenAsync(readerMember.IdentityToken, t.Owner.TenantId, ct);
        var plainToken = await TenantTokenAsync(plain.IdentityToken, t.Owner.TenantId, ct);

        (await GetAsync(ApiKeyEndpoints.Route, readerToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetAsync(ApiKeyEndpoints.Route, plainToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CreateAsync(readerToken, "X", t.Product.Id, ct, ("message.send", "Full"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "reading is not managing");
        (await RevokeAsync(readerToken, target.Key.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PostAsync(ApiKeyEndpoints.RotateRouteFor(target.Key.Id), new { }, readerToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await GetAsync(ApiKeyEndpoints.Route, readerMember.IdentityToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "an identity token has no tenant");
        (await GetAsync(ApiKeyEndpoints.Route, t.Product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await GetAsync(ApiKeyEndpoints.Route, null, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ListAsync(t.OwnerToken, ct)).Single().Status.ShouldBe("Active");
    }

    [Fact]
    public async Task KEY03_OnlyAProductsServiceTokenCanValidateAKey()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct, "message.send");
        var created = await CreateKeyAsync(t.OwnerToken, "Backend", t.Product.Id, ct, ("message.send", "Full"));
        var identity = (await LoginForTokensAsync(OwnerEmail, ct)).AccessToken;

        foreach (var token in new[] { t.OwnerToken, identity, (await LoginForTokensAsync(AdminEmail, ct)).AccessToken })
        {
            (await ValidateAsync(token, created.RawKey, null, null, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        (await ValidateAsync(null, created.RawKey, null, null, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var response = await ValidateAsync(t.Product.ServiceToken, created.RawKey, null, null, ct);
        response.Headers.CacheControl?.NoStore.ShouldBeTrue();
    }

    [Fact]
    public async Task KEY02_RevokingTwiceIsANoOpAndIsAuditedOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct, "message.send");
        var created = await CreateKeyAsync(t.OwnerToken, "Backend", t.Product.Id, ct, ("message.send", "Full"));

        (await RevokeAsync(t.OwnerToken, created.Key.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await RevokeAsync(t.OwnerToken, created.Key.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await WithDbAsync(async db =>
        {
            (await db.AuditEntries.CountAsync(entry => entry.Action == AuditActions.ApiKeyRevoked, ct)).ShouldBe(1);
            var entry = await db.AuditEntries.AsNoTracking().SingleAsync(candidate => candidate.Action == AuditActions.ApiKeyRevoked, ct);
            (entry.ActorId, entry.TenantId, entry.Detail).ShouldBe((t.Owner.UserId, t.Owner.TenantId, created.Key.Id.ToString()));
        });
    }

    // ---- helpers ----

    private sealed record Tenant(TestProduct Product, SignupResponse Owner, string OwnerToken);

    private async Task<Tenant> TenantAsync(CancellationToken ct, params string[] extraPermissions)
    {
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "whatsapp-saas", ct);
        var permissions = extraPermissions.Select(key => new { key, label = key }).ToArray();
        (await PostAsync($"/api/auth/admin/applications/{product.Id}/permissions", new { permissions }, adminToken, ct)).IsSuccessStatusCode.ShouldBeTrue();
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        (await PostAsync(SubscriptionEndpoints.RouteFor(owner.TenantId), new { }, product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        return new Tenant(product, owner, await TenantTokenAsync((await LoginForTokensAsync(OwnerEmail, ct)).AccessToken, owner.TenantId, ct));
    }

    private Task<HttpResponseMessage> CreateAsync(string token, string name, Guid? applicationId, CancellationToken ct, params (string Permission, string? Level)[] permissions) =>
        PostAsync(ApiKeyEndpoints.Route,
            new CreateApiKeyRequest(name, applicationId, [.. permissions.Select(p => new ApiKeyPermissionInput(p.Permission, p.Level))]), token, ct);

    private async Task<CreatedApiKeyResponse> CreateKeyAsync(string token, string name, Guid? applicationId, CancellationToken ct, params (string Permission, string? Level)[] permissions)
    {
        var response = await CreateAsync(token, name, applicationId, ct, permissions);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<CreatedApiKeyResponse>(Json, ct))!;
    }

    private Task<HttpResponseMessage> ValidateAsync(string? serviceToken, string? key, string? permission, string? level, CancellationToken ct) =>
        PostAsync(ApiKeyEndpoints.ValidateRoute, new ValidateApiKeyRequest(key, permission, level), serviceToken, ct);

    private Task<HttpResponseMessage> RevokeAsync(string? token, Guid keyId, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, ApiKeyEndpoints.KeyRouteFor(keyId));
        if (token is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        return Client.SendAsync(request, ct);
    }

    private async Task<List<ApiKeySummary>> ListAsync(string token, CancellationToken ct) =>
        (await (await GetAsync(ApiKeyEndpoints.Route, token, ct)).Content.ReadPageItemsAsync<ApiKeySummary>(Json, ct))!;

    private sealed record Member(Guid UserId, Guid MembershipId, string IdentityToken);

    private async Task<Member> AddMemberAsync(Guid tenantId, string email, CancellationToken ct)
    {
        var person = await SignUpVerifiedAsync("Member Co " + email, email, ct);
        await AddMembershipAsync(person.UserId, tenantId, MembershipStatus.Active, ct);
        Guid membershipId = default;
        await WithDbAsync(async db =>
            membershipId = (await db.TenantMemberships.AsNoTracking()
                .SingleAsync(membership => membership.UserId == person.UserId && membership.TenantId == tenantId, ct)).Id);
        return new Member(person.UserId, membershipId, (await LoginForTokensAsync(email, ct)).AccessToken);
    }
}
