using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Applications;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Applications;

public static class ApplicationFinanceWriteSupport
{
    public const string ResultContract = "application-finance.mutation.v1";

    public static TransactionalWrite<RecordApplicationFeeCommand, ApplicationFinanceMutationResult> Write(
        RecordApplicationFeeCommand command,
        RentalCommandDbContext db)
    {
        var handler = new RecordApplicationFeeRule(db);
        return Build("application-finance.record-fee", command, command.ApplicationId,
            handler.ExecuteAsync, handler.AuthorizeAsync);
    }

    public static TransactionalWrite<RefundApplicationFeeCommand, ApplicationFinanceMutationResult> Write(
        RefundApplicationFeeCommand command,
        RentalCommandDbContext db)
    {
        var handler = new RefundApplicationFeeRule(db);
        return Build("application-finance.refund-fee", command, command.ApplicationId,
            handler.ExecuteAsync, handler.AuthorizeAsync);
    }

    private static TransactionalWrite<TCommand, ApplicationFinanceMutationResult> Build<TCommand>(
        string operationName,
        TCommand command,
        int applicationId,
        Func<TCommand, IAtomicCommandContext, CancellationToken,
            Task<ApplicationFinanceMutationResult>> executeAsync,
        Func<TCommand, IAtomicCommandContext, CancellationToken, Task> authorizeReplayAsync)
        where TCommand : notnull, IAtomicCommandData => new(
            operationName,

            command,
            ResultContract,
            new WriteLockPlan(
                WriteLockProtocol.RentalApplication,
                applicationId),
            executeAsync,
            authorizeReplayAsync);

}

public sealed class RecordApplicationFeeRule
{
    private readonly RentalCommandDbContext _db;

    public RecordApplicationFeeRule(RentalCommandDbContext db) => _db = db;

    public async Task<ApplicationFinanceMutationResult> ExecuteAsync(
        RecordApplicationFeeCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        ApplicationFinanceCommandSupport.Validate(command);
        var securityNowUtc = await context.ReadDatabaseClockUtcAsync(ct);
        var application = await ApplicationFinanceCommandSupport.AuthorizedApplications(
                command.PortfolioId, command.ApplicationId, command.ActorUserId,
                command.AuthSessionId, command.AccessContextId, command.ExpectedAccessRevision,
                additionalCapability: CapabilityKeys.LeasingApplicationFeesCollect,
                _db, securityNowUtc)
            .Select(row => new { row.Id, row.PropertyId, row.UnitId })
            .SingleOrDefaultAsync(ct);
        if (application is null)
        {
            throw new UnauthorizedAccessException(
                "Application fee collection is outside the caller's current access scope.");
        }

        var nowUtc = securityNowUtc;
        var account = await _db.Set<ApplicationFinancialAccount>()
            .SingleOrDefaultAsync(row =>
                row.PortfolioId == command.PortfolioId
                && row.RentalApplicationId == command.ApplicationId,
                ct);
        var accountCreated = account is null;
        if (account is null)
        {
            account = new ApplicationFinancialAccount
            {
                PortfolioId = command.PortfolioId,
                RentalApplicationId = command.ApplicationId,
                Currency = command.Currency,
                OpenedAtUtc = nowUtc,
                CreatedByUserId = command.ActorUserId,
            };
            _db.Add(account);
            context.BindSemanticAudit(account, ApplicationFinanceCommandSupport.Audit(
                command.PortfolioId,
                nameof(ApplicationFinancialAccount),
                command.ActorUserId,
                AuditLogOperation.Created,
                "Opened the application's pre-tenancy financial account."));
            await context.FlushBusinessAsync(ct);
        }
        else if (!string.Equals(account.Currency, command.Currency, StringComparison.Ordinal))
        {
            return ApplicationFinanceCommandSupport.CurrencyMismatch(command.ApplicationId, account.Id, account.Currency);
        }

        var entry = new ApplicationFinancialEntry
        {
            PortfolioId = command.PortfolioId,
            ApplicationFinancialAccountId = account.Id,
            PropertyId = application.PropertyId,
            UnitId = application.UnitId,
            EntryType = ApplicationFinancialEntryType.FeeCollection,
            Direction = ApplicationFinancialDirection.Increase,
            Amount = command.Amount,
            Currency = account.Currency,
            EffectiveOn = command.EffectiveOn ?? DateOnly.FromDateTime(nowUtc),
            OccurredAtUtc = nowUtc,
            Description = "Application fee collected",
            Method = ApplicationFinanceCommandSupport.Clean(command.Method),
            Provider = ApplicationFinanceCommandSupport.Clean(command.Provider),
            ProviderReference = ApplicationFinanceCommandSupport.Clean(command.ProviderReference),
            Source = command.Source,
            SourceReference = ApplicationFinanceCommandSupport.Clean(command.SourceReference),
            IdempotencyKey = command.IdempotencyKey,
            CreatedByUserId = command.ActorUserId,
        };
        _db.Add(entry);
        context.BindSemanticAudit(entry, ApplicationFinanceCommandSupport.Audit(
            command.PortfolioId,
            nameof(ApplicationFinancialEntry),
            command.ActorUserId,
            AuditLogOperation.Created,
            "Recorded an append-only application fee collection."));
        await context.FlushBusinessAsync(ct);
        ApplicationFinanceCommandSupport.StageOutbox(context, command.PortfolioId,
            command.ApplicationId, account.Id, entry, nowUtc, command.IdempotencyKey);

        return ApplicationFinanceCommandSupport.Posted(
            command.ApplicationId, account.Id, entry, accountCreated);
    }

    public Task AuthorizeAsync(
        RecordApplicationFeeCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        ApplicationFinanceCommandSupport.AuthorizeReplayAsync(
            command.PortfolioId, command.ApplicationId, command.ActorUserId,
            command.AuthSessionId, command.AccessContextId, command.ExpectedAccessRevision,
            additionalCapability: CapabilityKeys.LeasingApplicationFeesCollect,
            _db, ct);
}

public sealed class RefundApplicationFeeRule
{
    private readonly RentalCommandDbContext _db;

    public RefundApplicationFeeRule(RentalCommandDbContext db) => _db = db;

    public async Task<ApplicationFinanceMutationResult> ExecuteAsync(
        RefundApplicationFeeCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        ApplicationFinanceCommandSupport.Validate(command);
        var securityNowUtc = await context.ReadDatabaseClockUtcAsync(ct);
        var application = await ApplicationFinanceCommandSupport.AuthorizedApplications(
                command.PortfolioId, command.ApplicationId, command.ActorUserId,
                command.AuthSessionId, command.AccessContextId, command.ExpectedAccessRevision,
                additionalCapability: null, _db, securityNowUtc)
            .Select(row => new { row.PropertyId, row.UnitId })
            .SingleOrDefaultAsync(ct)
            ?? throw new UnauthorizedAccessException(
                "Application fee refund is outside the caller's current access scope.");

        var account = await _db.Set<ApplicationFinancialAccount>()
            .Where(row => row.PortfolioId == command.PortfolioId
                && row.RentalApplicationId == command.ApplicationId)
            .Select(row => new
            {
                Entity = row,
            })
            .SingleOrDefaultAsync(ct);
        if (account is null)
        {
            return ApplicationFinanceCommandSupport.NotFound(command.ApplicationId);
        }

        var collection = await _db.Set<ApplicationFinancialEntry>()
            .SingleOrDefaultAsync(row =>
                row.PortfolioId == command.PortfolioId
                && row.ApplicationFinancialAccountId == account.Entity.Id
                && row.Id == command.CollectionEntryId
                && row.EntryType == ApplicationFinancialEntryType.FeeCollection
                && row.Direction == ApplicationFinancialDirection.Increase,
                ct);
        if (collection is null)
        {
            return new ApplicationFinanceMutationResult(
                ApplicationFinanceMutationOutcome.CollectionNotFound,
                command.ApplicationId,
                account.Entity.Id,
                null,
                command.CollectionEntryId,
                null,
                null,
                null,
                account.Entity.Currency,
                null,
                null,
                false,
                "The fee collection does not exist on this application's financial account.");
        }

        var alreadyRefunded = await _db.Set<ApplicationFinancialEntry>()
            .Where(row => row.PortfolioId == command.PortfolioId
                && row.ApplicationFinancialAccountId == account.Entity.Id
                && row.RelatedEntryId == collection.Id
                && row.EntryType == ApplicationFinancialEntryType.Refund)
            .SumAsync(row => (decimal?)row.Amount, ct) ?? 0m;
        if (alreadyRefunded + command.Amount > collection.Amount)
        {
            return new ApplicationFinanceMutationResult(
                ApplicationFinanceMutationOutcome.RefundExceedsCollectedAmount,
                command.ApplicationId,
                account.Entity.Id,
                null,
                collection.Id,
                ApplicationFinancialEntryType.Refund,
                ApplicationFinancialDirection.Decrease,
                command.Amount,
                account.Entity.Currency,
                command.EffectiveOn,
                null,
                false,
                "The refund would exceed the unrefunded amount of the selected fee collection.");
        }

        var nowUtc = securityNowUtc;
        var entry = new ApplicationFinancialEntry
        {
            PortfolioId = command.PortfolioId,
            ApplicationFinancialAccountId = account.Entity.Id,
            PropertyId = collection.PropertyId ?? application.PropertyId,
            UnitId = collection.UnitId ?? application.UnitId,
            EntryType = ApplicationFinancialEntryType.Refund,
            Direction = ApplicationFinancialDirection.Decrease,
            Amount = command.Amount,
            Currency = account.Entity.Currency,
            EffectiveOn = command.EffectiveOn ?? DateOnly.FromDateTime(nowUtc),
            OccurredAtUtc = nowUtc,
            Description = command.Reason.Trim(),
            Method = ApplicationFinanceCommandSupport.Clean(command.Method),
            Provider = ApplicationFinanceCommandSupport.Clean(command.Provider),
            ProviderReference = ApplicationFinanceCommandSupport.Clean(command.ProviderReference),
            Source = command.Source,
            SourceReference = ApplicationFinanceCommandSupport.Clean(command.SourceReference),
            IdempotencyKey = command.IdempotencyKey,
            RelatedEntryId = collection.Id,
            CreatedByUserId = command.ActorUserId,
        };
        _db.Add(entry);
        context.BindSemanticAudit(entry, ApplicationFinanceCommandSupport.Audit(
            command.PortfolioId,
            nameof(ApplicationFinancialEntry),
            command.ActorUserId,
            AuditLogOperation.Created,
            "Recorded an append-only application fee refund."));
        await context.FlushBusinessAsync(ct);
        ApplicationFinanceCommandSupport.StageOutbox(context, command.PortfolioId,
            command.ApplicationId, account.Entity.Id, entry, nowUtc, command.IdempotencyKey);

        return ApplicationFinanceCommandSupport.Posted(
            command.ApplicationId, account.Entity.Id, entry, accountCreated: false);
    }

    public Task AuthorizeAsync(
        RefundApplicationFeeCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        ApplicationFinanceCommandSupport.AuthorizeReplayAsync(
            command.PortfolioId, command.ApplicationId, command.ActorUserId,
            command.AuthSessionId, command.AccessContextId, command.ExpectedAccessRevision,
            additionalCapability: null, _db, ct);
}

internal static class ApplicationFinanceCommandSupport
{
    internal static void Validate(RecordApplicationFeeCommand command)
    {
        ValidateCommon(command.PortfolioId, command.ApplicationId, command.Amount, command.Currency,
            command.Provider, command.ProviderReference, command.Source, command.IdempotencyKey,
            command.ActorUserId, command.AuthSessionId, command.AccessContextId,
            command.ExpectedAccessRevision);
    }

    internal static void Validate(RefundApplicationFeeCommand command)
    {
        ValidateCommon(command.PortfolioId, command.ApplicationId, command.Amount, "USD",
            command.Provider, command.ProviderReference, command.Source, command.IdempotencyKey,
            command.ActorUserId, command.AuthSessionId, command.AccessContextId,
            command.ExpectedAccessRevision);
        if (command.CollectionEntryId <= 0)
            throw new ArgumentException("A fee collection entry is required.");
        if (string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Trim().Length > 500)
            throw new ArgumentException("A refund reason is required and cannot exceed 500 characters.");
    }

    private static void ValidateCommon(int portfolioId, int applicationId, decimal amount, string currency,
        string? provider, string? providerReference, ApplicationFinancialEntrySource source,
        string idempotencyKey, int actorUserId, Guid authSessionId, int accessContextId,
        long expectedAccessRevision)
    {
        if (portfolioId <= 0 || applicationId <= 0 || actorUserId <= 0
            || authSessionId == Guid.Empty || accessContextId <= 0 || expectedAccessRevision < 1)
            throw new ArgumentException("Portfolio, application, actor, and access envelope are required.");
        if (amount <= 0m)
            throw new ArgumentException("Amount must be greater than zero.");
        if (currency.Length != 3 || currency.Any(character => character is < 'A' or > 'Z'))
            throw new ArgumentException("Currency must be a three-letter uppercase ISO code.");
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
            throw new ArgumentException("A valid idempotency key is required.");
        if ((provider is null) != (providerReference is null))
            throw new ArgumentException("Provider and provider reference must be supplied together.");
        if (source == ApplicationFinancialEntrySource.PaymentProvider && provider is null)
            throw new ArgumentException("Payment-provider entries require provider provenance.");
        if (provider?.Trim().Length > 50 || providerReference?.Trim().Length > 200)
            throw new ArgumentException("Provider provenance exceeds its allowed length.");
    }

    internal static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static ApplicationFinanceMutationResult Posted(
        int applicationId,
        int accountId,
        ApplicationFinancialEntry entry,
        bool accountCreated) => new(
            ApplicationFinanceMutationOutcome.Posted,
            applicationId,
            accountId,
            entry.Id,
            entry.RelatedEntryId,
            entry.EntryType,
            entry.Direction,
            entry.Amount,
            entry.Currency,
            entry.EffectiveOn,
            entry.OccurredAtUtc,
            accountCreated,
            null);

    internal static ApplicationFinanceMutationResult NotFound(int applicationId) => new(
        ApplicationFinanceMutationOutcome.ApplicationNotFound,
        applicationId,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        false,
        "The rental application does not exist in this portfolio.");

    internal static ApplicationFinanceMutationResult CurrencyMismatch(
        int applicationId, int accountId, string currency) => new(
            ApplicationFinanceMutationOutcome.CurrencyMismatch,
            applicationId,
            accountId,
            null,
            null,
            null,
            null,
            null,
            currency,
            null,
            null,
            false,
            $"This application's financial account is denominated in {currency}.");

    internal static AtomicSemanticAudit Audit(
        int portfolioId,
        string entityType,
        int actorUserId,
        AuditLogOperation operation,
        string reason) => new(
            portfolioId,
            entityType,
            0,
            operation,
            UserId: actorUserId,
            ChangeReason: reason);

    internal static void StageOutbox(
        IAtomicCommandContext context,
        int portfolioId,
        int applicationId,
        int accountId,
        ApplicationFinancialEntry entry,
        DateTime nowUtc,
        string idempotencyKey)
    {
        context.StageOutbox(new OutboxMessage
        {
            PortfolioId = portfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
            {
                entityType = nameof(ApplicationFinancialAccount),
                entityId = accountId,
                data = new
                {
                    applicationId,
                    accountId,
                    entryId = entry.Id,
                    entryType = entry.EntryType.ToString(),
                    direction = entry.Direction.ToString(),
                    entry.Amount,
                    entry.Currency,
                    entry.EffectiveOn,
                },
            }),
            IdempotencyKey = idempotencyKey,
            CreatedAtUtc = nowUtc,
            NextAttemptAtUtc = nowUtc,
        });
    }

    internal static async Task AuthorizeReplayAsync(
        int portfolioId,
        int applicationId,
        int actorUserId,
        Guid authSessionId,
        int accessContextId,
        long expectedAccessRevision,
        string? additionalCapability,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        if (portfolioId <= 0 || applicationId <= 0 || actorUserId <= 0
            || authSessionId == Guid.Empty || accessContextId <= 0 || expectedAccessRevision < 1)
            throw new UnauthorizedAccessException();
        var nowUtc = await db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        var authorized = await AuthorizedApplications(
                portfolioId, applicationId, actorUserId, authSessionId, accessContextId,
                expectedAccessRevision, additionalCapability, db, nowUtc)
            .AnyAsync(ct);
        if (!authorized)
            throw new UnauthorizedAccessException();
    }

    internal static IQueryable<RentalApplication> AuthorizedApplications(
        int portfolioId,
        int applicationId,
        int actorUserId,
        Guid authSessionId,
        int accessContextId,
        long expectedAccessRevision,
        string? additionalCapability,
        RentalCommandDbContext db,
        DateTime securityNowUtc)
    {
        var assignments = db.Set<MembershipRoleAssignment>().Where(assignment =>
            assignment.PortfolioId == portfolioId
            && assignment.Status == MembershipRoleAssignmentStatus.Active
            && assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null
            && assignment.EffectiveFromUtc <= securityNowUtc
            && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > securityNowUtc));

        return db.Set<RentalApplication>().Where(application =>
            application.Id == applicationId
            && application.PortfolioId == portfolioId
            && application.Property != null
            && application.Property.PortfolioId == portfolioId
            && db.Set<AuthSession>().Any(session =>
                session.Id == authSessionId && session.UserId == actorUserId
                && session.ActiveAccessContextId == accessContextId
                && session.Status == AuthSessionStatus.Active && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > securityNowUtc)
            && db.Set<WorkspaceAccessContext>().Any(context =>
                context.Id == accessContextId && context.UserId == actorUserId
                && context.PortfolioId == portfolioId
                && context.AccessRevision == expectedAccessRevision
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null && context.RevokedAtUtc == null)
            && db.Set<WorkspaceMembership>().Any(membership =>
                membership.AccessContextId == accessContextId
                && membership.PortfolioId == portfolioId
                && membership.Status == WorkspaceMembershipStatus.Active
                && membership.SuspendedAtUtc == null && membership.RevokedAtUtc == null
                && membership.EffectiveFromUtc <= securityNowUtc
                && (membership.EffectiveToUtc == null || membership.EffectiveToUtc > securityNowUtc)
                && assignments.Any(assignment =>
                    assignment.WorkspaceMembershipId == membership.Id
                    && (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                        || (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                            && assignment.SelectedProperties.Any(scope =>
                                scope.PropertyId == application.PropertyId
                                && scope.PortfolioId == portfolioId)))
                    && assignment.RoleProfile!.Capabilities.Any(capability =>
                        capability.CapabilityDefinition!.Key == CapabilityKeys.MoneyPaymentsManage
                        || (additionalCapability != null
                            && capability.CapabilityDefinition.Key ==
                                additionalCapability)))));
    }
}
