namespace RentalCommand.Core.Entities;

/// <summary>
/// Read-only PostgreSQL projection over the provider-neutral jsonb payload kept on a parked
/// accounting ledger row. The database view extracts the fields in SQL so promotion never loads
/// a connection's ledger and filters or shapes it in memory.
/// </summary>
public sealed class AccountingParkedTransaction
{
    public int Id { get; init; }
    public int PortfolioId { get; init; }
    public int AccountingConnectionId { get; init; }
    public string ExternalType { get; init; } = string.Empty;
    public string ExternalId { get; init; } = string.Empty;
    public string? CustomerExternalId { get; init; }
    public string? VendorExternalId { get; init; }
    public string? AccountExternalId { get; init; }
    public string? ClassExternalId { get; init; }
    public string? DepositAccountExternalId { get; init; }
    public decimal Amount { get; init; }
    public DateTime TxnDateUtc { get; init; }
    public string? PaymentMethod { get; init; }
    public string? ReferenceNumber { get; init; }
    public string? SourceKind { get; init; }
}
