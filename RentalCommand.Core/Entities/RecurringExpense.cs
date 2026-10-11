using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;

namespace RentalCommand.Core.Entities;

/// <summary>
/// A standing cost entered once (insurance, property tax, HOA, management fee, …) that the Engine
/// materializes into <see cref="Expense"/> rows on a schedule, so it flows into every report without
/// re-entry (spec §8). The worker advances <see cref="NextRunDate"/> atomically with generated
/// expenses, whose template/occurrence identity is protected by a database unique constraint.
/// </summary>
public class RecurringExpense : IAuditable, IPortfolioScoped
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }

    /// <summary>Property this cost belongs to (optional; null = portfolio-level).</summary>
    public int? PropertyId { get; set; }

    /// <summary>Unit this cost belongs to (optional).</summary>
    public int? UnitId { get; set; }

    /// <summary>Schedule E category stamped on the generated expense rows.</summary>
    public ScheduleECategory Category { get; set; } = ScheduleECategory.Other;

    /// <summary>Short description stamped on the generated expense rows.</summary>
    public string Description { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public RecurringExpenseFrequency Frequency { get; set; } = RecurringExpenseFrequency.Monthly;

    /// <summary>First date the cost applies; the schedule is anchored here.</summary>
    public DateTime StartDate { get; set; }

    /// <summary>Next date an expense should be materialized (advances by <see cref="Frequency"/>).</summary>
    public DateTime NextRunDate { get; set; }

    public bool Active { get; set; } = true;

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Soft-delete marker; null means active.</summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>Short-lived Engine ownership for recurring-expense generation.</summary>
    public string? WorkerClaimOwner { get; set; }
    public Guid? WorkerClaimToken { get; set; }
    public DateTime? WorkerClaimExpiresAtUtc { get; set; }
    public int WorkerClaimAttemptCount { get; set; }

    public Portfolio? Portfolio { get; set; }
    public Property? Property { get; set; }
    public Unit? Unit { get; set; }
}
