using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.Applications;

public static class ApplicationLocking
{
    /// <summary>
    /// Loads a product and locks its row until the surrounding transaction ends, so changes to one product (its catalog, its
    /// status, its secrets) are applied one at a time. Call it inside a transaction. The built-in Auth Service product is never
    /// returned: its catalog is defined in code and it has no credentials, so the admin API cannot change it.
    /// </summary>
    public static Task<Application?> LockApplicationAsync(this AuthDbContext db, Guid applicationId, CancellationToken cancellationToken) =>
        db.Applications.FromSqlInterpolated($"SELECT * FROM applications WHERE id = {applicationId} AND NOT is_built_in FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
}
