namespace AuthService.Api.Features.EmailVerification;

/// <summary>Body of <c>POST /api/auth/email/verify</c>. Nullable so a missing token is reported as a validation error.</summary>
public sealed record VerifyEmailRequest(string? Token);

public sealed record VerifyEmailResponse(bool Verified);

/// <summary>Body of <c>POST /api/auth/email/verify/resend</c>. Nullable so a missing email is reported as a validation error.</summary>
public sealed record ResendVerificationRequest(string? Email);

/// <summary>The same answer for every email, so it never reveals whether an account exists.</summary>
public sealed record ResendVerificationResponse(string Message);
