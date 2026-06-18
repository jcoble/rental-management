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
