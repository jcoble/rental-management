using System.ComponentModel.DataAnnotations;

namespace RentalCommand.Api.DTOs;

public sealed class RecoverHistoricalRentChargeRequest
{
    [Range(1, int.MaxValue)]
    public int TenantAccountId { get; set; }

    [Range(1, int.MaxValue)]
    public int LeaseAgreementId { get; set; }

    [Range(1, long.MaxValue)]
    public long ExistingRentChargeEntryId { get; set; }

    [Range(1, long.MaxValue)]
    public long ExistingReceiptEntryId { get; set; }

    [Range(1, long.MaxValue)]
    public long ExistingAllocationId { get; set; }

    public DateOnly ExpectedCurrentRentTrackingStartOn { get; set; }

    public DateOnly CorrectRentTrackingStartOn { get; set; }

    public DateOnly RentPeriodStartOn { get; set; }

    public DateOnly ExpectedExistingChargeDueOn { get; set; }

    [Range(typeof(decimal), "0.01", "999999999999.99")]
    public decimal ExpectedExistingChargeAmount { get; set; }

    [Range(typeof(decimal), "0.01", "999999999999.99")]
    public decimal ExpectedReceiptAmount { get; set; }

    [Range(typeof(decimal), "0.01", "999999999999.99")]
    public decimal CorrectRentAmount { get; set; }

    [Required, StringLength(80)]
    public string FinancialReference { get; set; } = string.Empty;
}

public sealed class RecoverLateFeeChargesRequest
{
    [MinLength(1)]
    public List<RecoverLateFeeChargeRowRequest> Corrections { get; set; } = [];

    [Range(1, int.MaxValue)]
    public int ExpectedReviewedChargeCount { get; set; }

    [Range(1, int.MaxValue)]
    public int ExpectedReversedChargeCount { get; set; }

    [Range(0, int.MaxValue)]
    public int ExpectedReplacementChargeCount { get; set; }

    [Range(typeof(decimal), "0.01", "999999999999.99")]
    public decimal ExpectedReversedTotal { get; set; }

    [Range(typeof(decimal), "0", "999999999999.99")]
    public decimal ExpectedReplacementTotal { get; set; }

    [Required, StringLength(80)]
    public string FinancialReference { get; set; } = string.Empty;
}

public sealed class RecoverLateFeeChargeRowRequest
{
    [Range(1, int.MaxValue)]
    public int TenantAccountId { get; set; }

    [Range(1, long.MaxValue)]
    public long ExistingLateFeeEntryId { get; set; }

    [Range(typeof(decimal), "0.01", "999999999999.99")]
    public decimal ExpectedExistingAmount { get; set; }

    [Range(typeof(decimal), "0", "999999999999.99")]
    public decimal ReplacementAmount { get; set; }

    public bool AlreadyReversed { get; set; }
}
