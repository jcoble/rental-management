namespace RentalCommand.Core.Interfaces;

/// <summary>
/// Empty marker interface. Entities implementing it are automatically captured by the
/// <c>AtomicAuditSaveChangesInterceptor</c> on create / edit / delete — one append-only
/// <see cref="Entities.AtomicAuditLog"/> row per change. Opt a future entity into the audit trail
/// by adding this marker (and <see cref="IPortfolioScoped"/>); leave high-volume / infra
/// tables unmarked so they never flood the trail.
/// </summary>
public interface IAuditable
{
}
