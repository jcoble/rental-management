using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection.Name)]
public sealed class Phase1WriteExecutorParityCanaryTests(MigratedPostgreSqlFixture fixture)
{
    private static readonly AtomicJsonResultCodec<CanaryResult> ResultCodec =
        new("phase1-write-executor-parity.v1");

    [Fact]
    public async Task TestLocalHandler_OldShellAndRequestExecutorWriteIdenticalCompleteProjections()
    {
        await using var oldDatabase = await fixture.CreateContextAsync();
        await using var newDatabase = await fixture.CreateContextAsync();
        var now = new DateTime(2027, 1, 24, 15, 30, 0, DateTimeKind.Utc);
        var command = new CanaryCommand(
            PortfolioId: 1,
            Marker: "phase1-two-path-parity-canary",
            Amount: 123.45m,
            OccurredAtUtc: now);
        var identity = new AtomicCommandIdentity(
            "test.phase1.write-executor-parity",
            "phase1-two-path-parity-canary");

        AtomicCommandOutcome<CanaryResult> oldOutcome;
        await using (var services = BuildServices(oldDatabase.ConnectionString, now))
        await using (var scope = services.CreateAsyncScope())
        {
            oldOutcome = await scope.ServiceProvider.GetRequiredService<IAtomicUnitOfWork>()
                .ExecuteAsync(identity, command, ResultCodec);
        }

        AtomicCommandOutcome<CanaryResult> newOutcome;
        await using (var services = BuildServices(newDatabase.ConnectionString, now))
        await using (var scope = services.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<
                IAtomicCommandHandler<CanaryCommand, CanaryResult>>();
            var write = new TransactionalWrite<CanaryCommand, CanaryResult>(
                identity.CommandType,
                WriteIdempotencyPolicy.Required,
                command,
                ResultCodec.ContractName,
                new WriteLockPlan(
                    WriteLockProtocol.Possession,
                    WriteLock.For("Unit", 701),
                    WriteLock.For("LeaseManagement", 702)),
                handler.HandleAsync,
                handler.AuthorizeReplayAsync);
            newOutcome = await scope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>()
                .ExecuteAsync(identity.IdempotencyKey, write);
        }

        newOutcome.Value.Should().Be(oldOutcome.Value);
        newOutcome.Disposition.Should().Be(oldOutcome.Disposition);

        var oldProjection = await ReadCompleteProjectionAsync(
            oldDatabase.Db, identity, command.Marker, oldOutcome.AttemptId);
        var newProjection = await ReadCompleteProjectionAsync(
            newDatabase.Db, identity, command.Marker, newOutcome.AttemptId);
        newProjection.Should().BeEquivalentTo(oldProjection);
    }

    private static ServiceProvider BuildServices(string connectionString, DateTime now)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(now));
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<CanaryCommand, CanaryResult, CanaryHandler>();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(connectionString)
                .UseAtomicPersistenceKernel(provider));
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static async Task<CanaryProjection> ReadCompleteProjectionAsync(
        RentalCommandDbContext db,
        AtomicCommandIdentity identity,
        string marker,
        Guid outcomeAttemptId)
    {
        db.ChangeTracker.Clear();
        var receipt = await db.AtomicCommandReceipts.AsNoTracking().SingleAsync(row =>
            row.CommandType == identity.CommandType && row.IdempotencyKey == identity.IdempotencyKey);
        var audit = await db.AtomicAuditLogs.AsNoTracking().SingleAsync(row =>
            row.CommandType == identity.CommandType
            && row.CommandIdempotencyKey == identity.IdempotencyKey);
        var outbox = await db.OutboxMessages.AsNoTracking().SingleAsync(row =>
            row.IdempotencyKey == marker + ":outbox");
        var expense = await db.Expenses.IgnoreQueryFilters().AsNoTracking().SingleAsync(row =>
            row.PortfolioId == 1 && row.Description == marker);

        receipt.Id.Should().NotBeEmpty();
        receipt.AttemptId.Should().NotBeEmpty();
        audit.Id.Should().BePositive();
        audit.AttemptId.Should().NotBeEmpty();
        outbox.Id.Should().BePositive();
        receipt.AttemptId.Should().Be(outcomeAttemptId);
        audit.AttemptId.Should().Be(outcomeAttemptId);

        return new CanaryProjection(
            new AttemptCorrelationProjection(
                ReceiptAttemptId: "<attempt-id>",
                AuditAttemptId: "<attempt-id>",
                OutcomeAttemptId: "<attempt-id>",
                ReceiptMatchesAuditMatchesOutcome:
                    receipt.AttemptId == audit.AttemptId && audit.AttemptId == outcomeAttemptId),
            new ReceiptProjection(
                Id: "<receipt-id>",
                AttemptId: "<attempt-id>",
                receipt.CommandType,
                receipt.IdempotencyKey,
                receipt.RequestFingerprint,
                receipt.Status,
                receipt.ResultContract,
                receipt.ResultJson,
                receipt.StartedAt,
                receipt.CompletedAt),
            new AuditProjection(
                Id: "<audit-id>",
                AttemptId: "<attempt-id>",
                audit.CommandType,
                audit.CommandIdempotencyKey,
                audit.MutationOrdinal,
                audit.PortfolioId,
                audit.UserId,
                audit.ActorLabel,
                audit.EntityType,
                audit.EntityId,
                audit.Operation,
                audit.OldValues,
                audit.NewValues,
                audit.ChangeReason,
                audit.Timestamp,
                audit.IpAddress),
            new OutboxProjection(
                Id: "<outbox-id>",
                outbox.PortfolioId,
                outbox.MessageType,
                outbox.Payload,
                outbox.IdempotencyKey,
                outbox.AttemptCount,
                outbox.CreatedAtUtc,
                outbox.NextAttemptAtUtc,
                outbox.LastAttemptAtUtc,
                outbox.ClaimOwner,
                outbox.ClaimToken,
                outbox.ClaimExpiresAtUtc,
                outbox.AcceptedAtUtc,
                outbox.DeliveredAtUtc,
                outbox.DeadLetteredAtUtc,
                outbox.Provider,
                outbox.ProviderMessageId,
                outbox.FailureKind,
                outbox.LastError),
            new ExpenseProjection(
                expense.Id,
                expense.PortfolioId,
                expense.OperationalScope,
                expense.PropertyId,
                expense.UnitId,
                expense.VendorId,
                expense.WorkOrderId,
                expense.CapitalizedAssetId,
                expense.RecurringExpenseId,
                expense.RecurringExpenseOccurrenceDate,
                expense.Category,
                expense.Description,
                expense.Status,
                expense.Amount,
                expense.IncurredAt,
                expense.DueDate,
                expense.PaidAt,
                expense.BillableToOwner,
                expense.Notes,
                expense.CreatedAt,
                expense.UpdatedAt,
                expense.Subtotal,
                expense.TaxAmount,
                expense.ReceiptData,
                expense.PaymentMethod,
                expense.CardLast4,
                expense.DocumentKind,
                expense.DeletedAt));
    }

    private sealed record CanaryCommand(
        int PortfolioId,
        string Marker,
        decimal Amount,
        DateTime OccurredAtUtc) : IAtomicCommandData;

    private sealed record CanaryResult(int ExpenseId, string Marker);

    private sealed class CanaryHandler(RentalCommandDbContext db)
        : IAtomicCommandHandler<CanaryCommand, CanaryResult>
    {
        public async Task<CanaryResult> HandleAsync(
            CanaryCommand command,
            IAtomicCommandContext context,
            CancellationToken ct)
        {
            var expense = new Expense
            {
                PortfolioId = command.PortfolioId,
                OperationalScope = ExpenseOperationalScope.Portfolio,
                Category = ScheduleECategory.Repairs,
                Description = command.Marker,
                Status = ExpenseStatus.Pending,
                Amount = command.Amount,
                IncurredAt = command.OccurredAtUtc,
                BillableToOwner = true,
                Notes = "Test-local Phase 1 executor parity canary",
                CreatedAt = command.OccurredAtUtc,
                UpdatedAt = command.OccurredAtUtc,
                ReceiptData = "{\"source\":\"phase1-parity-canary\"}",
                PaymentMethod = "Test",
                CardLast4 = "0123",
                DocumentKind = "Canary",
            };
            db.Expenses.Add(expense);
            context.BindSemanticAudit(
                expense,
                new AtomicSemanticAudit(
                    command.PortfolioId,
                    nameof(Expense),
                    EntityId: 0,
                    AuditLogOperation.Created,
                    NewValues: $$"""{"Description":"{{command.Marker}}","Amount":{{command.Amount}}}""",
                    ChangeReason: "Phase 1 write-executor parity canary"));
            context.StageOutbox(new OutboxMessage
            {
                PortfolioId = command.PortfolioId,
                MessageType = "phase1-write-executor-parity",
                Payload = $$"""{"marker":"{{command.Marker}}"}""",
                IdempotencyKey = command.Marker + ":outbox",
            });
            await context.FlushBusinessAsync(ct);
            return new CanaryResult(expense.Id, command.Marker);
        }

        public Task AuthorizeReplayAsync(
            CanaryCommand command,
            IAtomicCommandContext context,
            CancellationToken ct) => Task.CompletedTask;
    }

    private sealed record CanaryProjection(
        AttemptCorrelationProjection AttemptCorrelation,
        ReceiptProjection Receipt,
        AuditProjection Audit,
        OutboxProjection Outbox,
        ExpenseProjection BusinessRow);

    private sealed record AttemptCorrelationProjection(
        string ReceiptAttemptId,
        string AuditAttemptId,
        string OutcomeAttemptId,
        bool ReceiptMatchesAuditMatchesOutcome);

    private sealed record ReceiptProjection(
        string Id,
        string AttemptId,
        string CommandType,
        string IdempotencyKey,
        string RequestFingerprint,
        AtomicCommandReceiptStatus Status,
        string ResultContract,
        string? ResultJson,
        DateTime StartedAt,
        DateTime? CompletedAt);

    private sealed record AuditProjection(
        string Id,
        string AttemptId,
        string CommandType,
        string CommandIdempotencyKey,
        long MutationOrdinal,
        int PortfolioId,
        int? UserId,
        string? ActorLabel,
        string EntityType,
        int EntityId,
        AuditLogOperation Operation,
        string? OldValues,
        string? NewValues,
        string? ChangeReason,
        DateTime Timestamp,
        string? IpAddress);

    private sealed record OutboxProjection(
        string Id,
        int? PortfolioId,
        string MessageType,
        string Payload,
        string IdempotencyKey,
        int AttemptCount,
        DateTime CreatedAtUtc,
        DateTime NextAttemptAtUtc,
        DateTime? LastAttemptAtUtc,
        string? ClaimOwner,
        Guid? ClaimToken,
        DateTime? ClaimExpiresAtUtc,
        DateTime? AcceptedAtUtc,
        DateTime? DeliveredAtUtc,
        DateTime? DeadLetteredAtUtc,
        string? Provider,
        string? ProviderMessageId,
        OutboxFailureKind? FailureKind,
        string? LastError);

    private sealed record ExpenseProjection(
        int Id,
        int PortfolioId,
        ExpenseOperationalScope OperationalScope,
        int? PropertyId,
        int? UnitId,
        int? VendorId,
        int? WorkOrderId,
        int? CapitalizedAssetId,
        int? RecurringExpenseId,
        DateTime? RecurringExpenseOccurrenceDate,
        ScheduleECategory Category,
        string Description,
        ExpenseStatus Status,
        decimal Amount,
        DateTime IncurredAt,
        DateTime? DueDate,
        DateTime? PaidAt,
        bool BillableToOwner,
        string? Notes,
        DateTime CreatedAt,
        DateTime UpdatedAt,
        decimal? Subtotal,
        decimal? TaxAmount,
        string? ReceiptData,
        string? PaymentMethod,
        string? CardLast4,
        string? DocumentKind,
        DateTime? DeletedAt);

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => null;
        public string? ActorLabel => "integration:phase1-parity-canary";
        public string? IpAddress => "127.0.0.1";
    }
}
