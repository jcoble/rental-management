namespace RentalCommand.Core.Authorization;

/// <summary>
/// Server-validated coordinates carried into list, dashboard, and report queries so property scope
/// is enforced by the same translated SQL statement that filters, aggregates, sorts, and pages the
/// requested records. The values are never accepted from request input.
/// </summary>
public readonly record struct WorkspaceReadScope(
    int PortfolioId,
    int UserId,
    Guid SessionId,
    int AccessContextId,
    long AccessRevision);
