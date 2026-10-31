using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.Accounts;

/// <summary>One tenant the person belongs to, so the client can choose which to work in (token exchange, TOK-01).</summary>
public sealed record MembershipSummary(Guid TenantId, string TenantName, MembershipStatus Status);

public static class MembershipQueries
{
    /// <summary>Every membership of one person, by tenant name. Says nothing about other people in those tenants.</summary>
    public static Task<List<MembershipSummary>> ListMembershipsAsync(this AuthDbContext db, Guid userId, CancellationToken cancellationToken) =>
        (from membership in db.TenantMemberships
         join tenant in db.Tenants on membership.TenantId equals tenant.Id
         where membership.UserId == userId
         orderby tenant.Name, tenant.Id
         select new MembershipSummary(tenant.Id, tenant.Name, membership.Status))
        .ToListAsync(cancellationToken);
}
