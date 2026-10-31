using AuthService.Infrastructure.Persistence.Entities;

namespace AuthService.Api.Features.Audit;

/// <summary>Who performed an audited action.</summary>
public readonly record struct AuditActor(AuditActorType Type, Guid? Id)
{
    public static AuditActor Anonymous => new(AuditActorType.Anonymous, null);

    public static AuditActor System => new(AuditActorType.System, null);

    public static AuditActor User(Guid userId) => new(AuditActorType.User, userId);
}
