namespace RentalCommand.Api.Controllers;

/// <summary>
/// Thrown when a request is authenticated but missing a required auth-context claim (portfolioId /
/// user id). The request pipeline maps THIS specific type to 401.
///
/// Deliberately NOT a generic <see cref="UnauthorizedAccessException"/>: the framework and BCL throw
/// <see cref="UnauthorizedAccessException"/> for filesystem permission errors (e.g. an unwritable
/// upload volume), and catching those as 401 disguises an infrastructure failure as a "session
/// expired" — which once turned a one-line permissions bug into a multi-hour false "auth" hunt.
/// A non-auth <see cref="UnauthorizedAccessException"/> now propagates to a 500, surfacing honestly.
/// </summary>
public sealed class MissingAuthContextException : Exception
{
    public MissingAuthContextException(string message) : base(message) { }
}
