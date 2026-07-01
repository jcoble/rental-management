using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Models.Accounting;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <summary>
/// The pull-into-domain import engine — the genuinely-new core of the accounting backbone.
/// EdiPlatform only ever pulled into a read-only cache; Rental Command pulls the landlord's
/// accounting Customers / Vendors / Accounts+Classes / Payments / Expenses and CREATES real RC
/// <see cref="Payment"/> / <see cref="Expense"/> rows, matched to the right lease/property/vendor via
/// confirm-driven <see cref="AccountingEntityMapping"/>s. Informed by EdiPlatform's
/// <c>ErpUpsertService</c> (auto-link-when-unambiguous heuristic) + <c>ErpPaymentReconService</c>
/// (pulled payment → domain Payment).
///
/// <para>
/// Provider-agnostic (AC-1): every external call goes through <see cref="IAccountingProvider"/>; this
/// service never names QuickBooks. Idempotent (AC-5): the unique
/// <c>(PortfolioId, ConnectionId, Direction, ExternalType, ExternalId)</c> index on
/// <see cref="AccountingSyncMap"/> gates re-import — an external txn already <c>Imported</c> is skipped.
/// Confirm-driven (AC-6): a money-in/out row is only created when its entity mapping is confirmed
/// (or unambiguously auto-linked); everything else lands in the review queue, never silently created,
/// never dropped (D-3, D-8).
/// </para>
///
/// <para>
/// DB-side discipline: mapping candidates (Tenants/Vendors/Properties) and the existing-mapping /
/// existing-ledger sets are loaded ONCE per batch (never per-transaction); re-import eligibility is a
/// set difference computed from those batch loads, not a per-row query.
/// </para>
/// </summary>
public sealed class AccountingImportService
{
    // Auto-link thresholds (D-5): a name match auto-confirms only when unambiguous.
    private const decimal AutoLinkMinConfidence = 0.80m;
    private const decimal AutoLinkRivalMargin = 0.15m;

    private readonly RentalCommandDbContext _db;
    private readonly IDataProtector _protector;
    private readonly AccountingProviderResolver _providerResolver;
    private readonly AccountingAppSettingsResolver _settingsResolver;
    private readonly AccountingTokenService _tokenService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AccountingImportService> _logger;

    public AccountingImportService(
        RentalCommandDbContext db,
        IDataProtectionProvider dataProtection,
        AccountingProviderResolver providerResolver,
        AccountingAppSettingsResolver settingsResolver,
        AccountingTokenService tokenService,
        TimeProvider timeProvider,
        ILogger<AccountingImportService> logger)
    {
        _db = db;
        // SAME protector label the connection service used to ENCRYPT the tokens (AC-4).
        _protector = dataProtection.CreateProtector("RentalCommand.Accounting.v1");
        _providerResolver = providerResolver;
        _settingsResolver = settingsResolver;
        _tokenService = tokenService;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Outcome counts for one import run, surfaced to the caller/endpoint.</summary>
    public sealed record ImportSummary(
        int CustomersMapped,
        int VendorsMapped,
        int AccountsMapped,
        int PaymentsImported,
        int ExpensesImported,
        int NeedsReview);

    /// <summary>
    /// Run an import for one connection. <paramref name="since"/> is the delta cursor (null = full /
    /// backfill). The reference entities are pulled and mapped first so the transaction pass can resolve
    /// against them. Each resource is isolated — one failing does not abort the rest — mirroring the
    /// pull worker's per-resource isolation.
    /// </summary>
    public async Task<ImportSummary> ImportAsync(
        AccountingConnection connection, DateTime? since, CancellationToken ct)
    {
        var provider = _providerResolver.Resolve(connection.Provider);
        // ctx is rebuilt (with a freshly-decrypted token) by PullWithRefreshAsync after a 401-driven
        // refresh, so it is a mutable local the per-resource pulls read through that helper.
        var ctx = BuildCallContext(connection);
        var caps = provider.Capabilities;

        var cursors = ParseCursors(connection.LastPulledAtJson);

        int customers = 0, vendors = 0, accounts = 0, payments = 0, expenses = 0, review = 0;

        // --- Reference entities → AccountingEntityMapping (suggested or auto-confirmed) -------------
        if (caps.CanPullCustomers)
        {
            (customers, var c) = await SafeAsync("customers", connection, async () =>
            {
                var pulled = await PullWithRefreshAsync(connection, c => provider.PullCustomersAsync(c, GetCursor(cursors, "customers", since), ct), ct);
                var n = await MapCustomersAsync(connection, pulled.Items, ct);
                SetCursor(cursors, "customers", pulled.MaxUpdatedAtUtc);
                return n;
            });
        }

        if (caps.CanPullVendors)
        {
            (vendors, _) = await SafeAsync("vendors", connection, async () =>
            {
                var pulled = await PullWithRefreshAsync(connection, c => provider.PullVendorsAsync(c, GetCursor(cursors, "vendors", since), ct), ct);
                var n = await MapVendorsAsync(connection, pulled.Items, ct);
                SetCursor(cursors, "vendors", pulled.MaxUpdatedAtUtc);
                return n;
            });
        }

        if (caps.CanPullAccounts)
        {
            (accounts, _) = await SafeAsync("accounts", connection, async () =>
            {
                var pulled = await PullWithRefreshAsync(connection, c => provider.PullAccountsAsync(c, GetCursor(cursors, "accounts", since), ct), ct);
                var n = await MapAccountsAsync(connection, pulled.Items, ct);
                SetCursor(cursors, "accounts", pulled.MaxUpdatedAtUtc);
                return n;
            });
        }

        // --- Transactions → real RC Payment / Expense rows (idempotent via the ledger) ------------
        if (caps.CanPullPayments)
        {
            (payments, var rv) = await SafeAsync("payments", connection, async () =>
            {
                var pulled = await PullWithRefreshAsync(connection, c => provider.PullPaymentsAsync(c, GetCursor(cursors, "payments", since), ct), ct);
                var r = await ImportPaymentsAsync(connection, pulled.Items, ct);
                SetCursor(cursors, "payments", pulled.MaxUpdatedAtUtc);
                return r;
            });
            review += rv;
        }

        if (caps.CanPullExpenses)
        {
            (expenses, var rv) = await SafeAsync("expenses", connection, async () =>
            {
                var pulled = await PullWithRefreshAsync(connection, c => provider.PullExpensesAsync(c, GetCursor(cursors, "expenses", since), ct), ct);
                var r = await ImportExpensesAsync(connection, pulled.Items, ct);
                SetCursor(cursors, "expenses", pulled.MaxUpdatedAtUtc);
                return r;
            });
            review += rv;
        }

        // Local: run a provider pull; on a 401/unauthorized, refresh the token ONCE (provider-agnostic,
        // via the shared AccountingTokenService), rebuild ctx with the new token, and retry once. A dead
        // refresh token surfaces as a clear error (the connection is already flipped to NeedsReconnect).
        async Task<T> PullWithRefreshAsync<T>(
            AccountingConnection conn, Func<AcctCallCtx, Task<T>> call, CancellationToken token)
        {
            try
            {
                return await call(ctx);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                var refresh = await _tokenService.RefreshAsync(_db, conn, token);
                if (refresh.Outcome != AccountingTokenService.RefreshOutcome.Refreshed || refresh.AccessToken == null)
                {
                    throw new InvalidOperationException(
                        $"Accounting access token for connection {conn.Id} is unauthorized and could not be refreshed — reconnect required.", ex);
                }

                // Rebuild ctx with the freshly-decrypted access token and retry the call once.
                ctx = ctx with { AccessToken = refresh.AccessToken };
                return await call(ctx);
            }
        }

        // Persist delta cursors + last-sync on the connection.
        connection.LastPulledAtJson = JsonSerializer.Serialize(cursors);
        connection.LastSyncedAt = _timeProvider.UtcNow();
        connection.UpdatedAt = _timeProvider.UtcNow();
        if (connection.Status == AccountingConnectionStatus.Error)
        {
            connection.Status = AccountingConnectionStatus.Connected;
            connection.LastError = null;
        }
        await _db.SaveChangesAsync(ct);

        return new ImportSummary(customers, vendors, accounts, payments, expenses, review);
    }

    /// <summary>
    /// Re-run the transaction import for the rows that were parked as <c>NeedsReview</c>/<c>Unmatched</c>
    /// for a now-confirmed mapping. Called after the landlord confirms an entity mapping so the pending
    /// money-in/out for that external entity flows into the domain without a full re-pull. DB-side:
    /// each retry lane reloads only its relevant parked external type rows.
    /// </summary>
    public async Task<int> RetryPendingForConnectionAsync(AccountingConnection connection, CancellationToken ct)
    {
        var parkedQuery = _db.AccountingSyncMaps
            .Where(m => m.PortfolioId == connection.PortfolioId
                && m.AccountingConnectionId == connection.Id
                && m.Direction == LedgerDirection.Import
                && (m.Status == LedgerStatus.NeedsReview || m.Status == LedgerStatus.Unmatched)
                && m.LocalEntityId == null);

        // Rehydrate the minimal external shape from the stored metadata so we can re-resolve. We stored
        // the raw QBO element in MetadataJson at first sight; re-decode it for the second attempt.
        var paymentRows = await parkedQuery
            .Where(m => m.ExternalType == ExternalKind.Payment)
            .ToListAsync(ct);
        var expenseRows = await parkedQuery
            .Where(m => m.ExternalType == ExternalKind.Purchase || m.ExternalType == ExternalKind.Bill)
            .ToListAsync(ct);

        int promoted = 0;
        if (paymentRows.Count > 0)
        {
            promoted += await PromoteParkedPaymentsAsync(connection, paymentRows, ct);
        }

        if (expenseRows.Count > 0)
        {
            promoted += await PromoteParkedExpensesAsync(connection, expenseRows, ct);
        }

        if (promoted > 0)
        {
            connection.UpdatedAt = _timeProvider.UtcNow();
            await _db.SaveChangesAsync(ct);
        }

        return promoted;
    }

    // =====================================================================================
    // Reference-entity mapping (Customer→Tenant, Vendor→Vendor, Account/Class→Property/Category)
    // =====================================================================================

    private async Task<int> MapCustomersAsync(
        AccountingConnection conn, IReadOnlyList<ExtCustomerDto> customers, CancellationToken ct)
    {
        if (customers.Count == 0)
        {
            return 0;
        }

        // DB-side: load candidate Tenants ONCE for the whole batch (not per-customer).
        var tenants = await _db.Tenants
            .Where(t => t.PortfolioId == conn.PortfolioId && t.DeletedAt == null)
            .Select(t => new { t.Id, t.FirstName, t.LastName, t.Email })
            .ToListAsync(ct);

        var existing = await LoadExistingMappingsAsync(conn, ExternalKind.Customer, ct);

        int n = 0;
        foreach (var c in customers)
        {
            var (localId, confidence, autoConfirm) = ResolveBest(
                c.DisplayName,
                tenants.Select(t => (t.Id, Name: $"{t.FirstName} {t.LastName}", Secondary: t.Email)).ToList(),
                c.Email);

            UpsertMapping(
                conn, existing, ExternalKind.Customer, c.ExternalId, c.DisplayName,
                LocalEntityKind.Tenant, localId, confidence, autoConfirm);
            n++;
        }

        await _db.SaveChangesAsync(ct);
        return n;
    }

    private async Task<int> MapVendorsAsync(
        AccountingConnection conn, IReadOnlyList<ExtVendorDto> vendors, CancellationToken ct)
    {
        if (vendors.Count == 0)
        {
            return 0;
        }

        var rcVendors = await _db.Vendors
            .Where(v => v.PortfolioId == conn.PortfolioId && v.DeletedAt == null)
            .Select(v => new { v.Id, v.Name, v.TaxId })
            .ToListAsync(ct);

        var existing = await LoadExistingMappingsAsync(conn, ExternalKind.Vendor, ct);

        int n = 0;
        foreach (var v in vendors)
        {
            var (localId, confidence, autoConfirm) = ResolveBest(
                v.DisplayName,
                rcVendors.Select(x => (x.Id, x.Name, Secondary: (string?)x.TaxId)).ToList(),
                v.TaxId);

            UpsertMapping(
                conn, existing, ExternalKind.Vendor, v.ExternalId, v.DisplayName,
                LocalEntityKind.Vendor, localId, confidence, autoConfirm);
            n++;
        }

        await _db.SaveChangesAsync(ct);
        return n;
    }

    /// <summary>
    /// Accounts map to a Schedule-E category (auto by the §3.3 name table; unmapped → Other + review
    /// hint, D-4); classes map to a Property by name. Both land in <see cref="AccountingEntityMapping"/>.
    /// </summary>
    private async Task<int> MapAccountsAsync(
        AccountingConnection conn, IReadOnlyList<ExtAccountDto> accounts, CancellationToken ct)
    {
        if (accounts.Count == 0)
        {
            return 0;
        }

        var properties = await _db.Properties
            .Where(p => p.PortfolioId == conn.PortfolioId && p.DeletedAt == null)
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(ct);

        var existingAccounts = await LoadExistingMappingsAsync(conn, ExternalKind.Account, ct);
        var existingClasses = await LoadExistingMappingsAsync(conn, ExternalKind.Class, ct);

        int n = 0;
        foreach (var a in accounts)
        {
            if (a.Kind == AccountingListKind.Class)
            {
                var (localId, confidence, autoConfirm) = ResolveBest(
                    a.Name, properties.Select(p => (p.Id, p.Name, Secondary: (string?)null)).ToList(), null);

                UpsertMapping(
                    conn, existingClasses, ExternalKind.Class, a.ExternalId, a.Name,
                    LocalEntityKind.Property, localId, confidence, autoConfirm);
            }
            else
            {
                var category = ScheduleECategoryMap.FromAccountName(a.Name);
                // The §3.3 table always resolves (Other is the catch-all); auto-confirm a named match,
                // leave the Other fallback as a suggestion so the landlord can map it (D-4).
                var matched = category != ScheduleECategory.Other;
                UpsertEnumMapping(
                    conn, existingAccounts, ExternalKind.Account, a.ExternalId, a.Name,
                    LocalEntityKind.ScheduleECategory, category.ToString(),
                    confidence: matched ? 1.0m : 0m, autoConfirm: matched);
            }

            n++;
        }

        await _db.SaveChangesAsync(ct);
        return n;
    }

    /// <summary>
    /// Best-match resolution over a candidate set using the shared <see cref="NameMatcher"/>. Returns
    /// the winning local id (or null), the winner's confidence, and whether it qualifies for auto-link
    /// (D-5: single candidate, or confidence ≥ 0.8 with no rival within 0.15).
    /// </summary>
    private static (int? LocalId, decimal Confidence, bool AutoConfirm) ResolveBest(
        string externalName,
        IReadOnlyList<(int Id, string Name, string? Secondary)> candidates,
        string? externalSecondary)
    {
        if (candidates.Count == 0)
        {
            return (null, 0m, false);
        }

        var scored = candidates
            .Select(c => (c.Id, Score: ScoreCandidate(externalName, externalSecondary, c.Name, c.Secondary)))
            .Where(x => x.Score > 0m)
            .OrderByDescending(x => x.Score)
            .ToList();

        if (scored.Count == 0)
        {
            return (null, 0m, false);
        }

        var top = scored[0];

        // Single viable candidate is unambiguous; otherwise require a clear lead over the runner-up.
        var rival = scored.Count > 1 ? scored[1].Score : 0m;
        var autoConfirm = scored.Count == 1
            ? top.Score >= AutoLinkMinConfidence
            : top.Score >= AutoLinkMinConfidence && (top.Score - rival) >= AutoLinkRivalMargin;

        return (top.Id, top.Score, autoConfirm);
    }

    private static decimal ScoreCandidate(
        string externalName, string? externalSecondary, string candidateName, string? candidateSecondary)
    {
        var nameScore = NameMatcher.NameMatchStrength(externalName, candidateName);

        // An exact secondary-key hit (email / tax id) is decisive — it lifts a weak name match to a
        // confident link (the same "named candidate outranks an unnamed one" idea as the bank matcher).
        if (!string.IsNullOrWhiteSpace(externalSecondary)
            && !string.IsNullOrWhiteSpace(candidateSecondary)
            && string.Equals(
                NameMatcher.NormalizeName(externalSecondary),
                NameMatcher.NormalizeName(candidateSecondary),
                StringComparison.Ordinal))
        {
            return 1m;
        }

        return nameScore;
    }

    // =====================================================================================
    // Transaction import (Payment, Expense) — create/link real RC rows, idempotent via ledger
    // =====================================================================================

    private async Task<(int Imported, int Review)> ImportPaymentsAsync(
        AccountingConnection conn, IReadOnlyList<ExtPaymentDto> dtos, CancellationToken ct)
    {
        if (dtos.Count == 0)
        {
            return (0, 0);
        }

        // DB-side idempotency: in ONE query, find which of THIS batch's external ids were already
        // imported (bounded by the batch, not the whole history), then process only the unseen ones.
        var batchIds = dtos.Select(d => d.ExternalId).ToList();
        var seen = await LoadSeenExternalIdsAsync(conn, ExternalKind.Payment, batchIds, ct);

        // Confirmed Customer→Tenant mappings (batch) + their lease + deposit-account hints.
        var customerToTenant = await LoadConfirmedMapAsync(conn, ExternalKind.Customer, LocalEntityKind.Tenant, ct);
        var tenantActiveLease = await LoadActiveLeaseByTenantAsync(conn.PortfolioId, ct);
        var depositAccountIds = await LoadDepositAccountExternalIdsAsync(conn, ct);

        int imported = 0, review = 0;
        foreach (var dto in dtos)
        {
            if (seen.Contains(dto.ExternalId))
            {
                continue; // AC-5: already imported (or already parked) — never duplicate.
            }

            int? tenantId = dto.CustomerExternalId != null
                && customerToTenant.TryGetValue(dto.CustomerExternalId, out var t) ? t : null;

            int? leaseId = tenantId != null && tenantActiveLease.TryGetValue(tenantId.Value, out var l) ? l : null;

            if (leaseId == null)
            {
                // D-8: money-in that does not map to a known tenant→lease is NOT auto-created — it lands
                // in the review queue (owner contribution / transfer / unmapped), never silently created.
                WriteLedger(conn, ExternalKind.Payment, dto.ExternalId, LedgerStatus.Unmatched,
                    localType: null, localId: null,
                    note: tenantId == null ? "No tenant mapping for this customer" : "Tenant has no active lease",
                    metadata: SerializeParked(dto));
                review++;
                continue;
            }

            // D-9: deposit account ⇒ SecurityDeposit; otherwise default tenant-mapped money-in to Rent.
            var paymentType = IsDepositPayment(dto, depositAccountIds) ? PaymentType.SecurityDeposit : PaymentType.Rent;

            var payment = new Payment
            {
                PortfolioId = conn.PortfolioId,
                LeaseId = leaseId.Value,
                PaymentType = paymentType,
                Status = PaymentStatus.Paid,
                Amount = dto.Amount,
                DueDate = dto.TxnDateUtc,
                PaidDate = dto.TxnDateUtc,
                Method = dto.PaymentMethod,
                ExternalReference = dto.ReferenceNumber ?? dto.ExternalId,
                CreatedAt = _timeProvider.UtcNow(),
                UpdatedAt = _timeProvider.UtcNow(),
            };
            _db.Payments.Add(payment);
            await _db.SaveChangesAsync(ct); // need the generated Id for the ledger row

            WriteLedger(conn, ExternalKind.Payment, dto.ExternalId, LedgerStatus.Imported,
                localType: LocalEntityKind.Payment, localId: payment.Id, note: null, metadata: SerializeParked(dto));
            imported++;
        }

        await _db.SaveChangesAsync(ct);
        return (imported, review);
    }

    private async Task<(int Imported, int Review)> ImportExpensesAsync(
        AccountingConnection conn, IReadOnlyList<ExtExpenseDto> dtos, CancellationToken ct)
    {
        if (dtos.Count == 0)
        {
            return (0, 0);
        }

        var batchIds = dtos.Select(d => d.ExternalId).ToList();
        var seen = await LoadSeenExternalIdsAsync(conn, ExternalKind.Purchase, ExternalKind.Bill, batchIds, ct);

        var vendorMap = await LoadConfirmedMapAsync(conn, ExternalKind.Vendor, LocalEntityKind.Vendor, ct);
        var classToProperty = await LoadConfirmedMapAsync(conn, ExternalKind.Class, LocalEntityKind.Property, ct);
        var accountToCategory = await LoadConfirmedEnumMapAsync(conn, ExternalKind.Account, LocalEntityKind.ScheduleECategory, ct);

        int imported = 0, review = 0;
        foreach (var dto in dtos)
        {
            // The provider already told us Purchase vs Bill via the neutral SourceKind discriminator.
            var externalKind = NormalizeExpenseKind(dto.SourceKind);
            if (seen.Contains(dto.ExternalId))
            {
                continue;
            }

            int? vendorId = dto.VendorExternalId != null && vendorMap.TryGetValue(dto.VendorExternalId, out var v) ? v : null;
            int? propertyId = dto.ClassExternalId != null && classToProperty.TryGetValue(dto.ClassExternalId, out var p) ? p : null;

            ScheduleECategory category = ScheduleECategory.Other;
            if (dto.AccountExternalId != null && accountToCategory.TryGetValue(dto.AccountExternalId, out var cat))
            {
                category = cat;
            }

            // D-9: skip Depreciation on both directions (non-cash).
            if (category == ScheduleECategory.Depreciation)
            {
                WriteLedger(conn, externalKind, dto.ExternalId, LedgerStatus.NeedsReview,
                    localType: null, localId: null, note: "Depreciation is non-cash; skipped on import",
                    metadata: SerializeParked(dto));
                review++;
                continue;
            }

            // An expense needs a vendor OR a property/category to be meaningful; with neither it is
            // unmatched and parked for review (the landlord confirms a mapping or creates the vendor).
            if (vendorId == null && propertyId == null && category == ScheduleECategory.Other)
            {
                WriteLedger(conn, externalKind, dto.ExternalId, LedgerStatus.Unmatched,
                    localType: null, localId: null, note: "No vendor / property / category mapping resolved",
                    metadata: SerializeParked(dto));
                review++;
                continue;
            }

            var expense = new Expense
            {
                PortfolioId = conn.PortfolioId,
                PropertyId = propertyId,
                VendorId = vendorId,
                Category = category,
                Status = ExpenseStatus.Paid,
                Amount = dto.Amount,
                IncurredAt = dto.TxnDateUtc,
                PaidAt = dto.TxnDateUtc,
                Description = BuildExpenseDescription(dto),
                CreatedAt = _timeProvider.UtcNow(),
                UpdatedAt = _timeProvider.UtcNow(),
            };
            _db.Expenses.Add(expense);
            await _db.SaveChangesAsync(ct);

            WriteLedger(conn, externalKind, dto.ExternalId, LedgerStatus.Imported,
                localType: LocalEntityKind.Expense, localId: expense.Id, note: null, metadata: SerializeParked(dto));
            imported++;
        }

        await _db.SaveChangesAsync(ct);
        return (imported, review);
    }

    // =====================================================================================
    // Promote parked rows after a mapping is confirmed
    // =====================================================================================

    private async Task<int> PromoteParkedPaymentsAsync(
        AccountingConnection conn, List<AccountingSyncMap> parked, CancellationToken ct)
    {
        var customerToTenant = await LoadConfirmedMapAsync(conn, ExternalKind.Customer, LocalEntityKind.Tenant, ct);
        var tenantActiveLease = await LoadActiveLeaseByTenantAsync(conn.PortfolioId, ct);
        var depositAccountIds = await LoadDepositAccountExternalIdsAsync(conn, ct);

        int promoted = 0;
        foreach (var row in parked)
        {
            var dto = DeserializePayment(row);
            if (dto == null)
            {
                continue;
            }

            int? tenantId = dto.CustomerExternalId != null
                && customerToTenant.TryGetValue(dto.CustomerExternalId, out var t) ? t : null;
            int? leaseId = tenantId != null && tenantActiveLease.TryGetValue(tenantId.Value, out var l) ? l : null;
            if (leaseId == null)
            {
                continue; // still unresolved — leave it parked
            }

            var payment = new Payment
            {
                PortfolioId = conn.PortfolioId,
                LeaseId = leaseId.Value,
                PaymentType = IsDepositPayment(dto, depositAccountIds) ? PaymentType.SecurityDeposit : PaymentType.Rent,
                Status = PaymentStatus.Paid,
                Amount = dto.Amount,
                DueDate = dto.TxnDateUtc,
                PaidDate = dto.TxnDateUtc,
                Method = dto.PaymentMethod,
                ExternalReference = dto.ReferenceNumber ?? dto.ExternalId,
                CreatedAt = _timeProvider.UtcNow(),
                UpdatedAt = _timeProvider.UtcNow(),
            };
            _db.Payments.Add(payment);
            await _db.SaveChangesAsync(ct);

            MarkImported(row, LocalEntityKind.Payment, payment.Id);
            promoted++;
        }

        return promoted;
    }

    private async Task<int> PromoteParkedExpensesAsync(
        AccountingConnection conn, List<AccountingSyncMap> parked, CancellationToken ct)
    {
        var vendorMap = await LoadConfirmedMapAsync(conn, ExternalKind.Vendor, LocalEntityKind.Vendor, ct);
        var classToProperty = await LoadConfirmedMapAsync(conn, ExternalKind.Class, LocalEntityKind.Property, ct);
        var accountToCategory = await LoadConfirmedEnumMapAsync(conn, ExternalKind.Account, LocalEntityKind.ScheduleECategory, ct);

        int promoted = 0;
        foreach (var row in parked)
        {
            var dto = DeserializeExpense(row);
            if (dto == null)
            {
                continue;
            }

            int? vendorId = dto.VendorExternalId != null && vendorMap.TryGetValue(dto.VendorExternalId, out var v) ? v : null;
            int? propertyId = dto.ClassExternalId != null && classToProperty.TryGetValue(dto.ClassExternalId, out var p) ? p : null;
            ScheduleECategory category = dto.AccountExternalId != null
                && accountToCategory.TryGetValue(dto.AccountExternalId, out var cat) ? cat : ScheduleECategory.Other;

            if (category == ScheduleECategory.Depreciation
                || (vendorId == null && propertyId == null && category == ScheduleECategory.Other))
            {
                continue; // still skipped / unresolved
            }

            var expense = new Expense
            {
                PortfolioId = conn.PortfolioId,
                PropertyId = propertyId,
                VendorId = vendorId,
                Category = category,
                Status = ExpenseStatus.Paid,
                Amount = dto.Amount,
                IncurredAt = dto.TxnDateUtc,
                PaidAt = dto.TxnDateUtc,
                Description = BuildExpenseDescription(dto),
                CreatedAt = _timeProvider.UtcNow(),
                UpdatedAt = _timeProvider.UtcNow(),
            };
            _db.Expenses.Add(expense);
            await _db.SaveChangesAsync(ct);

            MarkImported(row, LocalEntityKind.Expense, expense.Id);
            promoted++;
        }

        return promoted;
    }

    // =====================================================================================
    // Mapping persistence helpers
    // =====================================================================================

    private async Task<Dictionary<string, AccountingEntityMapping>> LoadExistingMappingsAsync(
        AccountingConnection conn, string externalType, CancellationToken ct)
    {
        var rows = await _db.AccountingEntityMappings
            .Where(m => m.PortfolioId == conn.PortfolioId
                && m.AccountingConnectionId == conn.Id
                && m.ExternalType == externalType)
            .ToListAsync(ct);
        return rows.ToDictionary(m => m.ExternalId, m => m, StringComparer.Ordinal);
    }

    private void UpsertMapping(
        AccountingConnection conn,
        Dictionary<string, AccountingEntityMapping> existing,
        string externalType, string externalId, string? externalDisplayName,
        string localType, int? localId, decimal confidence, bool autoConfirm)
    {
        if (existing.TryGetValue(externalId, out var row))
        {
            // Never overwrite a landlord-confirmed mapping; only refresh display + an unconfirmed suggestion.
            row.ExternalDisplayName = externalDisplayName;
            if (row.ConfirmedAt == null)
            {
                row.LocalEntityType = localType;
                row.LocalEntityId = localId;
                row.LocalEnumValue = null;
                row.Confidence = confidence;
                if (autoConfirm && localId != null)
                {
                    row.ConfirmedAt = _timeProvider.UtcNow(); // system auto-link (ConfirmedByUserId stays null)
                }
            }

            row.UpdatedAt = _timeProvider.UtcNow();
            return;
        }

        _db.AccountingEntityMappings.Add(new AccountingEntityMapping
        {
            PortfolioId = conn.PortfolioId,
            AccountingConnectionId = conn.Id,
            ExternalType = externalType,
            ExternalId = externalId,
            ExternalDisplayName = externalDisplayName,
            LocalEntityType = localType,
            LocalEntityId = localId,
            Confidence = confidence,
            ConfirmedAt = autoConfirm && localId != null ? _timeProvider.UtcNow() : null,
            CreatedAt = _timeProvider.UtcNow(),
            UpdatedAt = _timeProvider.UtcNow(),
        });
    }

    private void UpsertEnumMapping(
        AccountingConnection conn,
        Dictionary<string, AccountingEntityMapping> existing,
        string externalType, string externalId, string? externalDisplayName,
        string localType, string enumValue, decimal confidence, bool autoConfirm)
    {
        if (existing.TryGetValue(externalId, out var row))
        {
            row.ExternalDisplayName = externalDisplayName;
            if (row.ConfirmedAt == null)
            {
                row.LocalEntityType = localType;
                row.LocalEntityId = null;
                row.LocalEnumValue = enumValue;
                row.Confidence = confidence;
                if (autoConfirm)
                {
                    row.ConfirmedAt = _timeProvider.UtcNow();
                }
            }

            row.UpdatedAt = _timeProvider.UtcNow();
            return;
        }

        _db.AccountingEntityMappings.Add(new AccountingEntityMapping
        {
            PortfolioId = conn.PortfolioId,
            AccountingConnectionId = conn.Id,
            ExternalType = externalType,
            ExternalId = externalId,
            ExternalDisplayName = externalDisplayName,
            LocalEntityType = localType,
            LocalEntityId = null,
            LocalEnumValue = enumValue,
            Confidence = confidence,
            ConfirmedAt = autoConfirm ? _timeProvider.UtcNow() : null,
            CreatedAt = _timeProvider.UtcNow(),
            UpdatedAt = _timeProvider.UtcNow(),
        });
    }

    /// <summary>Confirmed external-id → local-entity-id map for a (external,local) type pair.</summary>
    private async Task<Dictionary<string, int>> LoadConfirmedMapAsync(
        AccountingConnection conn, string externalType, string localType, CancellationToken ct)
    {
        var rows = await _db.AccountingEntityMappings
            .Where(m => m.PortfolioId == conn.PortfolioId
                && m.AccountingConnectionId == conn.Id
                && m.ExternalType == externalType
                && m.LocalEntityType == localType
                && m.ConfirmedAt != null
                && m.LocalEntityId != null)
            .Select(m => new { m.ExternalId, LocalId = m.LocalEntityId!.Value })
            .ToListAsync(ct);
        return rows.ToDictionary(x => x.ExternalId, x => x.LocalId, StringComparer.Ordinal);
    }

    private async Task<Dictionary<string, ScheduleECategory>> LoadConfirmedEnumMapAsync(
        AccountingConnection conn, string externalType, string localType, CancellationToken ct)
    {
        var rows = await _db.AccountingEntityMappings
            .Where(m => m.PortfolioId == conn.PortfolioId
                && m.AccountingConnectionId == conn.Id
                && m.ExternalType == externalType
                && m.LocalEntityType == localType
                && m.ConfirmedAt != null
                && m.LocalEnumValue != null)
            .Select(m => new { m.ExternalId, m.LocalEnumValue })
            .ToListAsync(ct);

        var map = new Dictionary<string, ScheduleECategory>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (Enum.TryParse<ScheduleECategory>(r.LocalEnumValue, out var cat))
            {
                map[r.ExternalId] = cat;
            }
        }

        return map;
    }

    /// <summary>External ids of confirmed Account mappings whose category resolves to a deposit (D-9).</summary>
    private async Task<HashSet<string>> LoadDepositAccountExternalIdsAsync(
        AccountingConnection conn, CancellationToken ct)
    {
        // Deposit accounts are recognised by name at pull time; here we just need the set of QBO account
        // ids whose stored display name reads as a deposit account.
        var rows = await _db.AccountingEntityMappings
            .Where(m => m.PortfolioId == conn.PortfolioId
                && m.AccountingConnectionId == conn.Id
                && m.ExternalType == ExternalKind.Account
                && m.ExternalDisplayName != null
                && (m.ExternalDisplayName.ToLower().Contains("security deposit")
                    || m.ExternalDisplayName.ToLower().Contains("deposit held")
                    || m.ExternalDisplayName.ToLower().Contains("tenant deposit")))
            .Select(m => m.ExternalId)
            .ToListAsync(ct);

        return rows.ToHashSet(StringComparer.Ordinal);
    }

    private async Task<Dictionary<int, int>> LoadActiveLeaseByTenantAsync(int portfolioId, CancellationToken ct)
    {
        // One current lease per tenant where available (most recent start wins if several). A tenant who
        // gave notice still has a live lease and may still be paying rent, so NoticeGiven counts too.
        var rows = await _db.Leases
            .Where(l => l.PortfolioId == portfolioId
                && l.DeletedAt == null
                && (l.Status == LeaseStatus.Active || l.Status == LeaseStatus.NoticeGiven))
            .GroupBy(l => l.TenantId)
            .Select(g => new
            {
                TenantId = g.Key,
                LeaseId = g
                    .OrderByDescending(l => l.StartDate)
                    .ThenByDescending(l => l.Id)
                    .Select(l => l.Id)
                    .First(),
            })
            .ToListAsync(ct);

        return rows.ToDictionary(x => x.TenantId, x => x.LeaseId);
    }

    // =====================================================================================
    // Ledger helpers
    // =====================================================================================

    private Task<HashSet<string>> LoadSeenExternalIdsAsync(
        AccountingConnection conn, string type, IReadOnlyList<string> batchIds, CancellationToken ct)
        => LoadSeenExternalIdsAsync(conn, type, type, batchIds, ct);

    /// <summary>
    /// Which of THIS batch's external ids were already imported for this connection — bounded by the
    /// batch (EF → <c>WHERE ExternalId = ANY(@batch)</c>), never the whole connection history, so the
    /// query load does not grow with total imported volume (the DB-side/scale HARD RULE).
    /// </summary>
    private async Task<HashSet<string>> LoadSeenExternalIdsAsync(
        AccountingConnection conn, string typeA, string typeB, IReadOnlyList<string> batchIds, CancellationToken ct)
    {
        if (batchIds.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var ids = await _db.AccountingSyncMaps
            .Where(m => m.PortfolioId == conn.PortfolioId
                && m.AccountingConnectionId == conn.Id
                && m.Direction == LedgerDirection.Import
                && (m.ExternalType == typeA || m.ExternalType == typeB)
                && batchIds.Contains(m.ExternalId))
            .Select(m => m.ExternalId)
            .ToListAsync(ct);
        return ids.ToHashSet(StringComparer.Ordinal);
    }

    private void WriteLedger(
        AccountingConnection conn, string externalType, string externalId, string status,
        string? localType, int? localId, string? note, string? metadata)
    {
        _db.AccountingSyncMaps.Add(new AccountingSyncMap
        {
            PortfolioId = conn.PortfolioId,
            AccountingConnectionId = conn.Id,
            Direction = LedgerDirection.Import,
            ExternalType = externalType,
            ExternalId = externalId,
            LocalEntityType = localType,
            LocalEntityId = localId,
            Status = status,
            AttemptCount = 1,
            LastError = note,
            LastAttemptAt = _timeProvider.UtcNow(),
            // Stash the raw external payload so a later confirm can re-resolve without a fresh pull.
            MetadataJson = metadata,
            CreatedAt = _timeProvider.UtcNow(),
            UpdatedAt = _timeProvider.UtcNow(),
        });
    }

    private void MarkImported(AccountingSyncMap row, string localType, int localId)
    {
        row.Status = LedgerStatus.Imported;
        row.LocalEntityType = localType;
        row.LocalEntityId = localId;
        row.AttemptCount += 1;
        row.LastError = null;
        row.LastAttemptAt = _timeProvider.UtcNow();
        row.UpdatedAt = _timeProvider.UtcNow();
    }

    // =====================================================================================
    // Misc helpers
    // =====================================================================================

    private AcctCallCtx BuildCallContext(AccountingConnection conn)
    {
        var accessToken = UnprotectOrThrow(conn.AccessTokenCipherText);
        var settings = _settingsResolver.Resolve(conn.Provider);
        var realm = conn.ExternalAccountId
            ?? throw new InvalidOperationException(
                $"Connection {conn.Id} has no external account id (realm) — cannot call the provider.");
        return new AcctCallCtx(realm, accessToken, settings.UseSandbox);
    }

    private string UnprotectOrThrow(string? cipherText)
    {
        if (string.IsNullOrWhiteSpace(cipherText))
        {
            throw new InvalidOperationException("Connection has no access token — reconnect required.");
        }

        try
        {
            return _protector.Unprotect(cipherText);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("Stored access token could not be decrypted — reconnect required.", ex);
        }
    }

    private async Task<(int Value, int Review)> SafeAsync(
        string resource, AccountingConnection conn, Func<Task<int>> action)
    {
        try
        {
            return (await action(), 0);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Accounting import resource {Resource} failed for connection {ConnectionId} — continuing with other resources",
                resource, conn.Id);
            return (0, 0);
        }
    }

    private async Task<(int Imported, int Review)> SafeAsync(
        string resource, AccountingConnection conn, Func<Task<(int Imported, int Review)>> action)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Accounting import resource {Resource} failed for connection {ConnectionId} — continuing with other resources",
                resource, conn.Id);
            return (0, 0);
        }
    }

    private static bool IsDepositPayment(ExtPaymentDto dto, HashSet<string> depositAccountIds)
    {
        // Purely neutral: the provider already projected the deposit-to account id onto the DTO, so a
        // money-in that landed in a known deposit/liability account is a security deposit (D-9). No
        // provider-shaped JSON is parsed here — the backbone stays 100% provider-agnostic (AC-1).
        return dto.DepositAccountExternalId != null
            && depositAccountIds.Contains(dto.DepositAccountExternalId);
    }

    /// <summary>Map the provider's neutral <c>SourceKind</c> to the ledger's expense external type.</summary>
    private static string NormalizeExpenseKind(string? sourceKind)
        => string.Equals(sourceKind, ExternalKind.Bill, StringComparison.OrdinalIgnoreCase)
            ? ExternalKind.Bill
            : ExternalKind.Purchase;

    private static string BuildExpenseDescription(ExtExpenseDto dto)
        => !string.IsNullOrWhiteSpace(dto.ReferenceNumber)
            ? $"Imported expense {dto.ReferenceNumber}"
            : $"Imported expense {dto.ExternalId}";

    // --- Parked-row payload: store + restore the NEUTRAL DTO (provider-agnostic; AC-1) -----------
    //
    // We park the provider-neutral DTO the provider already projected, NOT the raw provider payload —
    // so a confirm-driven retry round-trips through pure neutral types and the generic import service
    // never re-parses provider-shaped JSON (which would silently mis-read a different provider's payload).

    private static string SerializeParked<T>(T dto) => JsonSerializer.Serialize(dto);

    private static ExtPaymentDto? DeserializePayment(AccountingSyncMap row)
        => DeserializeNeutral<ExtPaymentDto>(row.MetadataJson);

    private static ExtExpenseDto? DeserializeExpense(AccountingSyncMap row)
        => DeserializeNeutral<ExtExpenseDto>(row.MetadataJson);

    private static T? DeserializeNeutral<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, DateTime> ParseCursors(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return new Dictionary<string, DateTime>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, DateTime>>(json) ?? new Dictionary<string, DateTime>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, DateTime>();
        }
    }

    private static DateTime? GetCursor(IReadOnlyDictionary<string, DateTime> cursors, string key, DateTime? fallback)
        => cursors.TryGetValue(key, out var v) ? v : fallback;

    private static void SetCursor(Dictionary<string, DateTime> cursors, string key, DateTime? value)
    {
        if (value.HasValue)
        {
            cursors[key] = value.Value;
        }
    }
}
