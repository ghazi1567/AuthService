using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.Applications;

public static class ApplicationLocking
{
    /// <summary>
    /// Loads a product and locks its row until the surrounding transaction ends, so changes to one product (its catalog, its
    /// status, its secrets) are applied one at a time. Call it inside a transaction.
    /// </summary>
    public static Task<Application?> LockApplicationAsync(this AuthDbContext db, Guid applicationId, CancellationToken cancellationToken) =>
        db.Applications.FromSqlInterpolated($"SELECT * FROM applications WHERE id = {applicationId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
}
