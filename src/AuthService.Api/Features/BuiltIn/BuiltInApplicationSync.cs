using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.BuiltIn;

/// <summary>
/// Makes the database hold the built-in product and its permission catalog exactly as the code defines them. Run at startup, like
/// loading the signing keys, so a new permission is there as soon as the version that uses it is deployed. It only adds and
/// updates: a permission removed from the code stays in the catalog, because a role may still grant it.
/// </summary>
public sealed class BuiltInApplicationSync(AuthDbContext db, TimeProvider timeProvider)
{
    // An arbitrary number that identifies this lock to PostgreSQL's advisory lock, so two instances starting together take turns.
    private const long AdvisoryLockId = 0x41555448_42494C54;

    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({AdvisoryLockId})", cancellationToken);

        var now = timeProvider.GetUtcNow();
        var application = await db.Applications.SingleOrDefaultAsync(candidate => candidate.Key == BuiltInApplication.Key, cancellationToken);

        if (application is null)
        {
            application = new Application
            {
                Id = BuiltInApplication.Id,
                Key = BuiltInApplication.Key,
                Name = BuiltInApplication.Name,
                JwtAudience = BuiltInApplication.Audience,
                ClientId = BuiltInApplication.ClientId,
                IsActive = true,
                IsBuiltIn = true,
                CreatedAt = now,
            };
            db.Applications.Add(application);
        }
        else if (!application.IsBuiltIn || application.Id != BuiltInApplication.Id)
        {
            // Refuse to start rather than quietly take over a product someone registered under the reserved key.
            throw new InvalidOperationException(
                $"The application key '{BuiltInApplication.Key}' is reserved for the Auth Service itself but is registered to another application.");
        }
        else
        {
            application.Name = BuiltInApplication.Name;
            application.JwtAudience = BuiltInApplication.Audience;
            application.IsActive = true;
        }

        var existing = await db.Permissions.Where(permission => permission.ApplicationId == application.Id)
            .ToDictionaryAsync(permission => permission.Key, cancellationToken);

        foreach (var wanted in BuiltInApplication.Permissions)
        {
            if (!existing.TryGetValue(wanted.Key, out var permission))
            {
                db.Permissions.Add(new Permission
                {
                    Id = Guid.CreateVersion7(),
                    ApplicationId = application.Id,
                    Key = wanted.Key,
                    Label = wanted.Label,
                    Description = wanted.Description,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            else if (permission.Label != wanted.Label || permission.Description != wanted.Description)
            {
                permission.Label = wanted.Label;
                permission.Description = wanted.Description;
                permission.UpdatedAt = now;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
