using System.Collections.Concurrent;
using System.Text.Json;
using AuthService.Web.Api;

namespace AuthService.Web.Security;

/// <summary>What a person may do with one thing in the console, from none up to full. Mirrors the API's permission levels.</summary>
public enum AccessLevel
{
    None = 0,
    ReadOnly = 1,
    CreateRead = 2,
    Full = 3,
}

/// <summary>The permission keys of the Auth Service's own product (the tenant console), as the API names them.</summary>
public static class PermissionKeys
{
    public const string SubscriptionsRead = "subscriptions.read";
    public const string AuditRead = "audit.read";
    public const string ApiKeysRead = "apikeys.read";
    public const string ApiKeysManage = "apikeys.manage";
    public const string MembersRead = "members.read";
    public const string MembersManage = "members.manage";
    public const string MembersInvite = "members.invite";
    public const string RolesRead = "roles.read";
    public const string RolesManage = "roles.manage";
    public const string RolesAssign = "roles.assign";
    public const string AccessManage = "access.manage";
}

/// <summary>An organisation's admin console, open for one person: its name, the token to call the API with, and what they may do.</summary>
public sealed class ConsoleContext(
    Guid tenantId,
    string tenantName,
    string email,
    string token,
    IReadOnlyDictionary<string, AccessLevel> permissions)
{
    public Guid TenantId { get; } = tenantId;

    public string TenantName { get; } = tenantName;

    public string Email { get; } = email;

    /// <summary>The product token for the organisation. It never leaves the server and is never printed.</summary>
    public string Token { get; } = token;

    /// <summary>Whether the person holds <paramref name="key"/> at <paramref name="minimum"/> or above.</summary>
    public bool Can(string key, AccessLevel minimum) => permissions.TryGetValue(key, out var level) && level >= minimum;

    public override string ToString() => $"Console of {TenantId}";
}

public enum ConsoleOutcome
{
    Ready,

    /// <summary>The sign-in ended or the API no longer accepts it: sign in again.</summary>
    SessionEnded,

    /// <summary>Not an active member of this organisation, or the API refused the token exchange.</summary>
    Denied,

    /// <summary>The API could not be reached or had a problem.</summary>
    Unavailable,
}

public sealed record ConsoleResult(ConsoleOutcome Outcome, ConsoleContext? Console = null);

/// <summary>Keeps each person's console tokens on the server until they are about to run out, so a page does not trade a token every time.</summary>
public sealed class ConsoleTokenCache(TimeProvider time)
{
    private readonly ConcurrentDictionary<(string SessionId, Guid TenantId), Entry> _entries = new();

    public ConsoleContext? Find(string sessionId, Guid tenantId) =>
        _entries.TryGetValue((sessionId, tenantId), out var entry) && entry.ExpiresAt > time.GetUtcNow() ? entry.Console : null;

    public void Store(string sessionId, ConsoleContext console, int expiresInSeconds)
    {
        var now = time.GetUtcNow();
        foreach (var (key, entry) in _entries)
        {
            if (entry.ExpiresAt <= now)
            {
                _entries.TryRemove(key, out _);
            }
        }

        // Renewed a minute early, like the sign-in's own token.
        _entries[(sessionId, console.TenantId)] = new Entry(console, now.AddSeconds(Math.Max(0, expiresInSeconds - 60)));
    }

    /// <summary>Forgets the token, for example when the API refused it, so the next page trades for a new one.</summary>
    public void Forget(string sessionId, Guid tenantId) => _entries.TryRemove((sessionId, tenantId), out _);

    /// <summary>Forgets every organisation's token for a session, for example when the person goes to choose another organisation. None is reused after that.</summary>
    public void ForgetSession(string sessionId)
    {
        foreach (var key in _entries.Keys.Where(key => key.SessionId == sessionId))
        {
            _entries.TryRemove(key, out _);
        }
    }

    private sealed record Entry(ConsoleContext Console, DateTimeOffset ExpiresAt);
}

/// <summary>
/// Opens an organisation's admin console for the signed-in person: checks they are an active member, trades their sign-in for a token
/// for the Auth Service's own product (the tenant console), and reads what that token lets them do. One per request, so the layout and
/// the page share the answer.
/// </summary>
public sealed class ConsoleSession(AuthApiClient api, SessionService sessions, ConsoleTokenCache cache)
{
    /// <summary>The fixed id of the Auth Service as a product, which every organisation can use without subscribing.</summary>
    public static readonly Guid BuiltInApplicationId = new("00000000-0000-7000-8000-00000000a07a");

    private Task<ConsoleResult>? _loaded;
    private Guid _loadedTenant;

    /// <summary>When the API asked us to slow down while opening the console, how many seconds to wait. Null otherwise.</summary>
    public int? RetryAfterSeconds { get; private set; }

    /// <summary>Forgets every token of this person's session, so choosing another organisation starts from a fresh exchange.</summary>
    public void ForgetTokens(HttpContext httpContext)
    {
        if (httpContext.User.FindFirst(SessionAuthentication.SessionIdClaim) is { } sid)
        {
            cache.ForgetSession(sid.Value);
        }

        _loaded = null;
    }

    public Task<ConsoleResult> LoadAsync(HttpContext httpContext, Guid tenantId)
    {
        if (_loaded is null || _loadedTenant != tenantId)
        {
            _loadedTenant = tenantId;
            _loaded = OpenAsync(httpContext, tenantId);
        }

        return _loaded;
    }

    /// <summary>Drops the cached token so the next load trades for a new one (after the API turned this one down).</summary>
    public void Refresh(HttpContext httpContext, Guid tenantId)
    {
        if (httpContext.User.FindFirst(SessionAuthentication.SessionIdClaim) is { } sid)
        {
            cache.Forget(sid.Value, tenantId);
        }

        _loaded = null;
    }

    /// <summary>Remembers how long the API asked us to wait, rounded up, so the page can show a countdown. A missing wait is a minute.</summary>
    private void NoteRateLimit(ApiProblem problem)
    {
        if (problem.Status == StatusCodes.Status429TooManyRequests)
        {
            RetryAfterSeconds = (int)Math.Clamp(Math.Ceiling((problem.RetryAfter ?? TimeSpan.FromSeconds(60)).TotalSeconds), 1, 3600);
        }
    }

    private async Task<ConsoleResult> OpenAsync(HttpContext httpContext, Guid tenantId)
    {
        var cancellation = httpContext.RequestAborted;
        if (httpContext.User.FindFirst(SessionAuthentication.SessionIdClaim) is not { } sid)
        {
            return new ConsoleResult(ConsoleOutcome.SessionEnded);
        }

        if (cache.Find(sid.Value, tenantId) is { } cached)
        {
            return new ConsoleResult(ConsoleOutcome.Ready, cached);
        }

        var identityToken = await sessions.GetAccessTokenAsync(sid.Value, cancellation);
        if (identityToken is null)
        {
            return new ConsoleResult(ConsoleOutcome.SessionEnded);
        }

        var me = await api.GetAsync<CurrentUser>("/api/auth/users/me", identityToken, cancellation);
        if (!me.IsSuccess)
        {
            NoteRateLimit(me.Problem!);
            return new ConsoleResult(me.Problem!.Status == StatusCodes.Status401Unauthorized ? ConsoleOutcome.SessionEnded : ConsoleOutcome.Unavailable);
        }

        var membership = me.Value!.Memberships.FirstOrDefault(candidate => candidate.TenantId == tenantId);
        if (membership is not { Status: MembershipStatus.Active })
        {
            return new ConsoleResult(ConsoleOutcome.Denied);
        }

        var exchange = await api.PostAsync<TokenExchangeRequest, TokenExchangeResponse>(
            "/api/auth/token/exchange", new TokenExchangeRequest(tenantId, BuiltInApplicationId), cancellation, identityToken);
        if (!exchange.IsSuccess)
        {
            NoteRateLimit(exchange.Problem!);
            return new ConsoleResult(exchange.Problem!.Status switch
            {
                StatusCodes.Status401Unauthorized => ConsoleOutcome.SessionEnded,
                StatusCodes.Status403Forbidden => ConsoleOutcome.Denied,
                _ => ConsoleOutcome.Unavailable,
            });
        }

        var console = new ConsoleContext(
            tenantId, membership.TenantName, me.Value.Email, exchange.Value!.AccessToken, ProductTokenClaims.ReadPermissions(exchange.Value.AccessToken));
        cache.Store(sid.Value, console, exchange.Value.ExpiresIn);
        return new ConsoleResult(ConsoleOutcome.Ready, console);
    }
}

/// <summary>Reads the permissions of a product token. The API checks the token on every call; this only decides what to show.</summary>
public static class ProductTokenClaims
{
    /// <summary>The <c>permissions</c> claim (<c>key:Level</c> strings) as a map. A token that is not shaped like one holds none.</summary>
    public static IReadOnlyDictionary<string, AccessLevel> ReadPermissions(string accessToken)
    {
        var result = new Dictionary<string, AccessLevel>();
        var parts = accessToken.Split('.');
        if (parts.Length != 3)
        {
            return result;
        }

        try
        {
            var padded = parts[1].Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
            using var payload = JsonDocument.Parse(Convert.FromBase64String(padded));
            if (payload.RootElement.ValueKind != JsonValueKind.Object || !payload.RootElement.TryGetProperty("permissions", out var claim))
            {
                return result;
            }

            foreach (var item in claim.ValueKind == JsonValueKind.Array ? claim.EnumerateArray() : [])
            {
                var text = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                var separator = text?.LastIndexOf(':') ?? -1;
                if (text is not null && separator > 0 && Enum.TryParse<AccessLevel>(text[(separator + 1)..], ignoreCase: false, out var level))
                {
                    var key = text[..separator];
                    result[key] = result.TryGetValue(key, out var existing) && existing > level ? existing : level;
                }
            }
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            result.Clear();
        }

        return result;
    }
}
