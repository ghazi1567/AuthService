using AuthService.Infrastructure.Persistence.Entities;

namespace AuthService.Api.Features.Audit;

/// <summary>Who performed an audited action.</summary>
public readonly record struct AuditActor(AuditActorType Type, Guid? Id)
{
    public static AuditActor Anonymous => new(AuditActorType.Anonymous, null);

    public static AuditActor System => new(AuditActorType.System, null);

    public static AuditActor User(Guid userId) => new(AuditActorType.User, userId);

    /// <summary>A product acting with its service token (APP-05). The id is the product's application id.</summary>
    public static AuditActor Product(Guid applicationId) => new(AuditActorType.Product, applicationId);
}
