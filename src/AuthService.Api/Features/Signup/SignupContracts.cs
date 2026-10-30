namespace AuthService.Api.Features.Signup;

/// <summary>Body of <c>POST /api/auth/signup</c>. Every field is nullable so a missing one is reported as a validation error.</summary>
public sealed record SignupRequest(string? TenantName, string? Email, string? Password, string? DisplayName);

/// <summary>Returned on success. Never contains the password or its hash.</summary>
public sealed record SignupResponse(Guid TenantId, Guid UserId, Guid MembershipId, string Email);
