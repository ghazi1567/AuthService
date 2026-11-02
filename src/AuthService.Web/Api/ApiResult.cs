namespace AuthService.Web.Api;

/// <summary>The outcome of a call to the API: a value, or a problem a page can show. Calls never throw for an API error.</summary>
public sealed class ApiResult<T>
{
    private ApiResult(T? value, ApiProblem? problem)
    {
        Value = value;
        Problem = problem;
    }

    public bool IsSuccess => Problem is null;

    public T? Value { get; }

    public ApiProblem? Problem { get; }

    public static ApiResult<T> Success(T value) => new(value, null);

    public static ApiResult<T> Failure(ApiProblem problem) => new(default, problem);
}
