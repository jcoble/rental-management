using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Models.Accounting;
using RentalCommand.Data;
using RentalCommand.Data.Accounting;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name4)]
public sealed class AccountingFoundationPostgreSqlTests
{
    private static readonly AtomicJsonResultCodec<ConcurrentJournalPostingResult> ConcurrentPostingCodec =
        new("accounting.concurrent-journal-posting.v1");
    private readonly MigratedPostgreSqlFixture _fixture;

    public AccountingFoundationPostgreSqlTests(MigratedPostgreSqlFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task DefaultChart_IsPortfolioScopedAndIdempotent()
    {
        await using var setup = await _fixture.CreateContextAsync();
        var seed = new ChartOfAccountsSeedService(setup.Db);

        var first = await seed.SeedAsync(1);
        first.Should().HaveCount(ChartOfAccountsSeedService.DefaultChart.Count);
        await setup.Db.SaveChangesAsync();
        setup.Db.ChangeTracker.Clear();

        var second = await seed.SeedAsync(1);
        second.Should().BeEmpty();
        await setup.Db.SaveChangesAsync();

        (await setup.Db.LedgerAccounts.CountAsync(account => account.PortfolioId == 1))
            .Should().Be(ChartOfAccountsSeedService.DefaultChart.Count);
        (await setup.Db.LedgerAccounts.Where(account => account.PortfolioId == 1)
                .SingleAsync(account => account.Code == "1590"))
            .NormalBalance.Should().Be(NormalBalance.Credit);
    }

    [Fact]
    public async Task PostingService_RejectsUnbalancedAndReturnsExactReplay()
    {
        await using var setup = await _fixture.CreateContextAsync();
        var seed = new ChartOfAccountsSeedService(setup.Db);
        await seed.SeedAsync(1);
        await setup.Db.SaveChangesAsync();
        setup.Db.ChangeTracker.Clear();
        var accountIds = await setup.Db.LedgerAccounts.Where(account => account.PortfolioId == 1)
            .OrderBy(account => account.Code).Select(account => account.Id).Take(2).ToArrayAsync();
        var service = new AccountingPostingService(setup.Db);
        var proposal = Proposal(accountIds[0], accountIds[1], sourceId: 7001);

        var entry = await service.PostAsync(proposal);
        entry.Lines.Should().HaveCount(2);
        await setup.Db.SaveChangesAsync();
        setup.Db.ChangeTracker.Clear();

        var replay = await service.PostAsync(proposal);
        replay.Id.Should().Be(entry.Id);
        (await setup.Db.JournalEntries.CountAsync(journal => journal.PortfolioId == 1 && journal.SourceId == 7001))
            .Should().Be(1);

        var unbalanced = Proposal(accountIds[0], accountIds[1], sourceId: 7002);
        unbalanced.Lines =
        [
            new AccountingProposedLine { LedgerAccountId = accountIds[0], DebitAmount = 100m },
            new AccountingProposedLine { LedgerAccountId = accountIds[1], CreditAmount = 99m },
        ];
        await FluentActions.Invoking(() => service.PostAsync(unbalanced))
            .Should().ThrowAsync<AccountingPostingValidationException>()
            .WithMessage("*not balanced*");
    }

    [Fact]
    public async Task PostingService_ReplaysBusinessKeyAcrossRecreatedSourceAndRejectsChangedFacts()
    {
        await using var setup = await _fixture.CreateContextAsync();
        var seed = new ChartOfAccountsSeedService(setup.Db);
        await seed.SeedAsync(1);
        await setup.Db.SaveChangesAsync();
        setup.Db.ChangeTracker.Clear();
        var accountIds = await setup.Db.LedgerAccounts.Where(account => account.PortfolioId == 1)
            .OrderBy(account => account.Code)
            .Select(account => account.Id)
            .Take(2)
            .ToArrayAsync();
        var service = new AccountingPostingService(setup.Db);
        var original = Proposal(accountIds[0], accountIds[1], sourceId: 7_011);
        original.SourceBusinessKey = "tenant-charge:recreated-source";

        var posted = await service.PostAsync(original);
        await setup.Db.SaveChangesAsync();
        setup.Db.ChangeTracker.Clear();

        var recreatedSource = Proposal(accountIds[0], accountIds[1], sourceId: 7_012);
        recreatedSource.SourceBusinessKey = original.SourceBusinessKey;
        var replay = await service.PostAsync(recreatedSource);

        replay.Id.Should().Be(posted.Id);
        (await setup.Db.JournalEntries.CountAsync(entry =>
                entry.PortfolioId == 1 &&
                entry.SourceBusinessKey == original.SourceBusinessKey))
            .Should().Be(1);

        var changedFacts = Proposal(accountIds[0], accountIds[1], sourceId: 7_013);
        changedFacts.SourceBusinessKey = original.SourceBusinessKey;
        changedFacts.Description = "Changed accounting facts";
        await FluentActions.Invoking(() => service.PostAsync(changedFacts))
            .Should().ThrowAsync<AccountingIdempotencyConflictException>()
            .WithMessage("The source posting key was already used with different accounting facts.");
    }

    [Fact]
    public async Task AtomicRunner_ConcurrentBusinessKeyLoserReturnsTheCommittedJournal()
    {
        await using var setup = await _fixture.CreateContextAsync();
        var seed = new ChartOfAccountsSeedService(setup.Db);
        await seed.SeedAsync(1);
        await setup.Db.SaveChangesAsync();
        setup.Db.ChangeTracker.Clear();
        var accountIds = await setup.Db.LedgerAccounts.Where(account => account.PortfolioId == 1)
            .OrderBy(account => account.Code)
            .Select(account => account.Id)
            .Take(2)
            .ToArrayAsync();

        var gate = new ConcurrentJournalPostingGate();
        await using var services = CreateConcurrentPostingServices(setup.ConnectionString, gate);
        await using var firstScope = services.CreateAsyncScope();
        await using var secondScope = services.CreateAsyncScope();
        var firstWrites = firstScope.ServiceProvider.GetRequiredService<IWriteExecutor>();
        var secondWrites = secondScope.ServiceProvider.GetRequiredService<IWriteExecutor>();
        var firstCommand = new ConcurrentJournalPostingCommand(
            8_071,
            accountIds[0],
            accountIds[1],
            true);
        var secondCommand = firstCommand with { SourceId = 8_072, HoldBeforeCommit = false };

        var firstIdentity = new AtomicCommandIdentity(
            "accounting.concurrent-journal-posting", "first-attempt");
        var firstHandler = firstScope.ServiceProvider
            .GetRequiredService<ConcurrentJournalPostingHandler>();
        var first = firstWrites.ExecuteAsync(
            firstIdentity.IdempotencyKey,
            ConcurrentWrite(firstIdentity, firstCommand, firstHandler));
        await gate.FirstJournalFlushed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var secondIdentity = new AtomicCommandIdentity(
            "accounting.concurrent-journal-posting", "second-attempt");
        var secondHandler = secondScope.ServiceProvider
            .GetRequiredService<ConcurrentJournalPostingHandler>();
        var second = secondWrites.ExecuteAsync(
            secondIdentity.IdempotencyKey,
            ConcurrentWrite(secondIdentity, secondCommand, secondHandler));
        await gate.SecondStartedPosting.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var winner = await Task.WhenAny(
            gate.SecondReturnedCommittedJournal.Task,
            Task.Delay(TimeSpan.FromMilliseconds(300)));
        winner.Should().NotBe(gate.SecondReturnedCommittedJournal.Task,
            "the second Atomic runner must wait for the first transaction's business-identity lock");

        gate.ReleaseFirstCommit.TrySetResult();
        var outcomes = await Task.WhenAll(first, second);

        outcomes.Should().OnlyContain(outcome => outcome.Disposition == AtomicCommandDisposition.Executed);
        outcomes.Select(outcome => outcome.Value.JournalEntryId).Distinct().Should().ContainSingle();
        (await setup.Db.JournalEntries.CountAsync(entry =>
                entry.PortfolioId == 1 &&
                entry.SourceType == JournalSourceType.TenantCharge &&
                entry.SourceBusinessKey == ConcurrentJournalPostingCommand.SourceBusinessKey))
            .Should().Be(1);
        (await setup.Db.AtomicCommandReceipts.CountAsync(receipt =>
                receipt.CommandType == "accounting.concurrent-journal-posting"))
            .Should().Be(2);
    }

    [Fact]
    public async Task DatabaseCommit_RejectsUnbalancedEntryWhenApplicationCheckIsBypassed()
    {
        await using var setup = await _fixture.CreateContextAsync();
        var seed = new ChartOfAccountsSeedService(setup.Db);
        await seed.SeedAsync(1);
        await setup.Db.SaveChangesAsync();
        setup.Db.ChangeTracker.Clear();
        var accountIds = await setup.Db.LedgerAccounts.Where(account => account.PortfolioId == 1)
            .OrderBy(account => account.Code).Select(account => account.Id).Take(2).ToArrayAsync();

        var entry = new JournalEntry
        {
            PortfolioId = 1,
            EffectiveOn = new DateOnly(2026, 7, 31),
            Currency = "USD",
            Description = "Bypass balance check",
            SourceType = JournalSourceType.OpeningBalance,
            SourceId = 7003,
            SourceBusinessKey = "test:opening:7003",
            IdempotencyDigest = new string('a', 64),
            PostingRuleVersion = 1,
            AttemptId = Guid.NewGuid(),
            UserId = 1,
            AtomicReceiptId = Guid.NewGuid(),
        };
        entry.Lines.Add(new JournalLine
        {
            LedgerAccountId = accountIds[0],
            DebitAmount = 100m,
        });
        setup.Db.JournalEntries.Add(entry);

        await FluentActions.Invoking(() => setup.Db.SaveChangesAsync())
            .Should().ThrowAsync<PostgresException>()
            .WithMessage("*not balanced*");
    }

    [Fact]
    public async Task PostedEntriesAndLines_RejectUpdateAndDelete()
    {
        await using var setup = await _fixture.CreateContextAsync();
        var seed = new ChartOfAccountsSeedService(setup.Db);
        await seed.SeedAsync(1);
        await setup.Db.SaveChangesAsync();
        setup.Db.ChangeTracker.Clear();
        var accountIds = await setup.Db.LedgerAccounts.Where(account => account.PortfolioId == 1)
            .OrderBy(account => account.Code).Select(account => account.Id).Take(2).ToArrayAsync();
        var entry = await new AccountingPostingService(setup.Db)
            .PostAsync(Proposal(accountIds[0], accountIds[1], sourceId: 7004));
        await setup.Db.SaveChangesAsync();

        await FluentActions.Invoking(() => setup.Db.Database.ExecuteSqlRawAsync(
                "UPDATE \"JournalEntries\" SET \"Description\" = 'changed' WHERE \"Id\" = {0}", entry.Id))
            .Should().ThrowAsync<PostgresException>()
            .WithMessage("*immutable*");
        await FluentActions.Invoking(() => setup.Db.Database.ExecuteSqlRawAsync(
                "DELETE FROM \"JournalLines\" WHERE \"JournalEntryId\" = {0}", entry.Id))
            .Should().ThrowAsync<PostgresException>()
            .WithMessage("*immutable*");
    }

    [Fact]
    public async Task CommittedJournal_CannotReceiveLateBalancedLines()
    {
        await using var setup = await _fixture.CreateContextAsync();
        var seed = new ChartOfAccountsSeedService(setup.Db);
        await seed.SeedAsync(1);
        await setup.Db.SaveChangesAsync();
        setup.Db.ChangeTracker.Clear();
        var accountIds = await setup.Db.LedgerAccounts
            .Where(account => account.PortfolioId == 1)
            .OrderBy(account => account.Code)
            .Select(account => account.Id)
            .Take(2)
            .ToArrayAsync();
        var entry = await new AccountingPostingService(setup.Db)
            .PostAsync(Proposal(accountIds[0], accountIds[1], sourceId: 7006));
        await setup.Db.SaveChangesAsync();
        setup.Db.ChangeTracker.Clear();

        await FluentActions.Invoking(() => setup.Db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "JournalLines" ("JournalEntryId", "LedgerAccountId", "DebitAmount", "CreditAmount")
                VALUES ({entry.Id}, {accountIds[0]}, 1.0, 0.0),
                       ({entry.Id}, {accountIds[1]}, 0.0, 1.0)
                """))
            .Should().ThrowAsync<PostgresException>()
            .WithMessage("*Journal lines must be inserted in the journal entry transaction.*");
    }

    [Fact]
    public async Task PropertyWithJournalHistory_CannotBeDeletedAndReportsRestrictReason()
    {
        await using var setup = await _fixture.CreateContextAsync();
        var property = new Property
        {
            PortfolioId = 1,
            Name = "Journal history property",
            AddressLine1 = "100 Ledger Lane",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        setup.Db.Properties.Add(property);
        await setup.Db.SaveChangesAsync();

        var seed = new ChartOfAccountsSeedService(setup.Db);
        await seed.SeedAsync(1);
        await setup.Db.SaveChangesAsync();
        setup.Db.ChangeTracker.Clear();
        var accountIds = await setup.Db.LedgerAccounts
            .Where(account => account.PortfolioId == 1)
            .OrderBy(account => account.Code)
            .Select(account => account.Id)
            .Take(2)
            .ToArrayAsync();
        var proposal = Proposal(accountIds[0], accountIds[1], sourceId: 7010);
        proposal.Lines =
        [
            new AccountingProposedLine
            {
                LedgerAccountId = accountIds[0],
                DebitAmount = 100m,
                PropertyId = property.Id,
            },
            new AccountingProposedLine
            {
                LedgerAccountId = accountIds[1],
                CreditAmount = 100m,
                PropertyId = property.Id,
            },
        ];
        await new AccountingPostingService(setup.Db).PostAsync(proposal);
        await setup.Db.SaveChangesAsync();

        await FluentActions.Invoking(() => setup.Db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM \"Properties\" WHERE \"Id\" = {property.Id}"))
            .Should().ThrowAsync<PostgresException>()
            .WithMessage("*FK_JournalLines_Properties_RestrictHistory*");
    }

    [Fact]
    public async Task CrossPortfolioAccount_IsDeniedWithoutExistenceLeakage()
    {
        await using var setup = await _fixture.CreateContextAsync();
        var secondPortfolio = new Portfolio
        {
            Name = "Second test portfolio",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            Currency = "USD",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        setup.Db.Portfolios.Add(secondPortfolio);
        await setup.Db.SaveChangesAsync();
        setup.Db.LedgerAccounts.Add(new LedgerAccount
        {
            PortfolioId = secondPortfolio.Id,
            Code = "1000",
            Name = "Other portfolio cash",
            AccountType = AccountType.Asset,
            NormalBalance = NormalBalance.Debit,
            IsSystem = true,
            IsActive = true,
        });
        await setup.Db.SaveChangesAsync();
        var otherAccountId = await setup.Db.LedgerAccounts
            .Where(account => account.PortfolioId == secondPortfolio.Id).Select(account => account.Id).SingleAsync();

        var service = new AccountingPostingService(setup.Db);
        await FluentActions.Invoking(() => service.PostAsync(Proposal(otherAccountId, otherAccountId, 7005)))
            .Should().ThrowAsync<AccountingPostingValidationException>()
            .WithMessage("One or more ledger accounts are unavailable.");
    }

    [Theory]
    [InlineData("business")]
    [InlineData("journal")]
    [InlineData("receipt")]
    [InlineData("audit")]
    [InlineData("outbox")]
    [InlineData("success")]
    public async Task AtomicStages_RollBackBusinessJournalReceiptAuditAndOutboxTogether(string failureStage)
    {
        await using var setup = await _fixture.CreateContextAsync();
        var seed = new ChartOfAccountsSeedService(setup.Db);
        await seed.SeedAsync(1);
        await setup.Db.SaveChangesAsync();
        setup.Db.ChangeTracker.Clear();
        var accountIds = await setup.Db.LedgerAccounts.Where(account => account.PortfolioId == 1)
            .OrderBy(account => account.Code).Select(account => account.Id).Take(2).ToArrayAsync();
        var sourceId = 8000 + failureStage[0];
        var receiptId = Guid.NewGuid();
        var commandKey = $"accounting-stage:{failureStage}:{Guid.NewGuid():N}";

        await using var transaction = await setup.Db.Database.BeginTransactionAsync();
        setup.Db.AccountingConversionReconciliations.Add(new AccountingConversionReconciliation
        {
            PortfolioId = 1,
            SourceType = JournalSourceType.OpeningBalance,
            Currency = "USD",
            PostingRuleVersion = 1,
            SourceTotal = 100m,
            IsApproved = false,
        });
        await setup.Db.SaveChangesAsync();
        if (failureStage == "business")
            await RollbackStageAsync(transaction);
        else
        {
            await new AccountingPostingService(setup.Db).PostAsync(new AccountingProposedEntry
            {
                PortfolioId = 1,
                SourceType = JournalSourceType.OpeningBalance,
                SourceId = sourceId,
                SourceBusinessKey = $"opening-balance:{commandKey}",
                PostingRuleVersion = 1,
                EffectiveOn = new DateOnly(2026, 7, 31),
                Currency = "USD",
                Description = "Atomic stage test",
                AttemptId = Guid.NewGuid(),
                UserId = 1,
                AtomicReceiptId = receiptId,
                Lines =
                [
                    new AccountingProposedLine { LedgerAccountId = accountIds[0], DebitAmount = 100m },
                    new AccountingProposedLine { LedgerAccountId = accountIds[1], CreditAmount = 100m },
                ],
            });
            await setup.Db.SaveChangesAsync();
            if (failureStage == "journal")
                await RollbackStageAsync(transaction);
            else
            {
                setup.Db.AtomicCommandReceipts.Add(new AtomicCommandReceipt
                {
                    Id = receiptId,
                    AttemptId = Guid.NewGuid(),
                    CommandType = "AccountingStageTest",
                    IdempotencyKey = commandKey,
                    RequestFingerprint = new string('b', 64),
                    Status = AtomicCommandReceiptStatus.Completed,
                    ResultContract = "accounting-stage.v1",
                    StartedAt = DateTime.UtcNow,
                    CompletedAt = DateTime.UtcNow,
                });
                await setup.Db.SaveChangesAsync();
                if (failureStage == "receipt")
                    await RollbackStageAsync(transaction);
                else
                {
                    setup.Db.AtomicAuditLogs.Add(new AtomicAuditLog
                    {
                        AttemptId = Guid.NewGuid(),
                        CommandType = "AccountingStageTest",
                        CommandIdempotencyKey = commandKey,
                        MutationOrdinal = 1,
                        PortfolioId = 1,
                        UserId = 1,
                        EntityType = nameof(AccountingConversionReconciliation),
                        EntityId = 1,
                        Operation = AuditLogOperation.Created,
                        Timestamp = DateTime.UtcNow,
                    });
                    await setup.Db.SaveChangesAsync();
                    if (failureStage == "audit")
                        await RollbackStageAsync(transaction);
                    else
                    {
                        setup.Db.OutboxMessages.Add(new OutboxMessage
                        {
                            PortfolioId = 1,
                            MessageType = "AccountingStageTest",
                            Payload = "{}",
                            IdempotencyKey = commandKey,
                            CreatedAtUtc = DateTime.UtcNow,
                            NextAttemptAtUtc = DateTime.UtcNow,
                        });
                        await setup.Db.SaveChangesAsync();
                        if (failureStage == "outbox")
                            await RollbackStageAsync(transaction);
                        else
                            await transaction.CommitAsync();
                    }
                }
            }
        }

        setup.Db.ChangeTracker.Clear();
        var expected = failureStage == "success" ? 1 : 0;
        (await setup.Db.AccountingConversionReconciliations.CountAsync(row => row.SourceTotal == 100m))
            .Should().Be(expected);
        (await setup.Db.JournalEntries.CountAsync(entry => entry.SourceId == sourceId)).Should().Be(expected);
        (await setup.Db.AtomicCommandReceipts.CountAsync(receipt => receipt.Id == receiptId)).Should().Be(expected);
        (await setup.Db.AtomicAuditLogs.CountAsync(audit => audit.CommandIdempotencyKey == commandKey)).Should().Be(expected);
        (await setup.Db.OutboxMessages.CountAsync(outbox => outbox.IdempotencyKey == commandKey)).Should().Be(expected);
    }

    private static AccountingProposedEntry Proposal(int debitAccountId, int creditAccountId, long sourceId) =>
        new()
        {
            PortfolioId = 1,
            SourceType = JournalSourceType.TenantCharge,
            SourceId = sourceId,
            SourceBusinessKey = $"tenant-charge:test:{sourceId}",
            PostingRuleVersion = 1,
            EffectiveOn = new DateOnly(2026, 7, 31),
            Currency = "USD",
            Description = "Test tenant charge",
            AttemptId = Guid.NewGuid(),
            UserId = 1,
            AtomicReceiptId = Guid.NewGuid(),
            Lines =
            [
                new AccountingProposedLine { LedgerAccountId = debitAccountId, DebitAmount = 100m },
                new AccountingProposedLine { LedgerAccountId = creditAccountId, CreditAmount = 100m },
            ],
        };

    private static ServiceProvider CreateConcurrentPostingServices(
        string connectionString,
        ConcurrentJournalPostingGate gate)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddSingleton(gate);
        services.AddAtomicPersistenceKernel();
        services.AddScoped<ConcurrentJournalPostingHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
            builder.UseNpgsql(connectionString).UseAtomicPersistenceKernel(provider));
        return services.BuildServiceProvider();
    }

    private sealed record ConcurrentJournalPostingCommand(
        long SourceId,
        int DebitAccountId,
        int CreditAccountId,
        bool HoldBeforeCommit) : IAtomicCommandData
    {
        public const string SourceBusinessKey = "tenant-charge:atomic-concurrency";
    }

    private sealed record ConcurrentJournalPostingResult(int JournalEntryId);

    private static TransactionalWrite<ConcurrentJournalPostingCommand, ConcurrentJournalPostingResult>
        ConcurrentWrite(
            AtomicCommandIdentity identity,
            ConcurrentJournalPostingCommand command,
            ConcurrentJournalPostingHandler handler) =>
        new(
            identity.CommandType,
            WriteIdempotencyPolicy.Required,
            command,
            ConcurrentPostingCodec.ContractName,
            WriteLockPlan.None,
            handler.ExecuteAsync,
            handler.AuthorizeReplayAsync);

    private sealed class ConcurrentJournalPostingHandler
    {
        private readonly RentalCommandDbContext _db;
        private readonly ConcurrentJournalPostingGate _gate;

        public ConcurrentJournalPostingHandler(
            RentalCommandDbContext db,
            ConcurrentJournalPostingGate gate)
        {
            _db = db;
            _gate = gate;
        }

        public async Task<ConcurrentJournalPostingResult> ExecuteAsync(
            ConcurrentJournalPostingCommand command,
            IAtomicCommandContext context,
            CancellationToken ct)
        {
            if (!command.HoldBeforeCommit)
                _gate.SecondStartedPosting.TrySetResult();

            var journal = await new AccountingPostingService(_db).PostAsync(new AccountingProposedEntry
            {
                PortfolioId = 1,
                SourceType = JournalSourceType.TenantCharge,
                SourceId = command.SourceId,
                SourceBusinessKey = ConcurrentJournalPostingCommand.SourceBusinessKey,
                PostingRuleVersion = 1,
                EffectiveOn = new DateOnly(2026, 8, 1),
                Currency = "USD",
                Description = "Concurrent business-key test",
                AttemptId = context.AttemptId,
                ActorLabel = "accounting-test",
                AtomicReceiptId = context.AtomicReceiptId,
                Lines =
                [
                    new AccountingProposedLine { LedgerAccountId = command.DebitAccountId, DebitAmount = 100m },
                    new AccountingProposedLine { LedgerAccountId = command.CreditAccountId, CreditAmount = 100m },
                ],
            }, ct);

            if (command.HoldBeforeCommit)
            {
                await context.FlushBusinessAsync(ct);
                _gate.FirstJournalFlushed.TrySetResult();
                await _gate.ReleaseFirstCommit.Task.WaitAsync(ct);
            }
            else
            {
                _gate.SecondReturnedCommittedJournal.TrySetResult();
            }

            return new ConcurrentJournalPostingResult(journal.Id);
        }

        public Task AuthorizeReplayAsync(
            ConcurrentJournalPostingCommand command,
            IAtomicCommandContext context,
            CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ConcurrentJournalPostingGate
    {
        public TaskCompletionSource FirstJournalFlushed { get; } = NewSignal();
        public TaskCompletionSource SecondStartedPosting { get; } = NewSignal();
        public TaskCompletionSource SecondReturnedCommittedJournal { get; } = NewSignal();
        public TaskCompletionSource ReleaseFirstCommit { get; } = NewSignal();

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static async Task RollbackStageAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction) =>
        await transaction.RollbackAsync();
}
