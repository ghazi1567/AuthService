namespace AuthService.Api.Features.Signup;

public static class SignupEndpoints
{
    public const string Route = "/api/auth/signup";

    public static IEndpointRouteBuilder MapSignup(this IEndpointRouteBuilder app)
    {
        app.MapPost(Route, SignUpAsync).AllowAnonymous();
        return app;
    }

    private static async Task<IResult> SignUpAsync(
        SignupRequest request,
        SignupService signupService,
        CancellationToken cancellationToken)
    {
        if (!SignupValidator.TryValidate(request, out var valid, out var errors))
        {
            return Results.ValidationProblem(errors);
        }

        var result = await signupService.SignUpAsync(valid!, cancellationToken);

        return result switch
        {
            SignupResult.Created created => Results.Json(created.Response, statusCode: StatusCodes.Status201Created),

            // The spec asks the response to point the person to log in and create a tenant (TEN-09), never to merge accounts.
            _ => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Email already registered",
                detail: "An account with this email already exists. Log in to it and create a new tenant instead.",
                extensions: new Dictionary<string, object?> { ["code"] = "email_already_registered" }),
        };
    }
}
