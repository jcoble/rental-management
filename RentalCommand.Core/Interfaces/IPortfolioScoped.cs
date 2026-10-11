namespace RentalCommand.Core.Interfaces;

/// <summary>
/// An entity that belongs to a single portfolio (tenant). The audit interceptor reads
/// <see cref="PortfolioId"/> to scope each <see cref="Entities.AtomicAuditLog"/> row it writes.
/// Most business entities already expose <c>public int PortfolioId { get; set; }</c>, which
/// satisfies this interface with no extra code.
/// </summary>
public interface IPortfolioScoped
{
    int PortfolioId { get; }
}
