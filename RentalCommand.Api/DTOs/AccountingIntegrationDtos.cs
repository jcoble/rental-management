using RentalCommand.Core.Enums;

namespace RentalCommand.Api.DTOs;

// ---------------------------------------------------------------------------
// API DTOs for the provider-agnostic accounting-INTEGRATION backbone (connect a
// landlord's external accounting system — QuickBooks is provider #1).
//
// Kept separate from AccountingDtos.cs, which holds the unrelated in-app
// Schedule-E accounting WORKSPACE DTOs. The web settings shell renders one card
// per available provider from the status list, so a 2nd provider needs zero new
// UI code (AC-1 at the UI layer).
// ---------------------------------------------------------------------------

/// <summary>Result of starting a connect flow — the provider authorize URL to redirect the browser to.</summary>
public class StartAccountingConnectResponse
{
    public string AuthorizeUrl { get; set; } = string.Empty;
}

/// <summary>One card per available accounting provider (connected or not), for the settings shell.</summary>
public class AccountingConnectionStatusResponse
{
    public AccountingProvider Provider { get; set; }

    /// <summary>Provider display name (e.g. "QuickBooks") for the UI — never branched on in code.</summary>
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>False when server creds are absent → the "not configured — ask your admin" state (AC-8).</summary>
    public bool Configured { get; set; }

    /// <summary>Null when the portfolio has never connected this provider.</summary>
    public AccountingConnectionStatus? Status { get; set; }

    public string? CompanyName { get; set; }
    public DateTime? ConnectedAt { get; set; }
    public DateTime? LastSyncedAt { get; set; }
    public string? LastError { get; set; }

    public bool PullEnabled { get; set; }
    public bool PushEnabled { get; set; }

    /// <summary>Imported transactions awaiting landlord review (NeedsReview/Unmatched ledger rows). 0 in Phase 1.</summary>
    public int PendingReviewCount { get; set; }

    /// <summary>Transactions successfully imported into the domain. 0 in Phase 1.</summary>
    public int ImportedCount { get; set; }

    /// <summary>Per-resource pull/push capabilities, or null when no implementation is registered yet.</summary>
    public AccountingCapabilitiesDto? Capabilities { get; set; }
}

/// <summary>Wire shape of the provider capability matrix.</summary>
public class AccountingCapabilitiesDto
{
    public bool CanPullCustomers { get; set; }
    public bool CanPullVendors { get; set; }
    public bool CanPullAccounts { get; set; }
    public bool CanPullPayments { get; set; }
    public bool CanPullExpenses { get; set; }
    public bool CanPushIncome { get; set; }
    public bool CanPushExpense { get; set; }
}

/// <summary>Per-direction toggle request — the landlord enables whichever direction(s) they need.</summary>
public class SetAccountingDirectionRequest
{
    public bool PullEnabled { get; set; }
    public bool PushEnabled { get; set; }
}

/// <summary>Optional date range for a one-time / backfill import (deltas + idempotency make it advisory).</summary>
public class RunAccountingImportRequest
{
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
}

/// <summary>Result counts for a one-time import run.</summary>
public class RunAccountingImportResponse
{
    public int CustomersMapped { get; set; }
    public int VendorsMapped { get; set; }
    public int AccountsMapped { get; set; }
    public int PaymentsImported { get; set; }
    public int ExpensesImported { get; set; }
    public int NeedsReview { get; set; }
}

/// <summary>One entity mapping (suggested or confirmed) for the mapping-review panel.</summary>
public class AccountingMappingResponse
{
    public int Id { get; set; }

    /// <summary>"Customer" | "Vendor" | "Account" | "Class".</summary>
    public string ExternalType { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;
    public string? ExternalDisplayName { get; set; }

    /// <summary>"Tenant" | "Lease" | "Vendor" | "Property" | "ScheduleECategory".</summary>
    public string LocalEntityType { get; set; } = string.Empty;
    public int? LocalEntityId { get; set; }

    /// <summary>Set instead of <see cref="LocalEntityId"/> when the target is an enum (a Schedule-E category).</summary>
    public string? LocalEnumValue { get; set; }

    /// <summary>The suggester's confidence (0–1) when surfaced; null for a hand-created confirm.</summary>
    public decimal? Confidence { get; set; }

    /// <summary>True once the landlord (or an unambiguous auto-link) confirmed the mapping.</summary>
    public bool Confirmed { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public long Revision { get; set; }
}

/// <summary>
/// Confirm (or hand-create) a mapping between an external entity and a local one. Either
/// <see cref="LocalEntityId"/> (Tenant/Vendor/Property/…) or <see cref="LocalEnumValue"/>
/// (a Schedule-E category) is set, matching the external type.
/// </summary>
public class ConfirmAccountingMappingRequest
{
    public string ClientOperationId { get; set; } = string.Empty;
    public long ExpectedRevision { get; set; }
    public string ExternalType { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;
    public string? ExternalDisplayName { get; set; }

    public string LocalEntityType { get; set; } = string.Empty;
    public int? LocalEntityId { get; set; }
    public string? LocalEnumValue { get; set; }
}

public class ConfirmAccountingMappingResponse
{
    public int MappingId { get; set; }
    public long MappingRevision { get; set; }
    public int Promoted { get; set; }
    public Guid? ContinuationId { get; set; }
    public bool HasMore { get; set; }
}

public class ContinueAccountingMappingPromotionRequest
{
    public string ClientOperationId { get; set; } = string.Empty;
}

public class ContinueAccountingMappingPromotionResponse
{
    public int Promoted { get; set; }
    public int TotalPromoted { get; set; }
    public bool HasMore { get; set; }
}

/// <summary>One imported transaction parked in the review queue (unmatched / needs-review).</summary>
public class AccountingReviewItemResponse
{
    public int Id { get; set; }

    /// <summary>"Payment" | "Purchase" | "Bill".</summary>
    public string ExternalType { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>"NeedsReview" | "Unmatched".</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Why it could not be auto-created (e.g. "No tenant mapping for this customer").</summary>
    public string? Reason { get; set; }
}
