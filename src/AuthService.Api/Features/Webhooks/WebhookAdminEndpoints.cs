using System.Security.Claims;
using AuthService.Api.Common;
using AuthService.Api.Features.Audit;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthService.Api.Features.Webhooks;

public sealed record WebhookAdminItem(
    Guid Id,
    Guid ApplicationId,
    string ApplicationKey,
    string EventType,
    string Status,
    int Attempts,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset NextAttemptAt,
    DateTimeOffset DeadlineAt,
    DateTimeOffset? DeliveredAt);

/// <summary>Where the next page starts: pass both values back as <c>before</c> and <c>beforeId</c>.</summary>
public sealed record WebhookCursor(DateTimeOffset Before, Guid BeforeId);

public sealed record WebhookAdminPage(IReadOnlyList<WebhookAdminItem> Items, WebhookCursor? Next);

/// <summary>SUB-06: the platform admin sees webhooks that could not be delivered, and sends them again.</summary>
public static class WebhookAdminEndpoints
{
    public const string Route = "/api/auth/admin/webhooks";
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public static RouteGroupBuilder MapWebhooksAdmin(this RouteGroupBuilder admin)
    {
        admin.MapGet("/webhooks", ListAsync);
        admin.MapPost("/webhooks/{webhookId:guid}/resend", ResendAsync);
        return admin;
    }

    public static string ResendRouteFor(Guid webhookId) => $"{Route}/{webhookId}/resend";

    /// <summary>Webhooks newest first, filtered by status or product, paged with the cursor of the previous page.</summary>
    private static async Task<IResult> ListAsync(
        AuthDbContext db,
        string? status,
        Guid? applicationId,
        DateTimeOffset? before,
        Guid? beforeId,
        int? limit,
        CancellationToken cancellationToken)
    {
        var take = limit ?? DefaultLimit;
        if (take is < 1 or > MaxLimit)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(limit)] = [$"Limit must be between 1 and {MaxLimit}."] });
        }

        WebhookStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<WebhookStatus>(status.Trim(), ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed) || status.Trim().All(char.IsDigit))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(status)] = ["Status must be Pending, Delivered or Failed."] });
            }

            statusFilter = parsed;
        }

        var query = db.WebhookOutbox.AsNoTracking().AsQueryable();
        if (statusFilter is { } wanted)
        {
            query = query.Where(message => message.Status == wanted);
        }

        if (applicationId is { } application)
        {
            query = query.Where(message => message.ApplicationId == application);
        }

        if (before is { } cutoff)
        {
            query = beforeId is { } cutoffId
                ? query.Where(message => message.CreatedAt < cutoff || (message.CreatedAt == cutoff && message.Id.CompareTo(cutoffId) < 0))
                : query.Where(message => message.CreatedAt < cutoff);
        }

        var rows = await (
            from message in query
            join owner in db.Applications on message.ApplicationId equals owner.Id
            orderby message.CreatedAt descending, message.Id descending
            select new { message, ApplicationKey = owner.Key })
            .Take(take + 1)
            .ToListAsync(cancellationToken);

        var page = rows.Take(take).ToList();
        var next = rows.Count > take ? new WebhookCursor(page[^1].message.CreatedAt, page[^1].message.Id) : null;
        return Results.Ok(new WebhookAdminPage([.. page.Select(row => ToItem(row.message, row.ApplicationKey))], next));
    }

    /// <summary>Puts a Failed webhook back in the queue with a fresh 24 hours, to be delivered as normal.</summary>
    private static async Task<IResult> ResendAsync(
        Guid webhookId,
        ClaimsPrincipal caller,
        AuthDbContext db,
        AuditLog audit,
        IOptions<WebhookOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var message = await db.WebhookOutbox.SingleOrDefaultAsync(candidate => candidate.Id == webhookId, cancellationToken);
        if (message is null)
        {
            return ApiProblem.Create(StatusCodes.Status404NotFound, "webhook_not_found", "Webhook not found", "No webhook has this id.");
        }

        // Claimed with one conditional update, so two admins pressing resend at once queue it once.
        var now = timeProvider.GetUtcNow();
        var deadline = now.AddHours(options.Value.RetryWindowHours);
        var claimed = await db.WebhookOutbox
            .Where(candidate => candidate.Id == webhookId && candidate.Status == WebhookStatus.Failed)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.Status, WebhookStatus.Pending)
                .SetProperty(candidate => candidate.Attempts, 0)
                .SetProperty(candidate => candidate.NextAttemptAt, now)
                .SetProperty(candidate => candidate.DeadlineAt, deadline), cancellationToken);
        if (claimed == 0)
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "webhook_not_failed", "Webhook is not failed",
                "Only a webhook that failed can be sent again.");
        }

        var adminId = caller.GetUserId() ?? throw new InvalidOperationException("The platform-admin policy guarantees a user id.");
        audit.Record(AuditActions.WebhookResent, AuditActor.User(adminId), detail: webhookId.ToString());
        await db.SaveChangesAsync(cancellationToken);

        db.ChangeTracker.Clear();
        var item = await (
            from candidate in db.WebhookOutbox.AsNoTracking()
            join owner in db.Applications on candidate.ApplicationId equals owner.Id
            where candidate.Id == webhookId
            select new { candidate, owner.Key })
            .SingleAsync(cancellationToken);
        return Results.Ok(ToItem(item.candidate, item.Key));
    }

    private static WebhookAdminItem ToItem(WebhookOutboxMessage message, string applicationKey) => new(
        message.Id, message.ApplicationId, applicationKey, message.EventType, message.Status.ToString(), message.Attempts,
        message.LastError, message.CreatedAt, message.NextAttemptAt, message.DeadlineAt, message.DeliveredAt);
}
