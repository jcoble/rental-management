using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Time;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="ISandboxService"/>
public sealed class SandboxService : ISandboxService
{
    private readonly RentalCommandDbContext _db;
    private readonly ISelfOwnerProvisioner _selfOwnerProvisioner;
    private readonly Auth.DemoDataSeeder _demoSeeder;
    private readonly ILogger<SandboxService> _logger;
    private readonly TimeProvider _timeProvider;

    public SandboxService(
        RentalCommandDbContext db,
        ISelfOwnerProvisioner selfOwnerProvisioner,
        Auth.DemoDataSeeder demoSeeder,
        ILogger<SandboxService> logger,
        TimeProvider timeProvider)
    {
        _db = db;
        _selfOwnerProvisioner = selfOwnerProvisioner;
        _demoSeeder = demoSeeder;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task<SandboxStateResponse?> GetStateAsync(int portfolioId, CancellationToken ct = default)
    {
        var portfolio = await _db.Portfolios
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == portfolioId, ct);

        return portfolio is null ? null : ToState(portfolio);
    }

    public async Task<SandboxStateResponse?> GoLiveAsync(int portfolioId, CancellationToken ct = default)
    {
        // Scope strictly to the caller's own portfolio (IDOR guard): we only ever load + mutate this id.
        var portfolio = await _db.Portfolios
            .FirstOrDefaultAsync(p => p.Id == portfolioId, ct);

        if (portfolio is null)
        {
            return null;
        }

        // Idempotent: already Live → nothing to wipe, just return the current state.
        if (!portfolio.IsSandbox)
        {
            return ToState(portfolio);
        }

        // Transactional: the data wipe and the flag flip commit together. A failure rolls everything
        // back so we can never end up Live-but-still-holding-demo-data (or vice-versa).
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        await WipePortfolioDataAsync(portfolioId, ct);

        portfolio.IsSandbox = false;
        portfolio.SandboxSeededAtUtc = null;
        portfolio.UpdatedAt = _timeProvider.UtcNow();
        await _db.SaveChangesAsync(ct);

        // The wipe removed the demo owners, so the fresh Live portfolio has none. Re-create the primary
        // self-owner from the landlord's own account so the getting-started "owner" step is satisfied
        // before they add a property (the wipe's SetNull FK already cleared the stale OwnerEntityId).
        // Best-effort: if no admin user resolves, skip rather than fail the graduation.
        await EnsureSelfOwnerAfterWipeAsync(portfolioId, ct);

        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "Portfolio {PortfolioId} graduated from Sandbox to Live — demo data wiped.", portfolioId);

        return ToState(portfolio);
    }

    public async Task<SandboxStateResponse?> ApplyOnboardingChoiceAsync(
        int portfolioId, OnboardingChoice choice, CancellationToken ct = default)
    {
        // Scope strictly to the caller's own portfolio (IDOR guard): we only ever load + mutate this id.
        var portfolio = await _db.Portfolios
            .FirstOrDefaultAsync(p => p.Id == portfolioId, ct);

        if (portfolio is null)
        {
            return null;
        }

        // Idempotent: a decision was already recorded → do not re-seed or wipe; just return current state.
        // This makes the first-login gate safe against double-submits and repeat logins racing the redirect.
        if (!PortfolioOnboarding.IsPending(portfolio.Settings))
        {
            return ToState(portfolio);
        }

        var now = _timeProvider.UtcNow();

        if (choice == OnboardingChoice.Sandbox)
        {
            // Seed the demo dataset, then flip to a seeded Sandbox. The seed is fast and server-side; the
            // ~20s "Setting up your sandbox…" animation is client-side theater (no artificial server delay).
            // SeedPortfolioAsync is itself idempotent + atomic, so a retry can't double-seed.
            await _demoSeeder.SeedPortfolioAsync(portfolioId, ct);

            portfolio.IsSandbox = true;
            portfolio.SandboxSeededAtUtc = now;
        }
        else
        {
            // Live: an empty real portfolio. Registration already created it empty (no demo data) with the
            // self-owner provisioned, so there is nothing to seed or wipe — just stamp the decision below.
            portfolio.IsSandbox = false;
            portfolio.SandboxSeededAtUtc = null;
        }

        portfolio.Settings = PortfolioOnboarding.WriteChoice(portfolio.Settings, choice);
        portfolio.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Portfolio {PortfolioId} recorded first-login onboarding choice: {Choice}.", portfolioId, choice);

        return ToState(portfolio);
    }

    /// <summary>
    /// After a Go-Live wipe, recreates the portfolio's primary self-owner from its administering user.
    /// Resolves the owner-user as the earliest Admin-role-eligible account scoped to the portfolio; if no
    /// such user exists (e.g. a test fixture with no Identity users) it is a safe no-op.
    /// </summary>
    private async Task EnsureSelfOwnerAfterWipeAsync(int portfolioId, CancellationToken ct)
    {
        var user = await _db.Users
            .Where(u => u.PortfolioId == portfolioId && u.TenantId == null)
            .OrderBy(u => u.Id)
            .FirstOrDefaultAsync(ct);

        if (user is null)
        {
            _logger.LogWarning(
                "Go-Live for portfolio {PortfolioId}: no administering user found; skipped self-owner creation.",
                portfolioId);
            return;
        }

        await _selfOwnerProvisioner.EnsureSelfOwnerAsync(user, portfolioId, ct);
    }

    /// <summary>
    /// Deletes every portfolio-scoped DOMAIN row for the given portfolio, in child→parent order so no
    /// foreign-key constraint is violated. Set-based <c>ExecuteDeleteAsync</c> (with query filters
    /// ignored so soft-deleted rows go too) keeps the wipe fast and avoids loading thousands of entities.
    ///
    /// FK-order notes (the constraints that force ordering — the rest is plain child→parent):
    ///   * PaymentTransaction → Payment is RESTRICT → must delete transactions before payments.
    ///   * Conversation → Tenant is RESTRICT → must delete conversations (+ their messages) before tenants.
    ///   * Unit / ConversationMessage carry no PortfolioId → deleted via a join on their parent.
    /// The Portfolio row itself and account-level rows (the user's ApplicationUser/UserAccount, audit
    /// log, notification settings, outbox) are intentionally KEPT — only the demo domain data is wiped.
    /// </summary>
    private async Task WipePortfolioDataAsync(int portfolioId, CancellationToken ct)
    {
        // 1. Online-payment + autopay rows (PaymentTransaction RESTRICTs Payment).
        await _db.PaymentTransactions.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.AutopayEnrollments.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);

        // 2. Conversations + their messages (Conversation RESTRICTs Tenant; messages have no PortfolioId).
        await _db.ConversationMessages.IgnoreQueryFilters()
            .Where(m => _db.Conversations.Any(c => c.Id == m.ConversationId && c.PortfolioId == portfolioId))
            .ExecuteDeleteAsync(ct);
        await _db.Conversations.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);

        // 3. Tenant-screening chain (AdverseActionNotice / ScreeningResult → RentalApplication).
        await _db.AdverseActionNotices.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.ScreeningResults.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.RentalApplications.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);

        // 4. Inspections (items first → inspection).
        await _db.InspectionItems.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.Inspections.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);

        // 5. Appointments, canonical tenant/deposit ledgers, opening balances, payments, and notices.
        await _db.Appointments.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.SecurityDepositEntries.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.TenantLedgerAllocations.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.TenantLedgerEntries.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.TenantPaymentAttempts.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.TenantAutopayEnrollments.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.TenantAccountConditionPeriods.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.SecurityDepositAccounts.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.TenantAccounts.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.LeaseAgreementSigners.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.LeaseRenewalAddendumDecisions.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.LeaseAddendumSigners.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.LeaseAddendumFinancialEffects.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.LeaseAddenda.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.LeaseAgreements.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.TenantUserAccesses.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.LeaseManagementParties.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.UnitOperationalPeriods.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.LeaseManagements.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.OpeningBalances.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.Payments.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.NoticeDrafts.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);

        // 6. Bank feed (transactions → connection).
        await _db.BankTransactions.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.BankConnections.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);

        // 7. Work-order graph (status events → expenses → ratings/dispatches → work orders).
        await _db.WorkOrderStatusEvents.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.Expenses.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.VendorRatings.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.VendorDispatches.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.RecurringMaintenanceTasks.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.WorkOrders.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);

        // 8. Leases (now free of payments/deposits/work-orders/appointments referencing them).
        await _db.Leases.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);

        // 9. Units (no PortfolioId → join via Property) then Properties.
        await _db.Units.IgnoreQueryFilters()
            .Where(u => _db.Properties.IgnoreQueryFilters().Any(p => p.Id == u.PropertyId && p.PortfolioId == portfolioId))
            .ExecuteDeleteAsync(ct);
        await _db.Properties.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);

        // 10. People + companies (tenants now unreferenced by conversations/leases).
        await _db.Tenants.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.Vendors.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.OwnerEntities.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.Owners.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);

        // 11. Files, scan drafts/batches, activity (uploaded/captured demo artifacts).
        await _db.ScanDrafts.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.ScanBatches.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.StoredFiles.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
        await _db.AuditLogs.IgnoreQueryFilters()
            .Where(e => e.PortfolioId == portfolioId).ExecuteDeleteAsync(ct);
    }

    private static SandboxStateResponse ToState(Core.Entities.Portfolio p) => new()
    {
        PortfolioId = p.Id,
        IsSandbox = p.IsSandbox,
        SandboxSeededAtUtc = p.SandboxSeededAtUtc,
        OnboardingChoicePending = PortfolioOnboarding.IsPending(p.Settings),
    };
}
