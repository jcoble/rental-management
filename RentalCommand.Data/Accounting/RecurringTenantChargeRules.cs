using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Accounting;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Operations;
using RentalCommand.Core.Outbox;
using RentalCommand.Data.Operations;
using RentalCommand.Data.Payments;

namespace RentalCommand.Data.Accounting;

public sealed class CreateRecurringTenantChargeRule
{
    private readonly RentalCommandDbContext _db;

    public CreateRecurringTenantChargeRule(RentalCommandDbContext db) => _db = db;

    public async Task<RecurringTenantChargeMutationResult> ExecuteAsync(
        CreateRecurringTenantChargeCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        RecurringTenantChargeCommandValidation.Validate(command);
        var securityNowUtc = await context.ReadDatabaseClockUtcAsync(ct);
        var target = await RecurringTenantChargeCommandSupport.LoadTenantTargetAsync(
            _db, command.PortfolioId, command.TenantAccountId, ct);
        if (target is null)
            return new(RecurringTenantChargeMutationOutcome.NotFound, 0);

        await RecurringTenantChargeCommandSupport.EnsureAuthorizedAsync(
            _db, command.PortfolioId, command.Actor, target.PropertyId,
            command.BusinessNowUtc, securityNowUtc, ct);
        await RecurringTenantChargeCommandSupport.EnsureLeaseMatchesAsync(
            _db, command.PortfolioId, command.LeaseAgreementId,
            target.LeaseManagementId, ct);
        await RecurringTenantChargeCommandSupport.EnsureActiveIncomeAccountAsync(
            _db, command.PortfolioId, command.LedgerAccountId, ct);
        RecurringTenantChargeCommandValidation.ValidateTenantTarget(target);

        var schedule = new RecurringTenantCharge
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = command.PortfolioId,
            TenantAccountId = target.TenantAccountId,
            LeaseAgreementId = command.LeaseAgreementId,
            DisplayName = command.DisplayName.Trim(),
            Amount = command.Amount,
            Currency = target.Currency,
            LedgerAccountId = command.LedgerAccountId,
            EffectiveStartOn = command.EffectiveStartOn,
            EffectiveEndOn = command.EffectiveEndOn,
            MonthlyDueDay = (short)command.MonthlyDueDay,
            NextRunDate = command.NextRunDate ?? command.EffectiveStartOn,
            IsActive = true,
            PropertyId = target.PropertyId,
            UnitId = target.UnitId,
            CreatedAtUtc = command.BusinessNowUtc,
            UpdatedAtUtc = command.BusinessNowUtc,
        };

        _db.RecurringTenantCharges.Add(schedule);
        context.UseDatabaseWallClockForAudit(command.BusinessNowUtc);
        context.BindSemanticAudit(schedule, RecurringTenantChargeCommandSupport.Audit(
            command.PortfolioId,
            schedule,
            AuditLogOperation.Created,
            command.Actor.UserId,
            oldValues: null,
            changeReason: "Created recurring tenant-charge configuration."));
        await context.FlushBusinessAsync(ct);

        var snapshot = RecurringTenantChargeCommandSupport.Snapshot(schedule);
        context.StageOutbox(RecurringTenantChargeCommandSupport.DataUpdate(
            command.PortfolioId,
            schedule.Id,
            "created",
            command.DeliveryIdempotencyKey,
            command.BusinessNowUtc,
            snapshot));
        return new(RecurringTenantChargeMutationOutcome.Created, schedule.Id, snapshot);
    }

    public async Task AuthorizeAsync(
        CreateRecurringTenantChargeCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        RecurringTenantChargeCommandValidation.Validate(command);
        var securityNowUtc = await _db.Database
            .SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"")
            .SingleAsync(ct);
        var target = await RecurringTenantChargeCommandSupport.LoadTenantTargetAsync(
            _db, command.PortfolioId, command.TenantAccountId, ct);
        if (target is null)
            throw new UnauthorizedAccessException(
                "The recurring charge tenant account is no longer available.");
        await RecurringTenantChargeCommandSupport.EnsureAuthorizedAsync(
            _db, command.PortfolioId, command.Actor, target.PropertyId,
            command.BusinessNowUtc, securityNowUtc, ct);
    }
}

public sealed class UpdateRecurringTenantChargeRule
{
    private readonly RentalCommandDbContext _db;

    public UpdateRecurringTenantChargeRule(RentalCommandDbContext db) => _db = db;

    public async Task<RecurringTenantChargeMutationResult> ExecuteAsync(
        UpdateRecurringTenantChargeCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        RecurringTenantChargeCommandValidation.Validate(command);
        var securityNowUtc = await context.ReadDatabaseClockUtcAsync(ct);
        var schedule = await _db.RecurringTenantCharges
            .Include(row => row.TenantAccount!)
                .ThenInclude(account => account.LeaseManagement!)
            .SingleOrDefaultAsync(row =>
                row.PortfolioId == command.PortfolioId
                && row.TenantAccountId == command.TenantAccountId
                && row.Id == command.RecurringTenantChargeId, ct);
        if (schedule?.TenantAccount?.LeaseManagement is null)
            return new(RecurringTenantChargeMutationOutcome.NotFound,
                command.RecurringTenantChargeId);

        var target = RecurringTenantChargeCommandSupport.Target(schedule.TenantAccount);
        await RecurringTenantChargeCommandSupport.EnsureAuthorizedAsync(
            _db, command.PortfolioId, command.Actor, target.PropertyId,
            command.BusinessNowUtc, securityNowUtc, ct);
        await RecurringTenantChargeCommandSupport.EnsureActiveIncomeAccountAsync(
            _db,
            command.PortfolioId,
            command.LedgerAccountId ?? schedule.LedgerAccountId,
            ct);
        RecurringTenantChargeCommandValidation.ValidateTenantTarget(target);

        var effectiveStartOn = command.EffectiveStartOn ?? schedule.EffectiveStartOn;
        var effectiveEndOn = command.EffectiveEndOn ?? schedule.EffectiveEndOn;
        if (effectiveEndOn is DateOnly finalEnd && finalEnd < effectiveStartOn)
            throw new ArgumentException(
                "The recurring charge end date cannot be before its start date.",
                nameof(command.EffectiveEndOn));

        var oldValues = RecurringTenantChargeCommandSupport.ConfigurationValues(schedule);
        if (command.DisplayName is not null)
            schedule.DisplayName = command.DisplayName.Trim();
        if (command.Amount is decimal amount)
            schedule.Amount = amount;
        if (command.LedgerAccountId is int ledgerAccountId)
            schedule.LedgerAccountId = ledgerAccountId;
        if (command.EffectiveStartOn is DateOnly startOn)
            schedule.EffectiveStartOn = startOn;
        if (command.EffectiveEndOn is DateOnly endOn)
            schedule.EffectiveEndOn = endOn;
        if (command.MonthlyDueDay is int monthlyDueDay)
            schedule.MonthlyDueDay = (short)monthlyDueDay;
        if (command.NextRunDate is DateOnly nextRunDate)
            schedule.NextRunDate = nextRunDate;
        schedule.Currency = target.Currency;
        schedule.PropertyId = target.PropertyId;
        schedule.UnitId = target.UnitId;
        schedule.UpdatedAtUtc = command.BusinessNowUtc;

        context.UseDatabaseWallClockForAudit(command.BusinessNowUtc);
        context.BindSemanticAudit(schedule, RecurringTenantChargeCommandSupport.Audit(
            command.PortfolioId,
            schedule,
            AuditLogOperation.Updated,
            command.Actor.UserId,
            oldValues,
            "Updated recurring tenant-charge configuration for future occurrences."));
        await context.FlushBusinessAsync(ct);

        var snapshot = RecurringTenantChargeCommandSupport.Snapshot(schedule);
        context.StageOutbox(RecurringTenantChargeCommandSupport.DataUpdate(
            command.PortfolioId,
            schedule.Id,
            "updated",
            command.DeliveryIdempotencyKey,
            command.BusinessNowUtc,
            snapshot));
        return new(RecurringTenantChargeMutationOutcome.Updated, schedule.Id, snapshot);
    }

    public async Task AuthorizeAsync(
        UpdateRecurringTenantChargeCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        RecurringTenantChargeCommandValidation.Validate(command);
        var securityNowUtc = await _db.Database
            .SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"")
            .SingleAsync(ct);
        var propertyId = await RecurringTenantChargeCommandSupport
            .LoadSchedulePropertyIdAsync(
                _db,
                command.PortfolioId,
                command.TenantAccountId,
                command.RecurringTenantChargeId,
                ct);
        if (propertyId is null)
            throw new UnauthorizedAccessException(
                "The recurring charge schedule is no longer available.");
        await RecurringTenantChargeCommandSupport.EnsureAuthorizedAsync(
            _db, command.PortfolioId, command.Actor, propertyId.Value,
            command.BusinessNowUtc, securityNowUtc, ct);
    }
}

public sealed class DeactivateRecurringTenantChargeRule
{
    private readonly RentalCommandDbContext _db;

    public DeactivateRecurringTenantChargeRule(RentalCommandDbContext db) => _db = db;

    public async Task<RecurringTenantChargeMutationResult> ExecuteAsync(
        DeactivateRecurringTenantChargeCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        RecurringTenantChargeCommandValidation.Validate(command);
        var securityNowUtc = await context.ReadDatabaseClockUtcAsync(ct);
        var schedule = await _db.RecurringTenantCharges
            .Include(row => row.TenantAccount!)
                .ThenInclude(account => account.LeaseManagement!)
            .SingleOrDefaultAsync(row =>
                row.PortfolioId == command.PortfolioId
                && row.TenantAccountId == command.TenantAccountId
                && row.Id == command.RecurringTenantChargeId, ct);
        if (schedule?.TenantAccount?.LeaseManagement is null)
            return new(RecurringTenantChargeMutationOutcome.NotFound,
                command.RecurringTenantChargeId);

        var target = RecurringTenantChargeCommandSupport.Target(schedule.TenantAccount);
        await RecurringTenantChargeCommandSupport.EnsureAuthorizedAsync(
            _db, command.PortfolioId, command.Actor, target.PropertyId,
            command.BusinessNowUtc, securityNowUtc, ct);
        RecurringTenantChargeCommandValidation.ValidateTenantTarget(target);

        var oldValues = RecurringTenantChargeCommandSupport.ConfigurationValues(schedule);
        schedule.IsActive = false;
        schedule.Currency = target.Currency;
        schedule.PropertyId = target.PropertyId;
        schedule.UnitId = target.UnitId;
        schedule.UpdatedAtUtc = command.BusinessNowUtc;

        context.UseDatabaseWallClockForAudit(command.BusinessNowUtc);
        context.BindSemanticAudit(schedule, RecurringTenantChargeCommandSupport.Audit(
            command.PortfolioId,
            schedule,
            AuditLogOperation.Updated,
            command.Actor.UserId,
            oldValues,
            "Deactivated recurring tenant-charge configuration."));
        await context.FlushBusinessAsync(ct);

        var snapshot = RecurringTenantChargeCommandSupport.Snapshot(schedule);
        context.StageOutbox(RecurringTenantChargeCommandSupport.DataUpdate(
            command.PortfolioId,
            schedule.Id,
            "deactivated",
            command.DeliveryIdempotencyKey,
            command.BusinessNowUtc,
            snapshot));
        return new(RecurringTenantChargeMutationOutcome.Deactivated, schedule.Id, snapshot);
    }

    public async Task AuthorizeAsync(
        DeactivateRecurringTenantChargeCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        RecurringTenantChargeCommandValidation.Validate(command);
        var securityNowUtc = await _db.Database
            .SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"")
            .SingleAsync(ct);
        var propertyId = await RecurringTenantChargeCommandSupport
            .LoadSchedulePropertyIdAsync(
                _db,
                command.PortfolioId,
                command.TenantAccountId,
                command.RecurringTenantChargeId,
                ct);
        if (propertyId is null)
            throw new UnauthorizedAccessException(
                "The recurring charge schedule is no longer available.");
        await RecurringTenantChargeCommandSupport.EnsureAuthorizedAsync(
            _db, command.PortfolioId, command.Actor, propertyId.Value,
            command.BusinessNowUtc, securityNowUtc, ct);
    }
}

internal static class RecurringTenantChargeCommandSupport
{
    internal static async Task<TenantAccountTarget?> LoadTenantTargetAsync(
        RentalCommandDbContext db,
        int portfolioId,
        int tenantAccountId,
        CancellationToken ct) =>
        await db.TenantAccounts.AsNoTracking()
            .Where(account => account.PortfolioId == portfolioId && account.Id == tenantAccountId)
            .Select(account => new TenantAccountTarget(
                account.Id,
                account.LeaseManagementId,
                account.Currency,
                account.LeaseManagement!.PropertyId,
                account.LeaseManagement.UnitId))
            .SingleOrDefaultAsync(ct);

    internal static TenantAccountTarget Target(TenantAccount account) => new(
        account.Id,
        account.LeaseManagementId,
        account.Currency,
        account.LeaseManagement!.PropertyId,
        account.LeaseManagement.UnitId);

    internal static async Task EnsureAuthorizedAsync(
        RentalCommandDbContext db,
        int portfolioId,
        StaffOperationActor actor,
        int propertyId,
        DateTime businessNowUtc,
        DateTime securityNowUtc,
        CancellationToken ct)
    {
        if (!await StaffOperationAuthorization.CanManagePropertyAsync(
                portfolioId,
                actor,
                propertyId,
                CapabilityKeys.MoneyChargesManage,
                db,
                businessNowUtc,
                securityNowUtc,
                ct))
            throw new UnauthorizedAccessException(
                "The active assignment cannot manage recurring charges for this property.");
    }

    internal static Task<bool> ActiveIncomeAccountExistsAsync(
        RentalCommandDbContext db,
        int portfolioId,
        int ledgerAccountId,
        CancellationToken ct) =>
        db.LedgerAccounts.AnyAsync(account =>
            account.PortfolioId == portfolioId
            && account.Id == ledgerAccountId
            && account.IsActive
            && account.AccountType == AccountType.Income, ct);

    internal static async Task EnsureActiveIncomeAccountAsync(
        RentalCommandDbContext db,
        int portfolioId,
        int ledgerAccountId,
        CancellationToken ct)
    {
        if (!await ActiveIncomeAccountExistsAsync(db, portfolioId, ledgerAccountId, ct))
            throw new ArgumentException(
                "The recurring charge account must be an active Income account in this portfolio.",
                nameof(ledgerAccountId));
    }

    internal static async Task EnsureLeaseMatchesAsync(
        RentalCommandDbContext db,
        int portfolioId,
        int leaseAgreementId,
        int leaseManagementId,
        CancellationToken ct)
    {
        if (!await db.LeaseAgreements.AnyAsync(agreement =>
                agreement.PortfolioId == portfolioId
                && agreement.Id == leaseAgreementId
                && agreement.LeaseManagementId == leaseManagementId, ct))
            throw new ArgumentException(
                "The lease agreement must belong to the tenant account's lease relationship.",
                nameof(leaseAgreementId));
    }

    internal static async Task<int?> LoadSchedulePropertyIdAsync(
        RentalCommandDbContext db,
        int portfolioId,
        int tenantAccountId,
        int scheduleId,
        CancellationToken ct) =>
        await db.RecurringTenantCharges.AsNoTracking()
            .Where(schedule =>
                schedule.PortfolioId == portfolioId
                && schedule.TenantAccountId == tenantAccountId
                && schedule.Id == scheduleId)
            .Select(schedule => (int?)schedule.TenantAccount!.LeaseManagement!.PropertyId)
            .SingleOrDefaultAsync(ct);

    internal static AtomicSemanticAudit Audit(
        int portfolioId,
        RecurringTenantCharge schedule,
        AuditLogOperation operation,
        int userId,
        object? oldValues,
        string changeReason) =>
        new(
            portfolioId,
            nameof(RecurringTenantCharge),
            operation == AuditLogOperation.Created ? 0 : schedule.Id,
            operation,
            UserId: userId,
            ActorLabel: "Staff",
            OldValues: oldValues is null ? null : JsonSerializer.Serialize(oldValues),
            NewValues: JsonSerializer.Serialize(ConfigurationValues(schedule)),
            ChangeReason: changeReason);

    internal static object ConfigurationValues(RecurringTenantCharge schedule) => new
    {
        schedule.Id,
        schedule.PublicId,
        schedule.TenantAccountId,
        schedule.LeaseAgreementId,
        schedule.DisplayName,
        schedule.Amount,
        schedule.Currency,
        schedule.LedgerAccountId,
        schedule.EffectiveStartOn,
        schedule.EffectiveEndOn,
        schedule.MonthlyDueDay,
        schedule.NextRunDate,
        schedule.IsActive,
        schedule.PropertyId,
        schedule.UnitId,
    };

    internal static RecurringTenantChargeMutationSnapshot Snapshot(
        RecurringTenantCharge schedule)
    {
        if (schedule.PropertyId is not int propertyId || schedule.UnitId is not int unitId)
            throw new InvalidOperationException(
                "A recurring tenant-charge snapshot requires derived property and unit dimensions.");
        return new(
            schedule.Id,
            schedule.PublicId,
            schedule.TenantAccountId,
            schedule.LeaseAgreementId,
            schedule.DisplayName,
            schedule.Amount,
            schedule.Currency,
            schedule.LedgerAccountId,
            schedule.EffectiveStartOn,
            schedule.EffectiveEndOn,
            schedule.MonthlyDueDay,
            schedule.NextRunDate,
            schedule.IsActive,
            propertyId,
            unitId);
    }

    internal static OutboxMessage DataUpdate(
        int portfolioId,
        int entityId,
        string operation,
        string deliveryIdempotencyKey,
        DateTime now,
        RecurringTenantChargeMutationSnapshot snapshot) => new()
    {
        PortfolioId = portfolioId,
        MessageType = "data-update",
        Payload = JsonSerializer.Serialize(new
        {
            entityType = nameof(RecurringTenantCharge),
            entityId,
            operation,
            data = snapshot,
        }),
        IdempotencyKey = OutboxIdempotency.Create(
            "recurring-tenant-charge-config", deliveryIdempotencyKey),
        CreatedAtUtc = now,
        NextAttemptAtUtc = now,
    };

    internal sealed record TenantAccountTarget(
        int TenantAccountId,
        int LeaseManagementId,
        string Currency,
        int PropertyId,
        int UnitId);
}

internal static class RecurringTenantChargeCommandValidation
{
    internal static void Validate(CreateRecurringTenantChargeCommand command)
    {
        ValidateCommon(command.PortfolioId, command.Actor, command.BusinessNowUtc,
            command.DeliveryIdempotencyKey);
        if (command.TenantAccountId <= 0 || command.LeaseAgreementId <= 0
            || command.LedgerAccountId <= 0 || command.EffectiveStartOn == default
            || command.Amount <= 0m || command.MonthlyDueDay is < 1 or > 31)
            throw new ArgumentException(
                "Tenant account, lease, account, positive amount, start date, and due day are required.");
        ValidateDisplayName(command.DisplayName);
        if (command.EffectiveEndOn is DateOnly end && end < command.EffectiveStartOn)
            throw new ArgumentException(
                "The recurring charge end date cannot be before its start date.",
                nameof(command.EffectiveEndOn));
        if (command.NextRunDate == DateOnly.MinValue)
            throw new ArgumentException(
                "The recurring charge next run date is invalid.", nameof(command.NextRunDate));
    }

    internal static void Validate(UpdateRecurringTenantChargeCommand command)
    {
        ValidateCommon(command.PortfolioId, command.Actor, command.BusinessNowUtc,
            command.DeliveryIdempotencyKey);
        if (command.TenantAccountId <= 0 || command.RecurringTenantChargeId <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(command.TenantAccountId), "The recurring charge target is invalid.");
        if (command.DisplayName is not null)
            ValidateDisplayName(command.DisplayName);
        if (command.Amount is <= 0m)
            throw new ArgumentException("The recurring charge amount must be positive.", nameof(command.Amount));
        if (command.LedgerAccountId is <= 0)
            throw new ArgumentException(
                "The recurring charge account must be positive.", nameof(command.LedgerAccountId));
        if (command.EffectiveStartOn == DateOnly.MinValue)
            throw new ArgumentException(
                "The recurring charge start date is invalid.", nameof(command.EffectiveStartOn));
        if (command.EffectiveEndOn == DateOnly.MinValue)
            throw new ArgumentException(
                "The recurring charge end date is invalid.", nameof(command.EffectiveEndOn));
        if (command.MonthlyDueDay is < 1 or > 31)
            throw new ArgumentException(
                "The recurring charge due day must be between 1 and 31.",
                nameof(command.MonthlyDueDay));
        if (command.NextRunDate == DateOnly.MinValue)
            throw new ArgumentException(
                "The recurring charge next run date is invalid.", nameof(command.NextRunDate));
    }

    internal static void Validate(DeactivateRecurringTenantChargeCommand command)
    {
        ValidateCommon(command.PortfolioId, command.Actor, command.BusinessNowUtc,
            command.DeliveryIdempotencyKey);
        if (command.TenantAccountId <= 0 || command.RecurringTenantChargeId <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(command.TenantAccountId), "The recurring charge target is invalid.");
    }

    internal static void ValidateTenantTarget(
        RecurringTenantChargeCommandSupport.TenantAccountTarget target)
    {
        if (target.PropertyId <= 0 || target.UnitId <= 0
            || target.Currency.Length != 3
            || target.Currency.Any(character => character is < 'A' or > 'Z'))
            throw new ArgumentException(
                "The tenant account must resolve to a property, unit, and ISO currency.");
    }

    private static void ValidateCommon(
        int portfolioId,
        StaffOperationActor actor,
        DateTime businessNowUtc,
        string deliveryIdempotencyKey)
    {
        if (portfolioId <= 0 || actor.UserId <= 0 || actor.AuthSessionId == Guid.Empty
            || actor.AccessContextId <= 0 || actor.AccessRevision <= 0
            || businessNowUtc == default || businessNowUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("The recurring charge command scope is invalid.");
        if (string.IsNullOrWhiteSpace(deliveryIdempotencyKey)
            || deliveryIdempotencyKey.Length > 200)
            throw new ArgumentException(
                "The recurring charge operation key is required and cannot exceed 200 characters.",
                nameof(deliveryIdempotencyKey));
    }

    private static void ValidateDisplayName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 200)
            throw new ArgumentException(
                "The recurring charge display name is required and cannot exceed 200 characters.",
                nameof(displayName));
    }
}
