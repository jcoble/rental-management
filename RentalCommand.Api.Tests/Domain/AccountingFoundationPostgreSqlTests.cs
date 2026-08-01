using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Models.Accounting;
using RentalCommand.Data.Accounting;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class AccountingFoundationPostgreSqlTests
{
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

    private static async Task RollbackStageAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction) =>
        await transaction.RollbackAsync();
}
