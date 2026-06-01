using System.Text.Json;
using RentalCommand.Core.Entities;

namespace RentalCommand.Api.DTOs;

/// <summary>A single itemised deduction from a security deposit.</summary>
public record DepositDeduction(string Reason, decimal Amount, string? Notes);

/// <summary>Wire shape returned for a <see cref="SecurityDepositHolding"/>.</summary>
public class SecurityDepositResponse
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int LeaseId { get; set; }
    public string? LeaseNumber { get; set; }
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

    public static SecurityDepositResponse FromEntity(SecurityDepositHolding e)
    {
        var deductions = string.IsNullOrWhiteSpace(e.DeductionsJson)
            ? []
            : JsonSerializer.Deserialize<List<DepositDeduction>>(e.DeductionsJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
              ?? [];

        var totalDeductions = deductions.Sum(d => d.Amount);
        var netRefund = Math.Max(0m, e.Amount - totalDeductions);

        return new SecurityDepositResponse
        {
            Id = e.Id,
            PortfolioId = e.PortfolioId,
            LeaseId = e.LeaseId,
            LeaseNumber = e.Lease?.LeaseNumber,
            Amount = e.Amount,
            Status = e.Status.ToString(),
            HeldAt = e.HeldAt,
            ReturnedAt = e.ReturnedAt,
            ReturnedAmount = e.ReturnedAmount,
            Deductions = deductions,
            TotalDeductions = totalDeductions,
            NetRefund = netRefund,
            Notes = e.Notes,
            CreatedAt = e.CreatedAt,
            UpdatedAt = e.UpdatedAt,
        };
    }
}

/// <summary>Create a new security deposit holding, optionally overriding the lease's deposit amount.</summary>
public record CreateDepositRequest(int LeaseId, decimal? Amount, string? Notes);

/// <summary>Append an itemised deduction to an existing holding.</summary>
public record AddDeductionRequest(string Reason, decimal Amount, string? Notes);

/// <summary>Finalise the deposit return. Net refund is computed from Amount minus all deductions.</summary>
public record ReturnDepositRequest(string? Notes);
