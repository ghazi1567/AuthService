using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AuthService.Web.Tests;

/// <summary>A clock the test moves by hand.</summary>
public sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

public static class TestTokens
{
    /// <summary>A product token shaped like the API's: the person, the organisation and the permissions as key:Level strings.</summary>
    public static string ProductJwt(Guid userId, Guid tenantId, IEnumerable<string> permissions, int number)
    {
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var list = string.Join(",", permissions.Select(permission => $"\"{permission}\""));
        return $"{Part("""{"alg":"none"}""")}.{Part($$"""{"sub":"{{userId}}","tid":"{{tenantId}}","permissions":[{{list}}],"jti":"p{{number}}"}""")}.psig{number}";
    }

    /// <summary>A token shaped like the API's: three parts, the middle one naming the person. The number keeps each one different.</summary>
    public static string Jwt(Guid userId, int number)
    {
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Part("""{"alg":"none"}""")}.{Part($$"""{"sub":"{{userId}}","jti":"{{number}}"}""")}.signature{number}";
    }
}

/// <summary>
/// A stand-in for the API's sign-in, refresh and sign-out. It behaves like the real one where it matters: each refresh token works
/// once, and using an old one again ends the whole session.
/// </summary>
public sealed class FakeAuthApi : HttpMessageHandler
{
    /// <summary>
    /// The API hands lists out a page at a time (PLT-08). A test says what the list holds as a plain array, and this puts it in the
    /// page the API would return, with nothing after it. A test that wants more pages writes the whole page itself.
    /// </summary>
    private static string Paged(string json) =>
        json.TrimStart().StartsWith('[') ? $"{{\"items\":{json},\"next\":null}}" : json;

    private readonly object _gate = new();
    private int _counter;
    private string? _validRefreshToken;

    public Guid UserId { get; } = Guid.NewGuid();

    public int ExpiresIn { get; set; } = 900;

    /// <summary>When true the API answers a refresh with "invalid refresh token", as if the session was revoked.</summary>
    public bool RefusesRefresh { get; set; }

    /// <summary>When true every call fails as if the API could not be reached.</summary>
    public bool Unreachable { get; set; }

    public TimeSpan RefreshDelay { get; set; }

    public List<string> RefreshTokensSent { get; } = [];

    public List<string> LogoutTokensSent { get; } = [];

    /// <summary>How the fake answers an invitation being accepted; the call is recorded in <see cref="AcceptCalls"/> first.</summary>
    public Func<HttpResponseMessage>? AcceptResponse { get; set; }

    public List<(string? Authorization, string Body)> AcceptCalls { get; } = [];

    /// <summary>The organisations the signed-in person belongs to, as the API's JSON array.</summary>
    public string MembershipsJson { get; set; } = "[]";

    /// <summary>Overrides the answer of <c>GET /api/auth/users/me</c> (an error, for instance).</summary>
    public Func<HttpResponseMessage>? MeResponse { get; set; }

    public List<string?> MeAuthorizations { get; } = [];

    public Func<HttpResponseMessage>? CreateTenantResponse { get; set; }

    public List<(string? Authorization, string Body)> CreateTenantCalls { get; } = [];

    /// <summary>What the tenant-console token allows, as key:Level strings.</summary>
    public string[] ConsolePermissions { get; set; } =
    [
        "members.read:Full", "members.manage:Full", "members.invite:Full", "roles.read:Full", "roles.manage:Full", "roles.assign:Full",
        "access.manage:Full", "apikeys.read:Full", "apikeys.manage:Full", "subscriptions.read:Full", "audit.read:Full",
    ];

    public Func<HttpResponseMessage>? ExchangeResponse { get; set; }

    public List<(string? Authorization, string Body)> ExchangeCalls { get; } = [];

    public List<string> ProductTokensIssued { get; } = [];

    /// <summary>The members of the organisation, as the API's JSON array.</summary>
    public string MembersJson { get; set; } = "[]";

    public Func<HttpResponseMessage>? MembersResponse { get; set; }

    public List<string?> MembersAuthorizations { get; } = [];

    public Func<HttpResponseMessage>? StatusResponse { get; set; }

    public List<(string? Authorization, string Path, string Body)> StatusCalls { get; } = [];

    /// <summary>The pending invitations, as the API's JSON array.</summary>
    public string PendingJson { get; set; } = "[]";

    public Func<HttpResponseMessage>? PendingResponse { get; set; }

    /// <summary>Answers one invitation being sent (given the request body); by default every one is created.</summary>
    public Func<string, HttpResponseMessage>? InviteResponse { get; set; }

    public List<(string? Authorization, string Body)> InviteCalls { get; } = [];

    public Func<HttpResponseMessage>? ResendResponse { get; set; }

    public List<(string? Authorization, string Path)> ResendCalls { get; } = [];

    public string RolesJson { get; set; } = "[]";

    /// <summary>The API keys, as the API's JSON array (masked, never raw).</summary>
    public string ApiKeysJson { get; set; } = "[]";

    public Func<HttpResponseMessage>? ApiKeysResponse { get; set; }

    /// <summary>Answers a key being created; by default it is created and its raw value is <see cref="RawKey"/>.</summary>
    public Func<string, HttpResponseMessage>? CreateKeyResponse { get; set; }

    public string RawKey { get; set; } = "ask_live_9f8e7d6c5b4a39281706f5e4d3c2b1a0";

    public List<(string? Authorization, string Body)> CreateKeyCalls { get; } = [];

    /// <summary>Answers a key being rotated (given the request body); by default the new key's value is <see cref="RotatedRawKey"/> and the old one lives for the grace asked.</summary>
    public Func<string, HttpResponseMessage>? RotateKeyResponse { get; set; }

    /// <summary>The moment the API counts a rotation's grace from; tests pin the web app's clock to the same moment.</summary>
    public DateTimeOffset ApiNow { get; set; } = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    public string RotatedRawKey { get; set; } = "ask_live_aa11bb22cc33dd44ee55ff6600778899";

    public List<(string? Authorization, string Path, string Body)> RotateKeyCalls { get; } = [];

    public Func<HttpResponseMessage>? RevokeKeyResponse { get; set; }

    public List<(string? Authorization, string Path)> RevokeKeyCalls { get; } = [];

    public Func<HttpResponseMessage>? AccessResponse { get; set; }

    public List<(string Method, string? Authorization, string Path)> AccessCalls { get; } = [];

    public Func<HttpResponseMessage>? AssignRolesResponse { get; set; }

    public List<(string? Authorization, string Path, string Body)> AssignRolesCalls { get; } = [];

    /// <summary>What a role can grant, as the API's JSON object.</summary>
    public string CatalogJson { get; set; } = "{\"levels\":[\"ReadOnly\",\"CreateRead\",\"Full\"],\"applications\":[]}";

    public Func<string, HttpResponseMessage>? SaveRoleResponse { get; set; }

    public List<(string Method, string? Authorization, string Path, string Body)> SaveRoleCalls { get; } = [];

    public Func<HttpResponseMessage>? RoleDeleteResponse { get; set; }

    public List<(string? Authorization, string Path)> RoleDeleteCalls { get; } = [];

    public string SubscriptionsJson { get; set; } = "[]";

    public Func<HttpResponseMessage>? SubscriptionsResponse { get; set; }

    /// <summary>What the organisation's profile says its status is: Trial, Active or Suspended.</summary>
    public string TenantStatus { get; set; } = "Active";

    public string TenantName { get; set; } = "Acme Ltd";

    /// <summary>Overrides the answer of <c>GET /api/auth/tenant/me</c> (an error, for instance).</summary>
    public Func<HttpResponseMessage>? TenantProfileResponse { get; set; }

    public List<string?> TenantProfileAuthorizations { get; } = [];

    /// <summary>The audit log, as the API's page object: <c>{"items":[...],"next":null}</c>.</summary>
    public string AuditJson { get; set; } = """{"items":[],"next":null}""";

    /// <summary>Answers a read of the audit log (given its query string); by default <see cref="AuditJson"/>.</summary>
    public Func<string, HttpResponseMessage>? AuditResponse { get; set; }

    public List<(string? Authorization, string Query)> AuditCalls { get; } = [];

    /// <summary>Whether the API treats the signed-in person as a platform admin. Every call under <c>/api/auth/admin</c> is refused with 403 when not.</summary>
    public bool IsPlatformAdmin { get; set; }

    /// <summary>Overrides the answer of every platform admin call (the sign-in token expiring, the API failing).</summary>
    public Func<string, HttpResponseMessage?>? AdminResponse { get; set; }

    /// <summary>Every call made under <c>/api/auth/admin</c>: the bearer token and the path with its query.</summary>
    public List<(string? Authorization, string PathAndQuery)> AdminCalls { get; } = [];

    /// <summary>The platform audit log, as the API's page object.</summary>
    public string AdminAuditJson { get; set; } = """{"items":[],"next":null}""";

    /// <summary>The webhook outbox, as the API's page object.</summary>
    public string AdminWebhooksJson { get; set; } = """{"items":[],"next":null}""";

    /// <summary>Answers a webhook being sent again; by default it is queued as Pending.</summary>
    public Func<HttpResponseMessage>? WebhookResendResponse { get; set; }

    public List<(string? Authorization, string Path)> WebhookResendCalls { get; } = [];

    /// <summary>The platform's published keys (<c>GET /.well-known/jwks.json</c>), as the API's JSON.</summary>
    public string JwksJson { get; set; } = """{"keys":[{"kty":"EC","crv":"P-256","use":"sig","alg":"ES256","kid":"key-old-0001","x":"x","y":"y"},{"kty":"EC","crv":"P-256","use":"sig","alg":"ES256","kid":"key-next-0002","x":"x","y":"y"}]}""";

    public Func<HttpResponseMessage>? JwksResponse { get; set; }

    /// <summary>Answers an emergency rotation; null (the default) rotates and publishes only the new key.</summary>
    public Func<HttpResponseMessage?>? SigningKeyRotateResponse { get; set; }

    public List<(string? Authorization, string Body)> SigningKeyRotateCalls { get; } = [];

    /// <summary>The products the signed-in person can open in an organisation (<c>GET /api/auth/users/me/applications</c>), as a JSON array.</summary>
    public string MyApplicationsJson { get; set; } = "[]";

    /// <summary>Overrides the answer of the my-products read (given its query string), for an error for instance.</summary>
    public Func<string, HttpResponseMessage?>? MyApplicationsResponse { get; set; }

    public List<(string? Authorization, string Query)> MyApplicationsCalls { get; } = [];

    /// <summary>A product's permission catalog as the API returns it (<c>GET /api/auth/admin/applications/{id}/permissions</c>), as a JSON array.</summary>
    public string AdminPermissionsJson { get; set; } = "[]";

    /// <summary>Answers permissions being added (given the request body); null (the default) reports them all created.</summary>
    public Func<string, HttpResponseMessage?>? AddPermissionsResponse { get; set; }

    public List<(string? Authorization, string Path, string Body)> AddPermissionsCalls { get; } = [];

    /// <summary>Every product as the platform admin's list returns it (<c>GET /api/auth/admin/applications</c>), as a JSON array.</summary>
    public string AdminProductsJson { get; set; } = "[]";

    /// <summary>Answers a product being registered (given the request body); null (the default) registers it, inactive, with <see cref="ProductSecret"/>.</summary>
    public Func<string, HttpResponseMessage?>? RegisterProductResponse { get; set; }

    public List<(string? Authorization, string Body)> RegisterProductCalls { get; } = [];

    /// <summary>Answers a product being made available or withdrawn; null (the default) does it.</summary>
    public Func<string, HttpResponseMessage?>? SetActiveResponse { get; set; }

    public List<(string? Authorization, string Path, string Body)> SetActiveCalls { get; } = [];

    /// <summary>Answers a launch address being set or cleared (given the request body); null (the default) does it.</summary>
    public Func<string, HttpResponseMessage?>? LaunchUrlResponse { get; set; }

    public List<(string? Authorization, string Path, string Body)> LaunchUrlCalls { get; } = [];

    /// <summary>A product's new client secret, as the API returns it once.</summary>
    public string ProductSecret { get; set; } = "cs_live_5b1c9e2f7a4d83606f1e2d3c4b5a6978";

    /// <summary>Answers a client secret being replaced (given the request body); null (the default) replaces it.</summary>
    public Func<string, HttpResponseMessage?>? SecretRotateResponse { get; set; }

    public List<(string? Authorization, string Path, string Body)> SecretRotateCalls { get; } = [];

    /// <summary>The products anyone signed in may list (<c>GET /api/auth/applications</c>), as the API's JSON array.</summary>
    public string ProductsJson { get; set; } = "[]";

    public Func<HttpResponseMessage>? ProductsResponse { get; set; }

    public List<string> AccessTokensIssued { get; } = [];

    public List<string> RefreshTokensIssued { get; } = [];

    /// <summary>The organisation a product token is for (its <c>tid</c>), like the real API's profile read: the token decides, nothing else.</summary>
    private static string TenantOfToken(HttpRequestMessage request)
    {
        var token = request.Headers.Authorization?.Parameter ?? "";
        var parts = token.Split('.');
        if (parts.Length == 3)
        {
            var padded = parts[1].Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
            using var payload = JsonDocument.Parse(Convert.FromBase64String(padded));
            if (payload.RootElement.TryGetProperty("tid", out var tid) && tid.GetString() is { } value)
            {
                return value;
            }
        }

        return "7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11";
    }

    /// <summary>The organisation's name from the memberships the fake serves, or <see cref="TenantName"/> when it isn't among them.</summary>
    private string NameOfTenant(string tenantId)
    {
        using var memberships = JsonDocument.Parse(MembershipsJson);
        foreach (var membership in memberships.RootElement.EnumerateArray())
        {
            if (string.Equals(membership.GetProperty("tenantId").GetString(), tenantId, StringComparison.OrdinalIgnoreCase)
                && TenantName == "Acme Ltd")
            {
                return membership.GetProperty("tenantName").GetString() ?? TenantName;
            }
        }

        return TenantName;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // A real API answers later, not instantly: pages must cope with the render that happens while they wait.
        await Task.Yield();
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
        using var json = JsonDocument.Parse(body);

        if (Unreachable && path != "/api/auth/login")
        {
            throw new HttpRequestException("the API is down");
        }

        if (path.StartsWith("/api/auth/admin/", StringComparison.Ordinal))
        {
            lock (_gate)
            {
                AdminCalls.Add((request.Headers.Authorization?.ToString(), request.RequestUri.PathAndQuery));
            }

            if (AdminResponse?.Invoke(request.RequestUri.PathAndQuery) is { } overridden)
            {
                return overridden;
            }

            if (!IsPlatformAdmin)
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }

            if (path == "/api/auth/admin/signing-keys/rotate" && request.Method == HttpMethod.Post)
            {
                lock (_gate)
                {
                    SigningKeyRotateCalls.Add((request.Headers.Authorization?.ToString(), body));
                }

                if (SigningKeyRotateResponse?.Invoke() is { } failed)
                {
                    return failed;
                }

                // The new key signs at once and every earlier key leaves the published list, as in the real API.
                JwksJson = """{"keys":[{"kty":"EC","crv":"P-256","use":"sig","alg":"ES256","kid":"key-new-0001","x":"x","y":"y"}]}""";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"keyId":"key-new-0001"}""", Encoding.UTF8, "application/json") };
            }

            if (path == "/api/auth/admin/applications" && request.Method == HttpMethod.Post)
            {
                lock (_gate)
                {
                    RegisterProductCalls.Add((request.Headers.Authorization?.ToString(), body));
                }

                if (RegisterProductResponse?.Invoke(body) is { } rejected)
                {
                    return rejected;
                }

                var key = json.RootElement.TryGetProperty("key", out var keyElement) ? keyElement.GetString() : "new-product";
                var productName = json.RootElement.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : "New product";
                var audience = json.RootElement.TryGetProperty("jwtAudience", out var audienceElement) ? audienceElement.GetString() : "new-api";
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(
                        $$"""{"id":"{{Guid.NewGuid()}}","key":"{{key}}","name":"{{productName}}","jwtAudience":"{{audience}}","isActive":false,"clientId":"new-client","clientSecret":"{{ProductSecret}}","createdAt":"2026-11-02T12:00:00Z"}""",
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            if (path.EndsWith("/permissions", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Paged(AdminPermissionsJson), Encoding.UTF8, "application/json") };
            }

            if (path.EndsWith("/permissions", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                lock (_gate)
                {
                    AddPermissionsCalls.Add((request.Headers.Authorization?.ToString(), path, body));
                }

                if (AddPermissionsResponse?.Invoke(body) is { } refusedAdd)
                {
                    return refusedAdd;
                }

                var count = json.RootElement.GetProperty("permissions").GetArrayLength();
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent($$"""{"applicationId":"{{path.Split('/')[^2]}}","created":{{count}},"updated":0,"unchanged":0,"permissions":[]}""", Encoding.UTF8, "application/json"),
                };
            }

            if (path.EndsWith("/active", StringComparison.Ordinal) && request.Method == HttpMethod.Put)
            {
                lock (_gate)
                {
                    SetActiveCalls.Add((request.Headers.Authorization?.ToString(), path, body));
                }

                return SetActiveResponse?.Invoke(body) ?? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"id":"20000000-0000-4000-8000-000000000001","key":"helpdesk","isActive":true}""", Encoding.UTF8, "application/json"),
                };
            }

            if (path.EndsWith("/launch-url", StringComparison.Ordinal) && request.Method == HttpMethod.Put)
            {
                lock (_gate)
                {
                    LaunchUrlCalls.Add((request.Headers.Authorization?.ToString(), path, body));
                }

                return LaunchUrlResponse?.Invoke(body) ?? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"launchUrl":null}""", Encoding.UTF8, "application/json"),
                };
            }

            if (path.EndsWith("/secret/rotate", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                lock (_gate)
                {
                    SecretRotateCalls.Add((request.Headers.Authorization?.ToString(), path, body));
                }

                if (SecretRotateResponse?.Invoke(body) is { } refused)
                {
                    return refused;
                }

                var grace = json.RootElement.TryGetProperty("graceHours", out var hours) ? hours.GetInt32() : 24;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""{"applicationId":"{{path.Split('/')[^3]}}","clientId":"helpdesk-client","clientSecret":"{{ProductSecret}}","previousSecretsExpireAt":"{{ApiNow.AddHours(grace):O}}"}""",
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            if (path.EndsWith("/resend", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                lock (_gate)
                {
                    WebhookResendCalls.Add((request.Headers.Authorization?.ToString(), path));
                }

                return WebhookResendResponse?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"id":"60000000-0000-4000-8000-000000000001","applicationId":"20000000-0000-4000-8000-000000000001","applicationKey":"helpdesk","eventType":"ApplicationSubscribed","status":"Pending","attempts":0,"lastError":null,"createdAt":"2026-11-02T09:00:00Z","nextAttemptAt":"2026-11-02T10:00:00Z","deadlineAt":"2026-11-03T10:00:00Z","deliveredAt":null}""",
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            var adminJson = path switch
            {
                "/api/auth/admin/audit" => AdminAuditJson,
                "/api/auth/admin/webhooks" => AdminWebhooksJson,
                "/api/auth/admin/applications" => Paged(AdminProductsJson),
                _ => "{}",
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(adminJson, Encoding.UTF8, "application/json") };
        }

        if (path == "/api/auth/apikeys")
        {
            if (request.Method == HttpMethod.Post)
            {
                lock (_gate)
                {
                    CreateKeyCalls.Add((request.Headers.Authorization?.ToString(), body));
                }

                return CreateKeyResponse?.Invoke(body) ?? new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(
                        $$"""{"key":{"id":"90000000-0000-4000-8000-000000000001","name":"x","maskedKey":"ask_live_9f8e…a0","applicationId":null,"applicationKey":null,"permissions":[],"status":"Active","createdAt":"2026-11-02T12:00:00Z","expiresAt":null,"revokedAt":null},"rawKey":"{{RawKey}}"}""",
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            return ApiKeysResponse?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Paged(ApiKeysJson), Encoding.UTF8, "application/json"),
            };
        }

        if (path.StartsWith("/api/auth/apikeys/", StringComparison.Ordinal) && path.EndsWith("/rotate", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
        {
            lock (_gate)
            {
                RotateKeyCalls.Add((request.Headers.Authorization?.ToString(), path, body));
            }

            if (RotateKeyResponse is not null)
            {
                return RotateKeyResponse(body);
            }

            var grace = json.RootElement.TryGetProperty("graceHours", out var hours) ? hours.GetInt32() : 24;
            var previous = grace == 0 ? "null" : $"\"{ApiNow.AddHours(grace):O}\"";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"key":{"id":"90000000-0000-4000-8000-000000000009","name":"CI deploy key","maskedKey":"ask_live_aa11…99","applicationId":null,"applicationKey":null,"permissions":[],"status":"Active","createdAt":"2026-11-02T12:00:00Z","expiresAt":null,"revokedAt":null},"rawKey":"{{RotatedRawKey}}","previousKeyExpiresAt":{{previous}}}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        }

        if (path.StartsWith("/api/auth/apikeys/", StringComparison.Ordinal) && request.Method == HttpMethod.Delete)
        {
            lock (_gate)
            {
                RevokeKeyCalls.Add((request.Headers.Authorization?.ToString(), path));
            }

            return RevokeKeyResponse?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        if ((path == "/api/auth/roles" && request.Method == HttpMethod.Post)
            || (path.StartsWith("/api/auth/roles/", StringComparison.Ordinal) && request.Method == HttpMethod.Patch))
        {
            lock (_gate)
            {
                SaveRoleCalls.Add((request.Method.Method, request.Headers.Authorization?.ToString(), path, body));
            }

            return SaveRoleResponse?.Invoke(body) ?? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"00000000-0000-0000-0000-000000000009\",\"name\":\"x\",\"isBuiltIn\":false,\"permissions\":[],\"createdAt\":\"2026-11-01T10:00:00Z\"}", Encoding.UTF8, "application/json"),
            };
        }

        if (path.StartsWith("/api/auth/roles/", StringComparison.Ordinal) && request.Method == HttpMethod.Delete)
        {
            lock (_gate)
            {
                RoleDeleteCalls.Add((request.Headers.Authorization?.ToString(), path));
            }

            return RoleDeleteResponse?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        if (path.StartsWith("/api/auth/tenants/me/invitations/", StringComparison.Ordinal) && path.EndsWith("/resend", StringComparison.Ordinal))
        {
            lock (_gate)
            {
                ResendCalls.Add((request.Headers.Authorization?.ToString(), path));
            }

            return ResendResponse?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"membershipId\":\"00000000-0000-0000-0000-000000000000\",\"email\":\"x\",\"status\":\"Invited\",\"roles\":[],\"applicationIds\":[],\"expiresAt\":\"2026-11-09T10:00:00Z\"}", Encoding.UTF8, "application/json"),
            };
        }

        if (path.StartsWith("/api/auth/tenants/me/members/", StringComparison.Ordinal) && path.Contains("/applications/", StringComparison.Ordinal))
        {
            lock (_gate)
            {
                AccessCalls.Add((request.Method.Method, request.Headers.Authorization?.ToString(), path));
            }

            return AccessResponse?.Invoke() ?? new HttpResponseMessage(request.Method == HttpMethod.Delete ? HttpStatusCode.NoContent : HttpStatusCode.Created)
            {
                Content = request.Method == HttpMethod.Delete ? null : new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }

        if (path.StartsWith("/api/auth/tenants/me/members/", StringComparison.Ordinal) && path.EndsWith("/roles", StringComparison.Ordinal) && request.Method == HttpMethod.Put)
        {
            lock (_gate)
            {
                AssignRolesCalls.Add((request.Headers.Authorization?.ToString(), path, body));
            }

            return AssignRolesResponse?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"membershipId\":\"00000000-0000-0000-0000-000000000000\",\"roles\":[]}", Encoding.UTF8, "application/json"),
            };
        }

        if (path.StartsWith("/api/auth/tenants/me/members/", StringComparison.Ordinal))
        {
            lock (_gate)
            {
                StatusCalls.Add((request.Headers.Authorization?.ToString(), path, body));
            }

            return StatusResponse?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"membershipId\":\"00000000-0000-0000-0000-000000000000\",\"email\":\"x\",\"status\":\"Active\",\"roles\":[],\"applications\":[],\"createdAt\":\"2026-11-01T10:00:00Z\"}", Encoding.UTF8, "application/json"),
            };
        }

        switch (path)
        {
            case "/api/auth/token/exchange":
                lock (_gate)
                {
                    ExchangeCalls.Add((request.Headers.Authorization?.ToString(), body));
                }

                if (ExchangeResponse is not null)
                {
                    return ExchangeResponse();
                }

                string product;
                lock (_gate)
                {
                    product = TestTokens.ProductJwt(UserId, Guid.Parse(json.RootElement.GetProperty("tenantId").GetString()!), ConsolePermissions, ProductTokensIssued.Count + 1);
                    ProductTokensIssued.Add(product);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""{"accessToken":"{{product}}","tokenType":"Bearer","expiresIn":900}""", Encoding.UTF8, "application/json"),
                };

            case "/api/auth/tenants/me/invitations" when request.Method == HttpMethod.Post:
                lock (_gate)
                {
                    InviteCalls.Add((request.Headers.Authorization?.ToString(), body));
                }

                return InviteResponse?.Invoke(body) ?? new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("{\"membershipId\":\"00000000-0000-0000-0000-000000000001\",\"email\":\"x\",\"status\":\"Invited\",\"roles\":[],\"applicationIds\":[],\"expiresAt\":\"2026-11-09T10:00:00Z\"}", Encoding.UTF8, "application/json"),
                };

            case "/api/auth/tenants/me/invitations":
                return PendingResponse?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Paged(PendingJson), Encoding.UTF8, "application/json"),
                };

            case "/api/auth/tenants/me/permissions":
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(CatalogJson, Encoding.UTF8, "application/json") };

            case "/api/auth/roles":
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Paged(RolesJson), Encoding.UTF8, "application/json") };

            case "/.well-known/jwks.json":
                return JwksResponse?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JwksJson, Encoding.UTF8, "application/json") };

            case "/api/auth/users/me/applications":
                lock (_gate)
                {
                    MyApplicationsCalls.Add((request.Headers.Authorization?.ToString(), request.RequestUri.Query));
                }

                return MyApplicationsResponse?.Invoke(request.RequestUri.Query)
                    ?? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Paged(MyApplicationsJson), Encoding.UTF8, "application/json") };

            case "/api/auth/applications":
                return ProductsResponse?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Paged(ProductsJson), Encoding.UTF8, "application/json") };

            case "/api/auth/tenant/me":
                lock (_gate)
                {
                    TenantProfileAuthorizations.Add(request.Headers.Authorization?.ToString());
                }

                return TenantProfileResponse?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""{"id":"{{TenantOfToken(request)}}","name":"{{NameOfTenant(TenantOfToken(request))}}","status":"{{TenantStatus}}","createdAt":"2026-09-01T10:00:00Z"}""",
                        Encoding.UTF8,
                        "application/json"),
                };

            case "/api/auth/tenants/me/audit":
                lock (_gate)
                {
                    AuditCalls.Add((request.Headers.Authorization?.ToString(), request.RequestUri.Query));
                }

                return AuditResponse?.Invoke(request.RequestUri.Query) ?? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AuditJson, Encoding.UTF8, "application/json") };

            case "/api/auth/tenants/me/subscriptions":
                return SubscriptionsResponse?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Paged(SubscriptionsJson), Encoding.UTF8, "application/json") };

            case "/api/auth/tenants/me/members":
                lock (_gate)
                {
                    MembersAuthorizations.Add(request.Headers.Authorization?.ToString());
                }

                return MembersResponse?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Paged(MembersJson), Encoding.UTF8, "application/json"),
                };

            case "/api/auth/login":
                return Tokens();

            case "/api/auth/refresh":
                var sent = json.RootElement.GetProperty("refreshToken").GetString()!;
                lock (_gate)
                {
                    RefreshTokensSent.Add(sent);
                }

                if (RefreshDelay > TimeSpan.Zero)
                {
                    await Task.Delay(RefreshDelay, cancellationToken);
                }

                lock (_gate)
                {
                    if (RefusesRefresh || sent != _validRefreshToken)
                    {
                        // A reused token ends the session: nothing works afterwards.
                        _validRefreshToken = null;
                        return Problem(HttpStatusCode.Unauthorized, "invalid_refresh_token");
                    }
                }

                return Tokens();

            case "/api/auth/logout":
                lock (_gate)
                {
                    LogoutTokensSent.Add(json.RootElement.GetProperty("refreshToken").GetString()!);
                }

                return new HttpResponseMessage(HttpStatusCode.NoContent);

            case "/api/auth/users/me":
                lock (_gate)
                {
                    MeAuthorizations.Add(request.Headers.Authorization?.ToString());
                }

                return MeResponse?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""{"id":"{{UserId}}","email":"anna@acme.com","displayName":"Anna Kowalski","emailVerified":true,"createdAt":"2026-11-01T10:00:00Z","memberships":{{MembershipsJson}}}""",
                        Encoding.UTF8,
                        "application/json"),
                };

            case "/api/auth/tenants":
                lock (_gate)
                {
                    CreateTenantCalls.Add((request.Headers.Authorization?.ToString(), body));
                }

                return CreateTenantResponse?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.NotFound);

            case "/api/auth/invitations/accept":
                lock (_gate)
                {
                    AcceptCalls.Add((request.Headers.Authorization?.ToString(), body));
                }

                return AcceptResponse?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.NotFound);

            default:
                return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private HttpResponseMessage Tokens()
    {
        string access;
        string refresh;
        lock (_gate)
        {
            _counter++;
            access = TestTokens.Jwt(UserId, _counter);
            refresh = $"refresh-token-{_counter}";
            _validRefreshToken = refresh;
            AccessTokensIssued.Add(access);
            RefreshTokensIssued.Add(refresh);
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"accessToken":"{{access}}","tokenType":"Bearer","expiresIn":{{ExpiresIn}},"refreshToken":"{{refresh}}","memberships":[]}""",
                Encoding.UTF8,
                "application/json"),
        };
    }

    private static HttpResponseMessage Problem(HttpStatusCode status, string code) => new(status)
    {
        Content = new StringContent(
            $$"""{"status":{{(int)status}},"title":"x","detail":"x","code":"{{code}}"}""", Encoding.UTF8, "application/problem+json"),
    };
}

/// <summary>Records every line the app logs, to check that no password, token or session id is ever in one.</summary>
public sealed class CapturingLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => [.. _lines];

    public ILogger CreateLogger(string categoryName) => new Capture(categoryName, _lines);

    public void Dispose()
    {
    }

    private sealed class Capture(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue($"{category}: {formatter(state, exception)} {exception}");
    }
}

/// <summary>Fills in and sends the web app's forms the way a browser would: field names come from the page that was rendered.</summary>
public static partial class WebForms
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Loads the sign-in page at <paramref name="path"/>, fills it in and posts it back to the same address.</summary>
    public static async Task<HttpResponseMessage> PostLoginAsync(
        HttpClient client, string email, string password, bool remember = false, string path = "/login")
    {
        var page = await client.GetStringAsync(path, Token);
        var form = new Dictionary<string, string>();
        foreach (Match input in InputTag().Matches(page))
        {
            var tag = input.Value;
            var name = FieldName(tag);
            if (tag.Contains("id=\"login-email\""))
            {
                form[name] = email;
            }
            else if (tag.Contains("id=\"login-password\""))
            {
                form[name] = password;
            }
            else if (tag.Contains("type=\"checkbox\""))
            {
                if (remember)
                {
                    // A checked box sends its value, or "on" when it has none.
                    form[name] = Regex.Match(tag, "value=\"([^\"]*)\"") is { Success: true } value ? value.Groups[1].Value : "on";
                }
            }
            else if (tag.Contains("type=\"hidden\""))
            {
                form[name] = WebUtility.HtmlDecode(Regex.Match(tag, "value=\"([^\"]*)\"").Groups[1].Value);
            }
        }

        return await client.PostAsync(path, new FormUrlEncodedContent(form), Token);
    }

    /// <summary>Posts the sign-out form found on <paramref name="pagePath"/>, or posts without its anti-forgery token.</summary>
    public static async Task<HttpResponseMessage> PostLogoutAsync(HttpClient client, string pagePath = "/account", bool withToken = true)
    {
        var form = new Dictionary<string, string>();
        if (withToken)
        {
            var page = await client.GetStringAsync(pagePath, Token);
            var logout = Regex.Match(page, """<form[^>]*action="/logout"[^>]*>(?<body>.*?)</form>""", RegexOptions.Singleline);
            logout.Success.ShouldBeTrue("the page has a sign-out form");
            foreach (Match input in InputTag().Matches(logout.Groups["body"].Value))
            {
                if (input.Value.Contains("type=\"hidden\""))
                {
                    form[FieldName(input.Value)] = WebUtility.HtmlDecode(Regex.Match(input.Value, "value=\"([^\"]*)\"").Groups[1].Value);
                }
            }
        }

        return await client.PostAsync("/logout", new FormUrlEncodedContent(form), Token);
    }

    /// <summary>Loads <paramref name="path"/>, fills in the form named <paramref name="formName"/> (text values by input id) and posts it.</summary>
    public static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string path, string formName, IReadOnlyDictionary<string, string> valuesById)
    {
        var page = await client.GetStringAsync(path, Token);
        return await PostFormFromHtmlAsync(client, path, page, formName, valuesById);
    }

    /// <summary>Posts the form named <paramref name="formName"/> found in <paramref name="html"/>, as a browser would.</summary>
    public static async Task<HttpResponseMessage> PostFormFromHtmlAsync(
        HttpClient client, string path, string html, string formName, IReadOnlyDictionary<string, string>? valuesById = null)
    {
        foreach (Match form in Regex.Matches(html, "<form(?<attributes>[^>]*)>(?<body>.*?)</form>", RegexOptions.Singleline))
        {
            // A browser posts to the form's action when it has one, otherwise to the address it is on.
            var action = Regex.Match(form.Groups["attributes"].Value, "\\baction=\"([^\"]*)\"") is { Success: true } found
                ? WebUtility.HtmlDecode(found.Groups[1].Value)
                : path;
            var inputs = InputTag().Matches(form.Groups["body"].Value).Select(match => match.Value).ToList();
            var handler = inputs.FirstOrDefault(tag => FieldName(tag) == "_handler");
            if (handler is null || Regex.Match(handler, "value=\"([^\"]*)\"").Groups[1].Value != formName)
            {
                continue;
            }

            var fields = new Dictionary<string, string>();
            foreach (var tag in inputs)
            {
                var id = Regex.Match(tag, "\\bid=\"([^\"]+)\"").Groups[1].Value;
                if (id.Length > 0 && valuesById is not null && valuesById.TryGetValue(id, out var typed))
                {
                    fields[FieldName(tag)] = typed;
                }
                else if (tag.Contains("type=\"hidden\""))
                {
                    fields[FieldName(tag)] = WebUtility.HtmlDecode(Regex.Match(tag, "value=\"([^\"]*)\"").Groups[1].Value);
                }
                else if (!tag.Contains("type=\"checkbox\""))
                {
                    // A text box nobody typed in still sends its (empty) value.
                    fields[FieldName(tag)] = "";
                }
            }

            return await client.PostAsync(action, new FormUrlEncodedContent(fields), Token);
        }

        throw new InvalidOperationException($"The page has no form named \"{formName}\".");
    }

    /// <summary>Posts the plain form whose action contains <paramref name="action"/>, with every hidden field it carries (its anti-forgery token, the status).</summary>
    public static async Task<HttpResponseMessage> PostPlainFormAsync(HttpClient client, string html, string action)
    {
        foreach (Match form in Regex.Matches(html, "<form(?<attributes>[^>]*)>(?<body>.*?)</form>", RegexOptions.Singleline))
        {
            var target = Regex.Match(form.Groups["attributes"].Value, "\\baction=\"([^\"]*)\"") is { Success: true } found ? WebUtility.HtmlDecode(found.Groups[1].Value) : "";
            if (!target.Contains(action, StringComparison.Ordinal))
            {
                continue;
            }

            var fields = new Dictionary<string, string>();
            foreach (Match input in InputTag().Matches(form.Groups["body"].Value))
            {
                if (input.Value.Contains("type=\"hidden\""))
                {
                    fields[FieldName(input.Value)] = WebUtility.HtmlDecode(Regex.Match(input.Value, "value=\"([^\"]*)\"").Groups[1].Value);
                }
            }

            return await client.PostAsync(target, new FormUrlEncodedContent(fields), Token);
        }

        throw new InvalidOperationException($"The page has no form posting to \"{action}\".");
    }

    /// <summary>Posts the sign-out form found in <paramref name="html"/> with every hidden field it carries (its anti-forgery token, any return page).</summary>
    public static async Task<HttpResponseMessage> PostLogoutFromHtmlAsync(HttpClient client, string html)
    {
        var logout = Regex.Match(html, "<form[^>]*action=\"/logout\"[^>]*>(?<body>.*?)</form>", RegexOptions.Singleline);
        logout.Success.ShouldBeTrue("the page has a sign-out form");
        var form = new Dictionary<string, string>();
        foreach (Match input in InputTag().Matches(logout.Groups["body"].Value))
        {
            if (input.Value.Contains("type=\"hidden\""))
            {
                form[FieldName(input.Value)] = WebUtility.HtmlDecode(Regex.Match(input.Value, "value=\"([^\"]*)\"").Groups[1].Value);
            }
        }

        return await client.PostAsync("/logout", new FormUrlEncodedContent(form), Token);
    }

    private static string FieldName(string tag) => WebUtility.HtmlDecode(Regex.Match(tag, "name=\"([^\"]+)\"").Groups[1].Value);

    [GeneratedRegex("<input[^>]*>")]
    private static partial Regex InputTag();
}
