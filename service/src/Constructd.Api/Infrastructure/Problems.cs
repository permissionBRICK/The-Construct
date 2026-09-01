namespace Constructd.Api.Infrastructure;

/// <summary>
/// Every error answer is an RFC 7807 problem document, so clients (PowerShell, the extension) get
/// one predictable error shape.
/// </summary>
public static class Problems
{
    public static IResult BadRequest(string detail) =>
        TypedResults.Problem(detail, statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");

    public static IResult NotFound(string detail) =>
        TypedResults.Problem(detail, statusCode: StatusCodes.Status404NotFound, title: "Not found");

    public static IResult Forbidden(string detail) =>
        TypedResults.Problem(detail, statusCode: StatusCodes.Status403Forbidden, title: "Forbidden");

    public static IResult Conflict(string detail) =>
        TypedResults.Problem(detail, statusCode: StatusCodes.Status409Conflict, title: "Conflict");

    public static IResult UnavailableYet(string detail) =>
        TypedResults.Problem(detail, statusCode: StatusCodes.Status409Conflict, title: "Not available yet");
}
