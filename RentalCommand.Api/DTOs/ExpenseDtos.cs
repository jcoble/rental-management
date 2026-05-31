using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

/// <summary>Wire shape returned for an <see cref="Expense"/>.</summary>
public class ExpenseResponse
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int? PropertyId { get; set; }
    public int? VendorId { get; set; }
    public int? WorkOrderId { get; set; }
    public ScheduleECategory Category { get; set; }
    public string Description { get; set; } = string.Empty;
    public ExpenseStatus Status { get; set; }
    public decimal Amount { get; set; }
    public DateTime IncurredAt { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime? PaidAt { get; set; }
    public bool BillableToOwner { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Subtotal before tax, tip, and other charges (from scanned receipt).</summary>
    public decimal? Subtotal { get; set; }

    /// <summary>Tax amount charged (from scanned receipt).</summary>
    public decimal? TaxAmount { get; set; }

    /// <summary>
    /// Raw JSON string holding full receipt details (vendor contact, payment info, line items, etc.).
    /// Populated when the expense was created from a scan draft.
    /// </summary>
    public string? ReceiptData { get; set; }

    /// <summary>True when a <see cref="Core.Entities.StoredFile"/> is linked to this expense.</summary>
    public bool HasReceipt { get; set; }

    /// <summary>True when the linked file's content type starts with <c>image/</c>.</summary>
    public bool ReceiptIsImage { get; set; }

    /// <summary>Stable selector for frontend tests, e.g. <c>expense-1</c>.</summary>
    public string TestId => $"expense-{Id}";

    public static ExpenseResponse FromEntity(Expense e) => new()
    {
        Id = e.Id,
        PortfolioId = e.PortfolioId,
        PropertyId = e.PropertyId,
        VendorId = e.VendorId,
        WorkOrderId = e.WorkOrderId,
        Category = e.Category,
        Description = e.Description,
        Status = e.Status,
        Amount = e.Amount,
        IncurredAt = e.IncurredAt,
        DueDate = e.DueDate,
        PaidAt = e.PaidAt,
        BillableToOwner = e.BillableToOwner,
        Notes = e.Notes,
        CreatedAt = e.CreatedAt,
        UpdatedAt = e.UpdatedAt,
        Subtotal = e.Subtotal,
        TaxAmount = e.TaxAmount,
        ReceiptData = e.ReceiptData,
    };
}

public class CreateExpenseRequest
{
    public int? PropertyId { get; set; }
    public int? VendorId { get; set; }
    public int? WorkOrderId { get; set; }

    public ScheduleECategory Category { get; set; } = ScheduleECategory.Other;

    [Required]
    [MaxLength(500)]
    public string Description { get; set; } = string.Empty;

    public ExpenseStatus Status { get; set; } = ExpenseStatus.Pending;

    public decimal Amount { get; set; }

    [Required]
    public DateTime IncurredAt { get; set; }

    public DateTime? DueDate { get; set; }
    public DateTime? PaidAt { get; set; }
    public bool BillableToOwner { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }

    /// <summary>Subtotal before tax/tip; populated when creating from a scan draft.</summary>
    public decimal? Subtotal { get; set; }

    /// <summary>Tax amount; populated when creating from a scan draft.</summary>
    public decimal? TaxAmount { get; set; }

    /// <summary>Full receipt details JSON (jsonb); populated when creating from a scan draft.</summary>
    public string? ReceiptData { get; set; }
}

public class UpdateExpenseRequest
{
    public int? PropertyId { get; set; }
    public int? VendorId { get; set; }
    public int? WorkOrderId { get; set; }

    public ScheduleECategory? Category { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }

    public ExpenseStatus? Status { get; set; }
    public decimal? Amount { get; set; }
    public DateTime? IncurredAt { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime? PaidAt { get; set; }
    public bool? BillableToOwner { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }

    /// <summary>Subtotal before tax/tip.</summary>
    public decimal? Subtotal { get; set; }

    /// <summary>Tax amount.</summary>
    public decimal? TaxAmount { get; set; }

    /// <summary>Full receipt details JSON (jsonb); rebuilt by the edit form from its receipt fields.</summary>
    public string? ReceiptData { get; set; }
}
