namespace RentalCommand.Core.Entities;

public class BankStatement
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int BankConnectionId { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public decimal OpeningBalance { get; set; }
    public decimal ClosingBalance { get; set; }
    public decimal StatementMovement { get; set; }
    public string IsoCurrencyCode { get; set; } = "USD";
    public DateTime ImportedAtUtc { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public BankConnection? BankConnection { get; set; }
}
