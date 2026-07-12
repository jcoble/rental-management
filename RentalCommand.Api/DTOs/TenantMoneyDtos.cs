using System.ComponentModel.DataAnnotations;

namespace RentalCommand.Api.DTOs;

public sealed class RecordTenantReceiptRequest
{
    [Range(0.01, 99999999)] public decimal Amount { get; set; }
    public DateOnly EffectiveOn { get; set; }
    [Required, MaxLength(500)] public string Description { get; set; } = string.Empty;
    [Required, MaxLength(200)] public string PaymentMethodSummary { get; set; } = string.Empty;
    [MaxLength(200)] public string? ExternalReference { get; set; }
    [MaxLength(200)] public string? PayerName { get; set; }
    [MaxLength(100)] public string? CheckNumber { get; set; }
    [MaxLength(200)] public string? BankName { get; set; }
    [Range(1, int.MaxValue)] public int? SourceStoredFileId { get; set; }
    public bool AllocateOldestCharges { get; set; } = true;
}

public sealed class FundSecurityDepositRequest
{
    [Range(1, int.MaxValue)] public int SecurityDepositAccountId { get; set; }
    [Range(0.01, 99999999)] public decimal Amount { get; set; }
    public DateOnly EffectiveOn { get; set; }
    [Required, MaxLength(500)] public string Description { get; set; } = string.Empty;
    [Required, MaxLength(200)] public string PaymentMethodSummary { get; set; } = string.Empty;
    [MaxLength(200)] public string? ExternalReference { get; set; }
    [Range(1, int.MaxValue)] public int? SourceStoredFileId { get; set; }
}

public sealed class DeductSecurityDepositRequest
{
    [Range(1, int.MaxValue)] public int SecurityDepositAccountId { get; set; }
    [Range(0.01, 99999999)] public decimal Amount { get; set; }
    public DateOnly EffectiveOn { get; set; }
    [Required, MaxLength(500)] public string Reason { get; set; } = string.Empty;
    [MaxLength(2000)] public string? Notes { get; set; }
    [Range(1, int.MaxValue)] public int? SourceStoredFileId { get; set; }
}

public sealed class RefundSecurityDepositRequest
{
    [Range(1, int.MaxValue)] public int SecurityDepositAccountId { get; set; }
    [Range(0.01, 99999999)] public decimal? Amount { get; set; }
    public DateOnly EffectiveOn { get; set; }
    [Required, MaxLength(500)] public string Description { get; set; } = string.Empty;
    [MaxLength(200)] public string? ExternalReference { get; set; }
}
