namespace AuthService.Api.Common;

public static class ApiProblem
{
    /// <summary>
    /// An error response with a stable machine-readable <c>code</c>, so clients can tell failures apart without
    /// parsing the human-readable text.
    /// </summary>
    public static IResult Create(int statusCode, string code, string title, string detail) =>
        Results.Problem(
            statusCode: statusCode,
            title: title,
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code });
}
