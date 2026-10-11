namespace RentalCommand.Core.Models.Accounting;

// ---------------------------------------------------------------------------
// Stringly-typed vocabulary for the accounting ledger + mapping rows. These
// values are persisted in AccountingSyncMap.Direction/Status/ExternalType and
// AccountingEntityMapping.ExternalType/LocalEntityType. Centralizing the literals
// here keeps the import service, endpoints, status counts, and tests in lockstep
// (no scattered "Imported"/"Customer" magic strings that drift apart).
//
// They are provider-NEUTRAL: "Customer"/"Vendor"/"Account"/"Class"/"Payment"/
// "Purchase"/"Bill" are accounting-domain words, not QuickBooks types — every
// provider projects its native objects onto this small vocabulary.
// ---------------------------------------------------------------------------

/// <summary>Sync direction stored in <c>AccountingSyncMap.Direction</c>.</summary>
public static class LedgerDirection
{
    public const string Import = "Import";
    public const string Push = "Push";
}

/// <summary>Per-row sync state stored in <c>AccountingSyncMap.Status</c>.</summary>
public static class LedgerStatus
{
    public const string Imported = "Imported";
    public const string Pushed = "Pushed";
    public const string NeedsReview = "NeedsReview";
    public const string Unmatched = "Unmatched";
    public const string Failed = "Failed";
}

/// <summary>External object kind stored in <c>ExternalType</c> (ledger + mapping rows).</summary>
public static class ExternalKind
{
    // Reference entities (mapping rows).
    public const string Customer = "Customer";
    public const string Vendor = "Vendor";
    public const string Account = "Account";
    public const string Class = "Class";

    // Transactions (ledger rows).
    public const string Payment = "Payment";
    public const string Purchase = "Purchase";
    public const string Bill = "Bill";
    public const string SalesReceipt = "SalesReceipt";
}

/// <summary>Local Rental Command entity kind stored in <c>LocalEntityType</c>.</summary>
public static class LocalEntityKind
{
    public const string Tenant = "Tenant";
    public const string Lease = "Lease";
    public const string Vendor = "Vendor";
    public const string Property = "Property";
    public const string ScheduleECategory = "ScheduleECategory";

    // Created/linked transaction targets.
    public const string TenantLedgerEntry = "TenantLedgerEntry";
    public const string Expense = "Expense";
}
