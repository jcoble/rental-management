using RentalCommand.Core.Enums;

namespace RentalCommand.Core.Entities;

public class Payment
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int LeaseId { get; set; }
    public PaymentType PaymentType { get; set; } = PaymentType.Rent;
    public PaymentStatus Status { get; set; } = PaymentStatus.Scheduled;
    public decimal Amount { get; set; }
    public DateTime DueDate { get; set; }
    public DateTime? PaidDate { get; set; }
    public string? Method { get; set; }
    public string? ExternalReference { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Portfolio? Portfolio { get; set; }
    public Lease? Lease { get; set; }
}
