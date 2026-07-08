using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Services;

namespace RentalCommand.Api.DTOs;

/// <summary>Wire shape returned for an <see cref="Expense"/>.</summary>
public class ExpenseResponse
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int? PropertyId { get; set; }
    public int? UnitId { get; set; }
    public int? VendorId { get; set; }
    public int? WorkOrderId { get; set; }
    public int? CapitalizedAssetId { get; set; }
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

    /// <summary>How the expense was paid (e.g. "Visa", "Cash"); from the scanned receipt.</summary>
    public string? PaymentMethod { get; set; }

    /// <summary>Last 4 digits of the card used; from the scanned receipt.</summary>
    public string? CardLast4 { get; set; }

    /// <summary>Document classification from the scan (e.g. "Receipt", "Bill").</summary>
    public string? DocumentKind { get; set; }

    /// <summary>Name of the linked property, when assigned. Projected from the navigation (single GET).</summary>
    public string? PropertyName { get; set; }

    /// <summary>Unit number of the linked unit, when assigned. Projected from the navigation (single GET).</summary>
    public string? UnitNumber { get; set; }

    /// <summary>Name of the linked vendor, when assigned. Projected from the navigation (single GET).</summary>
    public string? VendorName { get; set; }

    /// <summary>True when a <see cref="Core.Entities.StoredFile"/> is linked to this expense.</summary>
    public bool HasReceipt { get; set; }

    /// <summary>True when the linked file's content type starts with <c>image/</c>.</summary>
    public bool ReceiptIsImage { get; set; }

    /// <summary>
    /// Typed line items loaded from the <see cref="Core.Entities.ExpenseLineItem"/> child rows.
    /// Populated on the single-item GET; empty list on list endpoints (not loaded to avoid N+1).
    /// </summary>
    public IReadOnlyList<ExpenseLineItemResponse> LineItems { get; set; } = [];

    /// <summary>Stable selector for frontend tests, e.g. <c>expense-1</c>.</summary>
    public string TestId => $"expense-{Id}";

    public static ExpenseResponse FromEntity(Expense e) => new()
    {
        Id = e.Id,
        PortfolioId = e.PortfolioId,
        PropertyId = e.PropertyId,
        UnitId = e.UnitId,
        VendorId = e.VendorId,
        WorkOrderId = e.WorkOrderId,
        CapitalizedAssetId = e.CapitalizedAssetId,
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
        PaymentMethod = e.PaymentMethod,
        CardLast4 = e.CardLast4,
        DocumentKind = e.DocumentKind,
        // Populated only when the caller eager-loads the Property/Unit/Vendor navigations (single GET).
        PropertyName = e.Property?.Name,
        UnitNumber = e.Unit?.UnitNumber,
        VendorName = e.Vendor?.Name,
    };
}

public class ExpenseListResponse
{
    public IReadOnlyList<ExpenseResponse> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; }
}

public class ExpenseListQuery : ListQuery
{
    [FromQuery(Name = "incurredFrom")]
    public DateTime? IncurredFrom { get; set; }

    [FromQuery(Name = "incurredTo")]
    public DateTime? IncurredTo { get; set; }

    [FromQuery(Name = "dueFrom")]
    public DateTime? DueFrom { get; set; }

    [FromQuery(Name = "dueTo")]
    public DateTime? DueTo { get; set; }

    [FromQuery(Name = "paidFrom")]
    public DateTime? PaidFrom { get; set; }

    [FromQuery(Name = "paidTo")]
    public DateTime? PaidTo { get; set; }
}

/// <summary>One typed line item returned on an <see cref="ExpenseResponse"/>.</summary>
public class ExpenseLineItemResponse
{
    public string Description { get; set; } = string.Empty;
    public decimal? Quantity { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? Amount { get; set; }
    public int LineNumber { get; set; }

    public static ExpenseLineItemResponse FromEntity(ExpenseLineItem li) => new()
    {
        Description = li.Description,
        Quantity = li.Quantity,
        UnitPrice = li.UnitPrice,
        Amount = li.Amount,
        LineNumber = li.LineNumber,
    };
}

public class CreateExpenseRequest
{
    [Range(1, int.MaxValue)]
    public int? PropertyId { get; set; }

    /// <summary>Optional unit this expense belongs to (e.g. a unit-specific appliance or permit cost).</summary>
    [Range(1, int.MaxValue)]
    public int? UnitId { get; set; }

    [Range(1, int.MaxValue)]
    public int? VendorId { get; set; }

    [Range(1, int.MaxValue)]
    public int? WorkOrderId { get; set; }

    [EnumDataType(typeof(ScheduleECategory))]
    public ScheduleECategory Category { get; set; } = ScheduleECategory.Other;

    [Required]
    [MaxLength(500)]
    public string Description { get; set; } = string.Empty;

    [EnumDataType(typeof(ExpenseStatus))]
    public ExpenseStatus Status { get; set; } = ExpenseStatus.Pending;

    [Range(0.01, 99999999)]
    public decimal Amount { get; set; }

    [Required]
    public DateTime IncurredAt { get; set; }

    public DateTime? DueDate { get; set; }
    public DateTime? PaidAt { get; set; }
    public bool BillableToOwner { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }

    /// <summary>Subtotal before tax/tip; populated when creating from a scan draft.</summary>
    [Range(0, 99999999)]
    public decimal? Subtotal { get; set; }

    /// <summary>Tax amount; populated when creating from a scan draft.</summary>
    [Range(0, 99999999)]
    public decimal? TaxAmount { get; set; }

    /// <summary>Full receipt details JSON (jsonb); populated when creating from a scan draft.</summary>
    public string? ReceiptData { get; set; }

    /// <summary>How the expense was paid (e.g. "Visa", "Cash"); promoted from the scanned receipt.</summary>
    [MaxLength(100)]
    public string? PaymentMethod { get; set; }

    /// <summary>Last 4 digits of the card used; promoted from the scanned receipt.</summary>
    [MaxLength(20)]
    public string? CardLast4 { get; set; }

    /// <summary>Document classification from the scan (e.g. "Receipt", "Bill"); promoted from the scan.</summary>
    [MaxLength(50)]
    public string? DocumentKind { get; set; }

    /// <summary>Itemized lines from the scanned receipt; persisted as ExpenseLineItem child rows.</summary>
    public List<CreateExpenseLineItem> LineItems { get; set; } = new();
}

/// <summary>One scanned receipt line item to persist as an <see cref="Core.Entities.ExpenseLineItem"/>.</summary>
public class CreateExpenseLineItem
{
    [MaxLength(1000)]
    public string Description { get; set; } = string.Empty;

    public decimal? Quantity { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? Amount { get; set; }

    /// <summary>1-based position of this line within the receipt.</summary>
    public int LineNumber { get; set; }
}

public class UpdateExpenseRequest
{
    [Range(1, int.MaxValue)]
    public int? PropertyId { get; set; }

    /// <summary>Optional unit this expense belongs to (e.g. a unit-specific appliance or permit cost).</summary>
    [Range(1, int.MaxValue)]
    public int? UnitId { get; set; }

    [Range(1, int.MaxValue)]
    public int? VendorId { get; set; }

    [Range(1, int.MaxValue)]
    public int? WorkOrderId { get; set; }

    [EnumDataType(typeof(ScheduleECategory))]
    public ScheduleECategory? Category { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }

    [EnumDataType(typeof(ExpenseStatus))]
    public ExpenseStatus? Status { get; set; }

    [Range(0.01, 99999999)]
    public decimal? Amount { get; set; }

    public DateTime? IncurredAt { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime? PaidAt { get; set; }
    public bool? BillableToOwner { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }

    /// <summary>Subtotal before tax/tip.</summary>
    [Range(0, 99999999)]
    public decimal? Subtotal { get; set; }

    /// <summary>Tax amount.</summary>
    [Range(0, 99999999)]
    public decimal? TaxAmount { get; set; }

    /// <summary>Full receipt details JSON (jsonb); rebuilt by the edit form from its receipt fields.</summary>
    public string? ReceiptData { get; set; }

    /// <summary>Explicitly clears <see cref="ReceiptData"/> when the edit form empties the raw receipt JSON.</summary>
    public bool? ClearReceiptData { get; set; }

    /// <summary>
    /// When provided (even if empty), REPLACES all existing <see cref="Core.Entities.ExpenseLineItem"/>
    /// rows for this expense. Pass <c>null</c> to leave existing line items untouched.
    /// Each item's LineNumber is assigned 1-based in list order.
    /// </summary>
    public List<UpdateExpenseLineItem>? LineItems { get; set; }
}

/// <summary>One line item in an <see cref="UpdateExpenseRequest"/>; replaces existing rows when provided.</summary>
public class UpdateExpenseLineItem
{
    [MaxLength(1000)]
    public string Description { get; set; } = string.Empty;

    public decimal? Quantity { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? Amount { get; set; }
}

public class CapitalizeExpenseRequest
{
    [Required]
    public DateTime InServiceDate { get; set; }

    [EnumDataType(typeof(DepreciationMethod))]
    public DepreciationMethod Method { get; set; } = DepreciationMethod.StraightLine;

    [Range(1, 40)]
    public decimal RecoveryYears { get; set; } = RecoveryClass.ResidentialBuilding;

    [EnumDataType(typeof(DepreciationConvention))]
    public DepreciationConvention Convention { get; set; } = DepreciationConvention.MidMonth;

    [MaxLength(500)]
    public string? Description { get; set; }
}
