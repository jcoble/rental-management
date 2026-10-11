using System.ComponentModel.DataAnnotations;

namespace RentalCommand.Api.DTOs;

public sealed class RecoverOpeningSecurityDepositsRequest
{
    public DateOnly EffectiveOn { get; set; }

    [Range(1, 500)]
    public int ExpectedAccountCount { get; set; }

    [Range(typeof(decimal), "0.01", "999999999999.99")]
    public decimal ExpectedTotal { get; set; }

    [Required, StringLength(80)]
    public string FinancialReference { get; set; } = string.Empty;
}
