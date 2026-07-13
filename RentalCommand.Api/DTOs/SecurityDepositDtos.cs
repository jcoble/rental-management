namespace RentalCommand.Api.DTOs;

/// <summary>A single itemised deduction from a security deposit.</summary>
public record DepositDeduction(string Reason, decimal Amount, string? Notes);

public class SecurityDepositListResponse
{
    public IReadOnlyList<SecurityDepositAccountResponse> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; }
}

/// <summary>Canonical deposit-subledger read model for one continuous tenant account.</summary>
public sealed class SecurityDepositAccountResponse
{
    public int Id { get; init; }
    public int PortfolioId { get; init; }
    public int TenantAccountId { get; init; }
    public int LeaseManagementId { get; init; }
    public int OriginatingAgreementId { get; init; }
    public int PropertyId { get; init; }
    public int UnitId { get; init; }
    public string AccountNumber { get; init; } = string.Empty;
    public string RelationshipNumber { get; init; } = string.Empty;
    public string? TenantName { get; init; }
    public string? PropertyName { get; init; }
    public string? UnitNumber { get; init; }
    public string Currency { get; init; } = string.Empty;
    public decimal TotalReceived { get; init; }
    public decimal TotalDeductions { get; init; }
    public decimal TotalRefunded { get; init; }
    public decimal HeldBalance { get; init; }
    public string Status { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
}
