using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

/// <summary>Body for <c>POST /api/v1/work-orders/{id}/dispatch</c>: assign + text a vendor the job.</summary>
public class DispatchWorkOrderRequest
{
    [Required]
    [MaxLength(100)]
    public string IdempotencyKey { get; set; } = string.Empty;

    [Required]
    [Range(1, int.MaxValue)]
    public int VendorId { get; set; }

    /// <summary>Optional extra note appended to the job SMS the vendor receives.</summary>
    [MaxLength(500)]
    public string? Note { get; set; }
}

/// <summary>Wire shape returned for a created/updated <see cref="VendorDispatch"/>.</summary>
public class VendorDispatchResponse
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int WorkOrderId { get; set; }
    public int VendorId { get; set; }
    public VendorDispatchStatus Status { get; set; }
    public DateTime DispatchedAtUtc { get; set; }
    public DateTime? RespondedAtUtc { get; set; }
    public string? Message { get; set; }

    public static VendorDispatchResponse FromEntity(VendorDispatch e) => new()
    {
        Id = e.Id,
        PortfolioId = e.PortfolioId,
        WorkOrderId = e.WorkOrderId,
        VendorId = e.VendorId,
        Status = e.Status,
        DispatchedAtUtc = e.DispatchedAtUtc,
        RespondedAtUtc = e.RespondedAtUtc,
        Message = e.Message,
    };
}

/// <summary>Body for cancelling one open vendor dispatch while preserving dispatch history.</summary>
public sealed class CancelVendorDispatchRequest
{
    [Required]
    [MaxLength(100)]
    public string IdempotencyKey { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Reason { get; set; }
}

public sealed class CancelVendorDispatchResponse
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public int WorkOrderId { get; set; }
    public int VendorId { get; set; }
    public VendorDispatchStatus Status { get; set; }
    public DateTime CancelledAtUtc { get; set; }
    public string? Reason { get; set; }
    public bool Replayed { get; set; }
}

/// <summary>
/// Exact expected state for a supported repair of a pending vendor-dispatch chronology.
/// </summary>
public sealed class RecoverVendorDispatchChronologyRequest
{
    [Required]
    public DateTime ExpectedContaminatedDispatchedAtUtc { get; set; }

    [Range(1, int.MaxValue)]
    public int ExpectedStatusEventId { get; set; }

    [Range(1, long.MaxValue)]
    public long ExpectedOutboxId { get; set; }

    [Required, StringLength(300)]
    public string ExpectedOutboxIdempotencyKey { get; set; } = string.Empty;

    [Required, StringLength(200)]
    public string OriginalCommandIdempotencyKey { get; set; } = string.Empty;

    [Required]
    public DateTime CorrectDispatchedAtUtc { get; set; }
}

public sealed class RecoverVendorDispatchChronologyResponse
{
    public int WorkOrderId { get; set; }
    public int DispatchId { get; set; }
    public int StatusEventId { get; set; }
    public long OutboxId { get; set; }
    public DateTime DispatchedAtUtc { get; set; }
    public bool WorkOrderUpdatedAtRepaired { get; set; }
    public bool Replayed { get; set; }
}
