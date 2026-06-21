namespace RentalCommand.Core;

/// <summary>
/// Thrown by domain services when a request is well-formed but violates a business invariant — an
/// illegal lease state transition, a double-booked unit, deleting a tenant who still holds an active
/// lease, an out-of-order date range, etc. Carries the HTTP status the request pipeline should surface
/// (<see cref="StatusCode"/>: 400 for a plain validation failure, 409 for a conflict with existing
/// state) together with a SAFE, user-facing <see cref="Exception.Message"/>.
///
/// <para>
/// Deliberately distinct from a "not found" (services return <c>null</c> for that → 404) and from an
/// unexpected infrastructure fault (a raw <c>DbUpdateException</c>/<c>PostgresException</c> → generic
/// 500/409). The global exception handler maps THIS type to a clean ProblemDetails using its message,
/// which is why the message must never contain internal detail (SQL, constraint names, stack frames).
/// </para>
/// </summary>
public sealed class DomainValidationException : Exception
{
    /// <summary>HTTP status to surface: 400 (validation) or 409 (conflict). Defaults to 400.</summary>
    public int StatusCode { get; }

    public DomainValidationException(string message, int statusCode = 400)
        : base(message)
    {
        StatusCode = statusCode;
    }
}
