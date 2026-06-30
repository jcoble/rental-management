using System.ComponentModel.DataAnnotations;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

public enum AssistantActionKind
{
    Unsupported,
    CreateExpense
}

public enum AssistantActionRisk
{
    Low,
    Medium,
    High
}

public enum AssistantActionStatus
{
    DraftReady,
    MissingRequiredFields,
    WriteModeRequired,
    Unsupported,
    NotConfirmed,
    InvalidDraft,
    Created
}

public sealed class AssistantActionDraftRequest
{
    [Required]
    [MaxLength(2000)]
    public string Command { get; set; } = string.Empty;

    /// <summary>
    /// Explicit, caller-controlled write-mode opt-in. The server never executes actions unless this
    /// is true on the execute request, even if a draft already exists.
    /// </summary>
    public bool WriteModeEnabled { get; set; }
}

public sealed class AssistantActionExecuteRequest
{
    public bool WriteModeEnabled { get; set; }
    public bool Confirmed { get; set; }
    public AssistantActionDraft? Draft { get; set; }
}

public sealed class AssistantActionDraftResponse
{
    public AssistantActionStatus Status { get; set; }
    public string Message { get; set; } = string.Empty;
    public bool RequiresWriteMode { get; set; }
    public bool CanExecute { get; set; }
    public AssistantActionDraft? Draft { get; set; }
    public IReadOnlyList<string> MissingFields { get; set; } = [];
    public IReadOnlyList<AssistantActionOption> Options { get; set; } = [];
}

public sealed class AssistantActionExecuteResponse
{
    public AssistantActionStatus Status { get; set; }
    public string Message { get; set; } = string.Empty;
    public AssistantActionKind Kind { get; set; } = AssistantActionKind.Unsupported;
    public int? EntityId { get; set; }
    public string? DetailHref { get; set; }
    public ExpenseResponse? Expense { get; set; }
}

public sealed class AssistantActionDraft
{
    public AssistantActionKind Kind { get; set; }
    public AssistantActionRisk Risk { get; set; } = AssistantActionRisk.Medium;
    public string Summary { get; set; } = string.Empty;
    public AssistantExpenseDraft? Expense { get; set; }
}

public sealed class AssistantExpenseDraft
{
    public int? PropertyId { get; set; }
    public string? PropertyName { get; set; }
    public ScheduleECategory Category { get; set; } = ScheduleECategory.Other;
    public ExpenseStatus Status { get; set; } = ExpenseStatus.Paid;
    public string Description { get; set; } = string.Empty;
    public decimal? Amount { get; set; }
    public DateTime IncurredAt { get; set; }
    public DateTime? PaidAt { get; set; }
    public string? Notes { get; set; }
}

public sealed class AssistantActionOption
{
    public string Field { get; set; } = string.Empty;
    public int Id { get; set; }
    public string Label { get; set; } = string.Empty;
}
