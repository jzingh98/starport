namespace Starport.Api.Endpoints;

public static class ApiProblems
{
    public static IResult Problem(int status, string code, string title) =>
        Results.Problem(statusCode: status, title: title,
            extensions: new Dictionary<string, object?> { ["code"] = code });
}
