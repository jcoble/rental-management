using System.ComponentModel.DataAnnotations;

namespace RentalCommand.Api.DTOs;

/// <summary>A single itemised deduction from a security deposit.</summary>
public record DepositDeduction(string Reason, decimal Amount, string? Notes);

public class SecurityDepositResponse
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int LeaseId { get; set; }
    public string? LeaseNumber { get; set; }

    /// <summary>Full name of the lease's tenant, when present. Projected from the Lease→Tenant navigation.</summary>
    public string? TenantName { get; set; }

    public decimal Amount { get; set; }
    public string Status { get; set; } = "Held";
    public DateTime HeldAt { get; set; }
    public DateTime? ReturnedAt { get; set; }
    public decimal? ReturnedAmount { get; set; }
    public IReadOnlyList<DepositDeduction> Deductions { get; set; } = [];
    public decimal TotalDeductions { get; set; }
    public decimal NetRefund { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

}

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

/// <summary>Create a new security deposit holding, optionally overriding the lease's deposit amount.</summary>
public class CreateDepositRequest
{
    [Required]
    [Range(1, int.MaxValue)]
    public int LeaseId { get; set; }

    [Range(0, 99999999)]
    public decimal? Amount { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }
}

/// <summary>Append an itemised deduction to an existing holding.</summary>
public class AddDeductionRequest
{
    [Required]
    [MaxLength(500)]
    public string Reason { get; set; } = string.Empty;

    [Range(0.01, 99999999)]
    public decimal Amount { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }
}

/// <summary>Finalise the deposit return. Net refund is computed from Amount minus all deductions.</summary>
public class ReturnDepositRequest
{
    [MaxLength(2000)]
    public string? Notes { get; set; }
}
