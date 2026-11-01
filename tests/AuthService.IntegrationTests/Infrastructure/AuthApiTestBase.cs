using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AuthService.Api.Email;
using AuthService.Api.Email.Providers;
using AuthService.Api.Features.EmailVerification;
using AuthService.Api.Features.Login;
using AuthService.Api.Features.PasswordReset;
using AuthService.Api.Features.Refresh;
using AuthService.Api.Features.Signup;
using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AuthService.IntegrationTests.Infrastructure;

/// <summary>
/// The single base class every API integration suite derives from (TST-02). Each test gets its own
/// running service and its own database copy, so tests cannot affect each other.
/// </summary>
/// <example>
/// <code>
/// public sealed class MyTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
/// {
///     [Fact]
///     public async Task TEN_TC1_Something() => (await Client.GetAsync("/", TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
/// }
/// </code>
/// </example>
public abstract class AuthApiTestBase(PostgresContainerFixture postgres) : IAsyncLifetime
{
    /// <summary>The password every helper-created account uses.</summary>
    protected const string DefaultPassword = "correct horse battery";

    /// <summary>Reads API responses: web defaults, with enums written by name like the API does.</summary>
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private AuthApiHarness? _harness;

    protected PostgresContainerFixture Postgres { get; } = postgres;

    protected AuthApiHarness Harness =>
        _harness ?? throw new InvalidOperationException("The test harness has not been started.");

    protected HttpClient Client => Harness.Client;

    protected TestDatabase Database => Harness.Database;

    /// <summary>Override to start this test class's service with different configuration or services.</summary>
    protected virtual HarnessOptions? Options => null;

    public async ValueTask InitializeAsync() =>
        _harness = await AuthApiHarness.CreateAsync(Postgres, TestContext.Current.CancellationToken, Options);

    public async ValueTask DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }

    /// <summary>Calls the real signup endpoint (TEN-01). Parameters are nullable so tests can send incomplete requests.</summary>
    protected Task<HttpResponseMessage> SignUpAsync(string? tenantName, string? email, string? password, CancellationToken ct) =>
        Client.PostAsJsonAsync(SignupEndpoints.Route, new { tenantName, email, password }, ct);

    /// <summary>Every email the service has sent so far. Override when a test replaces the in-memory email provider.</summary>
    protected virtual IReadOnlyList<EmailMessage> SentEmails
    {
        get
        {
            using var scope = Harness.CreateScope();
            return scope.ServiceProvider.GetRequiredService<InMemoryEmailSender>().Sent;
        }
    }

    /// <summary>
    /// Waits for the <paramref name="nth"/> verification email sent to <paramref name="email"/> and returns the token in
    /// its link, exactly as a person would copy it from the message.
    /// </summary>
    protected async Task<string> WaitForEmailTokenAsync(string email, string subject, int nth = 1)
    {
        string? token = null;

        await Eventually.UntilAsync(
            () =>
            {
                var emails = SentEmails.Where(message => message.To == email && message.Subject == subject).ToList();
                if (emails.Count < nth)
                {
                    return false;
                }

                token = Uri.UnescapeDataString(Regex.Match(emails[nth - 1].TextBody, @"token=([^\s&]+)").Groups[1].Value);
                return true;
            },
            $"email '{subject}' #{nth} to {email} was sent");

        return token!;
    }

    /// <summary>The token in the <paramref name="nth"/> verification email sent to <paramref name="email"/> (TEN-11).</summary>
    protected Task<string> WaitForVerificationTokenAsync(string email, int nth = 1) =>
        WaitForEmailTokenAsync(email, "Verify your email address", nth);

    /// <summary>The token in the <paramref name="nth"/> password-reset email sent to <paramref name="email"/> (TEN-10).</summary>
    protected Task<string> WaitForResetTokenAsync(string email, int nth = 1) =>
        WaitForEmailTokenAsync(email, "Reset your password", nth);

    /// <summary>Calls the real "forgot password" endpoint (TEN-10).</summary>
    protected Task<HttpResponseMessage> ForgotPasswordAsync(string? email, CancellationToken ct) =>
        Client.PostAsJsonAsync(PasswordResetEndpoints.ForgotRoute, new { email }, ct);

    /// <summary>Calls the real password-reset endpoint (TEN-10), as the page behind the emailed link does.</summary>
    protected Task<HttpResponseMessage> ResetPasswordAsync(string? token, string? newPassword, CancellationToken ct) =>
        Client.PostAsJsonAsync(PasswordResetEndpoints.ResetRoute, new { token, newPassword }, ct);

    /// <summary>Calls the real verify endpoint (TEN-11), as the page behind the emailed link does.</summary>
    protected Task<HttpResponseMessage> VerifyEmailLinkAsync(string? token, CancellationToken ct) =>
        Client.PostAsJsonAsync(EmailVerificationEndpoints.VerifyRoute, new { token }, ct);

    /// <summary>Calls the real resend endpoint (TEN-11).</summary>
    protected Task<HttpResponseMessage> ResendVerificationAsync(string? email, CancellationToken ct) =>
        Client.PostAsJsonAsync(EmailVerificationEndpoints.ResendRoute, new { email }, ct);

    /// <summary>Calls an endpoint with GET, sending <paramref name="bearerToken"/> as the access token when there is one.</summary>
    protected Task<HttpResponseMessage> GetAsync(string path, string? bearerToken, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (bearerToken is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
        }

        return Client.SendAsync(request, ct);
    }

    /// <summary>Calls an endpoint with POST and a JSON body, sending <paramref name="bearerToken"/> as the access token when there is one.</summary>
    protected Task<HttpResponseMessage> PostAsync(string path, object body, string? bearerToken, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (bearerToken is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
        }

        return Client.SendAsync(request, ct);
    }

    /// <summary>Calls an endpoint with PUT and a JSON body, sending <paramref name="bearerToken"/> as the access token when there is one.</summary>
    protected Task<HttpResponseMessage> PutAsync(string path, object body, string? bearerToken, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(body) };
        if (bearerToken is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
        }

        return Client.SendAsync(request, ct);
    }

    /// <summary>The key this running service signs its tokens with, so a test can make a token the service will accept.</summary>
    protected SecurityKey ServiceSigningKey()
    {
        using var scope = Harness.CreateScope();
        return scope.ServiceProvider.GetRequiredService<SigningKeyService>().CurrentSigningKey;
    }

    /// <summary>
    /// Makes a signed identity-style token for <paramref name="userId"/>, to try tokens a real login would never produce:
    /// expired, signed by another key, from another issuer, or carrying an audience (a product token).
    /// </summary>
    protected static string CreateToken(
        Guid userId,
        SecurityKey key,
        TimeSpan expiresIn,
        string issuer = "AuthService",
        string? audience = null)
    {
        var now = DateTime.UtcNow;

        // Expired tokens need an issue time before their expiry, so shift the whole token back instead of just the expiry.
        var issuedAt = expiresIn < TimeSpan.Zero ? now + expiresIn - TimeSpan.FromMinutes(1) : now;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, userId.ToString())]),
            IssuedAt = issuedAt,
            NotBefore = issuedAt,
            Expires = now + expiresIn,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.EcdsaSha256),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>
    /// Sets or clears the platform-admin flag directly in the database, which is the only way it can ever be changed: no
    /// endpoint does it.
    /// </summary>
    protected Task SetPlatformAdminAsync(string email, bool isPlatformAdmin, CancellationToken ct) =>
        WithDbAsync(async db =>
        {
            var changed = await db.Users
                .Where(user => user.Email == email)
                .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.IsPlatformAdmin, isPlatformAdmin), ct);
            changed.ShouldBe(1, $"no user with email '{email}'");
        });

    /// <summary>Signs up a verified user, makes them a platform admin in the database, and returns their access token.</summary>
    protected async Task<(SignupResponse Account, string AccessToken)> CreatePlatformAdminAsync(string email, CancellationToken ct)
    {
        var account = await SignUpVerifiedAsync("Platform Ops", email, ct);
        await SetPlatformAdminAsync(email, true, ct);
        return (account, (await LoginForTokensAsync(email, ct)).AccessToken);
    }

    /// <summary>Calls the real login endpoint (TEN-02).</summary>
    protected Task<HttpResponseMessage> LoginAsync(string? email, string? password, CancellationToken ct) =>
        Client.PostAsJsonAsync(LoginEndpoints.Route, new { email, password }, ct);

    /// <summary>Logs in with the default password and returns the tokens, failing the test if login is refused.</summary>
    protected async Task<LoginResponse> LoginForTokensAsync(string email, CancellationToken ct)
    {
        var response = await LoginAsync(email, DefaultPassword, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<LoginResponse>(Json, ct))!;
    }

    /// <summary>Calls the real refresh endpoint (TEN-03).</summary>
    protected Task<HttpResponseMessage> RefreshAsync(string? refreshToken, CancellationToken ct) =>
        Client.PostAsJsonAsync(RefreshEndpoints.Route, new { refreshToken }, ct);

    /// <summary>Signs up through the real endpoint, then marks the email verified so the account can log in.</summary>
    protected async Task<SignupResponse> SignUpVerifiedAsync(string tenantName, string email, CancellationToken ct)
    {
        var response = await SignUpAsync(tenantName, email, DefaultPassword, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var account = (await response.Content.ReadFromJsonAsync<SignupResponse>(Json, ct))!;

        await VerifyEmailAsync(email, ct);
        return account;
    }

    /// <summary>Runs <paramref name="action"/> against the running service's own database context, in a fresh scope.</summary>
    protected async Task WithDbAsync(Func<AuthDbContext, Task> action)
    {
        await using var scope = Harness.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }

    /// <summary>Sets the status of every membership belonging to the person with this email.</summary>
    protected Task SetMembershipStatusesAsync(string email, MembershipStatus status, CancellationToken ct) =>
        WithDbAsync(db => db.TenantMemberships
            .Where(membership => db.Users.Any(user => user.Email == email && user.Id == membership.UserId))
            .ExecuteUpdateAsync(setters => setters.SetProperty(membership => membership.Status, status), ct));

    /// <summary>Adds a membership directly in the database (TEN-09, which creates them through the API, is not built yet).</summary>
    protected Task AddMembershipAsync(Guid userId, Guid tenantId, MembershipStatus status, CancellationToken ct) =>
        WithDbAsync(async db =>
        {
            db.TenantMemberships.Add(new TenantMembership
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                UserId = userId,
                Status = status,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        });

    /// <summary>Reads an error response, keeping the parts that must not differ between failures that look alike to a caller.</summary>
    protected static async Task<ProblemSummary> ReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = document.RootElement;

        return new ProblemSummary(
            root.GetProperty("status").GetInt32(),
            root.GetProperty("title").GetString(),
            root.GetProperty("detail").GetString(),
            root.GetProperty("code").GetString());
    }

    protected sealed record ProblemSummary(int Status, string? Title, string? Detail, string? Code);

    /// <summary>A product as it arrives in real life: registered, with a catalog, active, and holding a service token.</summary>
    protected sealed record TestProduct(Guid Id, string Key, string ClientId, string ClientSecret, string ServiceToken);

    /// <summary>Registers a product through the admin API, gives it a one-permission catalog, activates it and signs it in.</summary>
    protected async Task<TestProduct> CreateActiveProductAsync(string adminToken, string key, CancellationToken ct)
    {
        var registered = await PostAsync("/api/auth/admin/applications", new { key, name = key, jwtAudience = key + "-api" }, adminToken, ct);
        registered.StatusCode.ShouldBe(HttpStatusCode.Created);
        var product = (await registered.Content.ReadFromJsonAsync<JsonElement>(Json, ct));
        var id = product.GetProperty("id").GetGuid();
        var clientId = product.GetProperty("clientId").GetString()!;
        var clientSecret = product.GetProperty("clientSecret").GetString()!;

        (await PostAsync($"/api/auth/admin/applications/{id}/permissions",
            new { permissions = new[] { new { key = "thing.use", label = "Use the thing" } } }, adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await PutAsync($"/api/auth/admin/applications/{id}/active", new { isActive = true }, adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var token = await PostAsync("/api/auth/token",
            new { grant_type = "client_credentials", client_id = clientId, client_secret = clientSecret }, null, ct);
        token.StatusCode.ShouldBe(HttpStatusCode.OK);
        var accessToken = (await token.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("access_token").GetString()!;

        return new TestProduct(id, key, clientId, clientSecret, accessToken);
    }

    /// <summary>
    /// Trades an identity token for a tenant-admin token: a token for the Auth Service's own product in one tenant, which is what
    /// the tenant-scoped endpoints take (TOK-01 with the built-in application).
    /// </summary>
    protected async Task<string> TenantTokenAsync(string identityToken, Guid tenantId, CancellationToken ct)
    {
        var response = await PostAsync("/api/auth/token/exchange",
            new { tenantId, applicationId = AuthService.Api.Features.BuiltIn.BuiltInApplication.Id }, identityToken, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("accessToken").GetString()!;
    }

    /// <summary>Sends a PATCH with a raw JSON body, for tests that need to say exactly what is on the wire.</summary>
    protected Task<HttpResponseMessage> PatchJsonAsync(string path, string rawJson, string? bearerToken, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, path)
        {
            Content = new StringContent(rawJson, System.Text.Encoding.UTF8, "application/json"),
        };
        if (bearerToken is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
        }

        return Client.SendAsync(request, ct);
    }

    /// <summary>
    /// Marks an email as verified directly in the database. Email verification (TEN-11) is not built yet, and login
    /// refuses unverified users, so tests that need a working login use this after signing up.
    /// </summary>
    protected async Task VerifyEmailAsync(string email, CancellationToken ct)
    {
        await using var scope = Harness.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        var updated = await db.Users
            .Where(user => user.Email == email)
            .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.EmailVerifiedAt, DateTimeOffset.UtcNow), ct);

        updated.ShouldBe(1, $"no user with email '{email}' to verify");
    }
}
