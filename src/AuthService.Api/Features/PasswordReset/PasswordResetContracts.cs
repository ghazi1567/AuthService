namespace AuthService.Api.Features.PasswordReset;

/// <summary>Body of <c>POST /api/auth/password/forgot</c>. Nullable so a missing email is reported as a validation error.</summary>
public sealed record ForgotPasswordRequest(string? Email);

/// <summary>The same answer for every email, so it never reveals whether an account exists.</summary>
public sealed record ForgotPasswordResponse(string Message);

/// <summary>Body of <c>POST /api/auth/password/reset</c>. Nullable so a missing field is reported as a validation error.</summary>
public sealed record ResetPasswordRequest(string? Token, string? NewPassword);

public sealed record ResetPasswordResponse(bool Reset);
