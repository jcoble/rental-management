using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Accounting;

/// <summary>Attaches the locked default chart rows that are missing for one portfolio.</summary>
public sealed class ChartOfAccountsSeedService
{
    private static readonly IReadOnlyList<DefaultAccount> Defaults =
    [
        new("1000", "Operating Cash", AccountType.Asset, "operating-cash"),
        new("1010", "Undeposited Funds", AccountType.Asset, "undeposited-funds"),
        new("1020", "Security Deposit Trust Cash", AccountType.Asset, "security-deposit-trust-cash"),
        new("1100", "Tenant Accounts Receivable", AccountType.Asset, "tenant-accounts-receivable"),
        new("1200", "Mortgage Escrow Asset", AccountType.Asset, "mortgage-escrow-asset"),
        new("1500", "Buildings and Improvements", AccountType.Asset, "buildings-and-improvements"),
        new("1510", "Land", AccountType.Asset, "land"),
        new("1590", "Accumulated Depreciation", AccountType.Asset, "accumulated-depreciation", NormalBalance.Credit),
        new("2000", "Accounts Payable", AccountType.Liability, "accounts-payable"),
        new("2100", "Tenant Security Deposits Payable", AccountType.Liability, "tenant-security-deposits-payable"),
        new("2200", "Mortgage Payable", AccountType.Liability, "mortgage-payable"),
        new("3000", "Owner Contributions", AccountType.Equity, "owner-contributions"),
        new("3100", "Owner Distributions", AccountType.Equity, "owner-distributions", NormalBalance.Debit),
        new("3200", "Retained Earnings", AccountType.Equity, "retained-earnings"),
        new("4000", "Rental Income", AccountType.Income, "rental-income"),
        new("4010", "Pet Income", AccountType.Income, "pet-income"),
        new("4020", "Parking Income", AccountType.Income, "parking-income"),
        new("4030", "Late Fee Income", AccountType.Income, "late-fee-income"),
        new("4040", "Utility Reimbursement Income", AccountType.Income, "utility-reimbursement-income"),
        new("4090", "Other Rental Income", AccountType.Income, "other-rental-income"),
        new("5000", "Repairs and Maintenance", AccountType.Expense, "repairs-and-maintenance"),
        new("5010", "Utilities", AccountType.Expense, "utilities"),
        new("5020", "Insurance", AccountType.Expense, "insurance"),
        new("5030", "Property Taxes", AccountType.Expense, "property-taxes"),
        new("5040", "Management Fees", AccountType.Expense, "management-fees"),
        new("5050", "Mortgage Interest", AccountType.Expense, "mortgage-interest"),
        new("5060", "Depreciation Expense", AccountType.Expense, "depreciation-expense"),
        new("5090", "Other Operating Expense", AccountType.Expense, "other-operating-expense"),
    ];

    private readonly RentalCommandDbContext _db;

    public ChartOfAccountsSeedService(RentalCommandDbContext db) => _db = db;

    /// <summary>
    /// Attaches missing defaults and returns the complete default rows. The caller's transaction
    /// owns persistence; this method deliberately does not call SaveChanges.
    /// </summary>
    public async Task<IReadOnlyList<LedgerAccount>> SeedAsync(int portfolioId, CancellationToken ct = default)
    {
        if (portfolioId <= 0)
            throw new ArgumentOutOfRangeException(nameof(portfolioId));

        var existingCodes = await _db.LedgerAccounts
            .Where(account => account.PortfolioId == portfolioId)
            .Select(account => account.Code)
            .ToListAsync(ct);
        var existing = existingCodes.ToHashSet(StringComparer.Ordinal);
        foreach (var tracked in _db.ChangeTracker.Entries<LedgerAccount>()
                     .Where(entry => entry.State == EntityState.Added && entry.Entity.PortfolioId == portfolioId))
            existing.Add(tracked.Entity.Code);
        var seeded = new List<LedgerAccount>(Defaults.Count);
        foreach (var definition in Defaults)
        {
            if (existing.Contains(definition.Code))
                continue;

            var account = new LedgerAccount
            {
                PortfolioId = portfolioId,
                Code = definition.Code,
                Name = definition.Name,
                AccountType = definition.Type,
                NormalBalance = definition.Balance ?? NormalBalanceFor(definition.Type),
                SystemKey = definition.SystemKey,
                IsSystem = true,
                IsActive = true,
            };
            _db.LedgerAccounts.Add(account);
            seeded.Add(account);
        }

        return seeded;
    }

    public static IReadOnlyList<(string Code, string Name, AccountType Type)> DefaultChart =>
        Defaults.Select(definition => (definition.Code, definition.Name, definition.Type)).ToArray();

    private static NormalBalance NormalBalanceFor(AccountType type) =>
        type is AccountType.Asset or AccountType.Expense ? NormalBalance.Debit : NormalBalance.Credit;

    private sealed record DefaultAccount(
        string Code,
        string Name,
        AccountType Type,
        string SystemKey,
        NormalBalance? Balance = null);
}
