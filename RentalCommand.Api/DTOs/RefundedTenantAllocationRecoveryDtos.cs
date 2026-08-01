using System.ComponentModel.DataAnnotations;

namespace RentalCommand.Api.DTOs;

public sealed class RecoverRefundedTenantAllocationRequest
{
    [Range(1, int.MaxValue)]
    public int TenantAccountId { get; set; }

    [Range(1, long.MaxValue)]
    public long ExistingAllocationId { get; set; }

    [Range(1, long.MaxValue)]
    public long ExpectedDebitEntryId { get; set; }

    [Range(1, long.MaxValue)]
    public long ExpectedCreditEntryId { get; set; }

    [Range(1, long.MaxValue)]
    public long ExpectedRefundPaymentAttemptId { get; set; }

    [Range(typeof(decimal), "0.01", "999999999999.99")]
    public decimal ExpectedAllocationAmount { get; set; }

    [Range(typeof(decimal), "0.01", "999999999999.99")]
    public decimal ExpectedRefundAmount { get; set; }

    [Required, StringLength(80)]
    public string FinancialReference { get; set; } = string.Empty;
}
