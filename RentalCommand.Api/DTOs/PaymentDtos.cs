using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

public sealed class TenantAccountOptionQuery : ListQuery
{
}

public sealed class TenantAccountOptionResponse
{
    public int TenantAccountId { get; init; }
    public int LeaseManagementId { get; init; }
    public int PropertyId { get; init; }
    public int UnitId { get; init; }
    public string AccountNumber { get; init; } = string.Empty;
    public string RelationshipNumber { get; init; } = string.Empty;
    public string PropertyName { get; init; } = string.Empty;
    public string UnitNumber { get; init; } = string.Empty;
    public string? PrimaryTenantName { get; init; }
}

public sealed class TenantAccountOptionListResponse
{
    public IReadOnlyList<TenantAccountOptionResponse> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; }
}

public class PaymentListResponse
{
    public IReadOnlyList<PaymentReceiptResponse> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; }
}

/// <summary>
/// One immutable receipt credit on a continuous TenantAccount. This is the canonical payment read
/// shape; contractual charges and their open balances are separate ledger entries/projections.
/// </summary>
public sealed class PaymentReceiptResponse
{
    public long Id { get; init; }
    public Guid PublicId { get; init; }
    public int PortfolioId { get; init; }
    public int TenantAccountId { get; init; }
    public int LeaseManagementId { get; init; }
    public int PropertyId { get; init; }
    public int UnitId { get; init; }
    public string AccountNumber { get; init; } = string.Empty;
    public string RelationshipNumber { get; init; } = string.Empty;
    public string? TenantName { get; init; }
    public string? PropertyName { get; init; }
    public string? UnitNumber { get; init; }
    public decimal Amount { get; init; }
    public string Currency { get; init; } = string.Empty;
    public DateOnly ReceivedOn { get; init; }
    public DateTime PostedAtUtc { get; init; }
    public string Description { get; init; } = string.Empty;
    public string? Provider { get; init; }
    public string? ProviderReference { get; init; }
    public TenantPaymentAttemptState? ProviderState { get; init; }
    public string? PaymentMethodSummary { get; init; }
    public string? PayerName { get; init; }
    public string? CheckNumber { get; init; }
    public string? BankName { get; init; }
    public int? SourceStoredFileId { get; init; }
}

public class PaymentListQuery : ListQuery
{
    [FromQuery(Name = "tenantAccountId")]
    [Range(1, int.MaxValue)]
    public int? TenantAccountId { get; set; }

    [FromQuery(Name = "leaseManagementId")]
    [Range(1, int.MaxValue)]
    public int? LeaseManagementId { get; set; }

    [FromQuery(Name = "dueFrom")]
    public DateTime? DueFrom { get; set; }

    [FromQuery(Name = "dueTo")]
    public DateTime? DueTo { get; set; }

    [FromQuery(Name = "paidFrom")]
    public DateTime? PaidFrom { get; set; }

    [FromQuery(Name = "paidTo")]
    public DateTime? PaidTo { get; set; }
}
