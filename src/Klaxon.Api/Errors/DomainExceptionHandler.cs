using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Klaxon.Api.Errors;

// Translates domain and persistence failures into RFC-9457 problem responses. The domain
// constructors are the validation layer: they throw ArgumentException (and its subclasses) with the
// offending parameter name, so there is no separate validation tier.
public sealed class DomainExceptionHandler(IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, errors) = Map(exception);
        if (status is null)
            return false; // not ours -> let the default 500 handling take over

        httpContext.Response.StatusCode = status.Value;
        var problemDetails = new ProblemDetails { Status = status, Title = title };
        if (errors is not null)
            problemDetails.Extensions["errors"] = errors;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails,
        });
    }

    private static (int? Status, string? Title, IDictionary<string, string[]>? Errors) Map(Exception exception) =>
        exception switch
        {
            // ArgumentException is the base of ArgumentNullException and ArgumentOutOfRangeException,
            // so this one arm covers every guarded constructor.
            ArgumentException argument => (
                StatusCodes.Status400BadRequest,
                "One or more validation errors occurred.",
                new Dictionary<string, string[]> { [argument.ParamName ?? "request"] = [argument.Message] }),

            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } => (
                StatusCodes.Status409Conflict, "The resource conflicts with an existing one.", null),

            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation } } => (
                StatusCodes.Status409Conflict, "A referenced resource does not exist.", null),

            _ => (null, null, null),
        };
}
