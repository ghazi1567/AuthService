using Microsoft.Extensions.Options;

namespace AuthService.Web.Api;

/// <summary>Checks <see cref="AuthApiOptions"/> at start-up and reports every problem by name, so the message says which setting to fix.</summary>
public sealed class AuthApiOptionsValidator : IValidateOptions<AuthApiOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthApiOptions options)
    {
        var problems = options.Problems().ToList();
        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
