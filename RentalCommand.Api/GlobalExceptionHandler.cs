using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Auth;

namespace RentalCommand.Api;

/// <summary>
/// Last-resort exception-to-ProblemDetails mapper, registered via <c>UseExceptionHandler</c>. Two jobs:
///
/// <list type="number">
///   <item>
///     Surface a <see cref="DomainValidationException"/> (a business-rule violation: illegal lease
///     transition, double-booked unit, deleting a tenant with an active lease, inverted date range, …)
///     as the clean 400/409 it carries, using its safe message — NOT a 500.
///   </item>
///   <item>
///     Stop a raw persistence fault (<see cref="DbUpdateException"/> / <see cref="PostgresException"/>,
///     e.g. a CHECK/UNIQUE/FK constraint violation that slipped past the app-layer guards) from leaking
///     the exception type, stack trace, SQL, or constraint name to the client. The client gets a generic
///     409 (constraint conflict) or 500; the FULL detail is logged server-side.
///   </item>
/// </list>
///
/// Anything else is left unhandled (<c>return false</c>) so the framework's default 500 applies. The
/// <see cref="RentalCommand.Api.Controllers.MissingAuthContextException"/> → 401 mapping is handled by a
/// dedicated middleware registered closer to the controllers, so it never reaches here.
/// </summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ProblemDetails problem;

        switch (exception)
        {
            case RefreshTokenRotationOwnershipException:
                problem = new ProblemDetails
                {
                    Status = StatusCodes.Status401Unauthorized,
                    Title = "Unauthorized",
                    Detail = "This refresh token is no longer valid. Please sign in again.",
                };
                break;

            case AtomicIdempotencyConflictException idempotencyConflict:
                problem = new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = "Conflict",
                    Detail = idempotencyConflict.Message,
                };
                break;

            case FairHousingBlockedException fairHousing:
                // A soft, overridable content gate — the copy was flagged by the Fair Housing review and
                // not acknowledged. 422 (Unprocessable Content) with the structured concern list so the UI
                // can render each flagged phrase + reason and offer "send anyway".
                problem = new ProblemDetails
                {
                    Status = StatusCodes.Status422UnprocessableEntity,
                    Title = "Fair Housing review",
                    Detail = fairHousing.Message,
                };
                problem.Extensions["fairHousingConcerns"] = fairHousing.Concerns
                    .Select(c => new { phrase = c.Phrase, concern = c.Concern })
                    .ToArray();
                break;

            case DomainValidationException domain:
                // Safe, user-facing message authored by the domain service.
                problem = new ProblemDetails
                {
                    Status = domain.StatusCode,
                    Title = domain.StatusCode == StatusCodes.Status409Conflict ? "Conflict" : "Validation failed",
                    Detail = domain.Message,
                };
                break;

            case DbUpdateException dbUpdate:
            {
                // A constraint violation (23xxx in the SQLSTATE class) is a conflict with existing data /
                // an invariant; anything else is an unexpected persistence fault. Either way the client
                // gets a generic body — never the SQL, constraint name, or type. Full detail is logged.
                var postgres = dbUpdate.InnerException as PostgresException
                    ?? FindPostgresException(dbUpdate);
                var isConstraintViolation = postgres?.SqlState?.StartsWith("23", StringComparison.Ordinal) == true;

                _logger.LogError(dbUpdate,
                    "Unhandled persistence error on {Method} {Path} (SQLSTATE {SqlState}, constraint {Constraint}).",
                    httpContext.Request.Method, httpContext.Request.Path,
                    postgres?.SqlState, postgres?.ConstraintName);

                problem = isConstraintViolation
                    ? new ProblemDetails
                    {
                        Status = StatusCodes.Status409Conflict,
                        Title = "Conflict",
                        Detail = "The request conflicts with existing data or a data-integrity rule.",
                    }
                    : new ProblemDetails
                    {
                        Status = StatusCodes.Status500InternalServerError,
                        Title = "Server error",
                        Detail = "An unexpected error occurred while saving your changes.",
                    };
                break;
            }

            case PostgresException postgres:
            {
                // A bare Postgres error that surfaced outside a SaveChanges (rare, but possible).
                var isConstraintViolation = postgres.SqlState?.StartsWith("23", StringComparison.Ordinal) == true;

                _logger.LogError(postgres,
                    "Unhandled database error on {Method} {Path} (SQLSTATE {SqlState}, constraint {Constraint}).",
                    httpContext.Request.Method, httpContext.Request.Path,
                    postgres.SqlState, postgres.ConstraintName);

                problem = isConstraintViolation
                    ? new ProblemDetails
                    {
                        Status = StatusCodes.Status409Conflict,
                        Title = "Conflict",
                        Detail = "The request conflicts with existing data or a data-integrity rule.",
                    }
                    : new ProblemDetails
                    {
                        Status = StatusCodes.Status500InternalServerError,
                        Title = "Server error",
                        Detail = "An unexpected database error occurred.",
                    };
                break;
            }

            default:
                // Not ours — let the framework produce its default response (and log).
                return false;
        }

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        await httpContext.Response.WriteAsJsonAsync(
            problem, problem.GetType(), options: null, contentType: "application/problem+json", cancellationToken);
        return true;
    }

    // Walk the inner-exception chain for a PostgresException (the EF wrapper sometimes nests it deeper
    // than the immediate InnerException).
    private static PostgresException? FindPostgresException(Exception ex)
    {
        for (var current = ex.InnerException; current != null; current = current.InnerException)
        {
            if (current is PostgresException pg)
            {
                return pg;
            }
        }

        return null;
    }
}
