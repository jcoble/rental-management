using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Writes;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Money;
using RentalCommand.Data;
using RentalCommand.TestCommon;
using System.Reflection;
using System.Text.Json;

namespace RentalCommand.IntegrationTests;

public sealed class AtomicMoneyMutationWriteExecutorTests
{
    private static readonly Guid SessionId =
        Guid.Parse("12121212-1212-1212-1212-121212121212");
    private static readonly DateTime BusinessNow =
        new(2099, 8, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void OneEnvelopePreservesIdentitiesContractAndFrozenFingerprints()
    {
        var commands = new[]
        {
            Command(AtomicMoneyDomain.Expense, AtomicMoneyOperation.Update, 41, "expense"),
            Command(AtomicMoneyDomain.Loan, AtomicMoneyOperation.PostPayment, 42, "loan"),
            Command(AtomicMoneyDomain.CapitalAsset, AtomicMoneyOperation.Delete, 43, "capital"),
            Command(AtomicMoneyDomain.OwnerDistribution, AtomicMoneyOperation.Update, 44, "owner"),
            Command(AtomicMoneyDomain.PropertyDisposition, AtomicMoneyOperation.Update, 45, "disposition"),
        };
        var expectedFingerprints = new[]
        {
            "1b9005241c734cdbf846401cc85e167892d799588482c2f42bf3f068a14dae02",
            "29a66b2c20a50ee4695560747c5d58eb971151e5314126e442bf5ab7a57fa30d",
            "22b4e97a74d63d500563339557fa8a266c73352b871da2043896c9821bf9da58",
            "95b773bb81476e7108e51491fd45ed2c9e648b941a73644a09173638a7b59dea",
            "f37c04dbe6bb8c9a81139ad1b06f757ac4eb5e869cea0257a392a7f433b07c5b",
        };

        using var db = NewDb();
        commands.Select(AtomicCommandFingerprint.Create).Should().Equal(expectedFingerprints);
        foreach (var command in commands)
        {
            var identity = AtomicMoneyMutation.Identity(command);
            var write = AtomicMoneyMutation.Write(command, db);
            write.OperationName.Should().Be(identity.CommandType);
            write.ResultContract.Should().Be("money.scoped-mutation.v2");
            write.Request.Should().BeSameAs(command);
            identity.IdempotencyKey.Should().Be(
                $"{command.PortfolioId}:{command.AccessContextId}:{command.Domain}:{command.Operation}:" +
                $"{command.EntityId}:{command.IdempotencyKey}");
        }
    }

    [Fact]
    public async Task ConditionalShapesKeepOnlyTheCommonPrefixInTheFixedPlan()
    {
        using var db = NewDb();
        var shapes = new[]
        {
            Command(AtomicMoneyDomain.Expense, AtomicMoneyOperation.Update, 41, "expense-lock"),
            Command(AtomicMoneyDomain.CapitalAsset, AtomicMoneyOperation.Delete, 42, "capital-lock"),
            Command(AtomicMoneyDomain.OwnerDistribution, AtomicMoneyOperation.Update, 43, "owner-lock"),
        };

        foreach (var command in shapes)
        {
            var acquired = new List<string>();
            var recorder = new Mock<IAtomicCommandContext>();
            recorder.Setup(item => item.AcquireLockAsync(
                    It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Callback<string, int, CancellationToken>((name, id, _) => acquired.Add($"{name}:{id}"))
                .Returns(Task.CompletedTask);
            recorder.Setup(item => item.AcquireLockAsync(
                    It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Callback<string, Guid, CancellationToken>((name, id, _) => acquired.Add($"{name}:{id}"))
                .Returns(Task.CompletedTask);

            var plan = AtomicMoneyMutation.Write(command, db).LockPlan;
            foreach (var writeLock in plan.Locks)
                await writeLock.AcquireAsync(recorder.Object);

            acquired.Should().Equal(
                $"AuthSession:{SessionId}",
                "WorkspaceAccessContext:9",
                "Portfolio:7");
        }
    }

    [Fact]
    public async Task ConditionalLockRulesAcquireResolvedIdsInOrder()
    {
        await using var fixture = await ConditionalLockFixture.CreateAsync();

        await fixture.AssertProgressionAsync(
            new AtomicMoneyMutationCommand(7, 8, SessionId, 9, 10, "money.test",
                AtomicMoneyDomain.Expense, AtomicMoneyOperation.Update, 41, "expense-rule",
                JsonSerializer.Serialize(new UpdateExpenseRequest { WorkOrderId = 52 }), BusinessNow),
            "Expense:41", "WorkOrder:51", "WorkOrder:52");
        await fixture.AssertProgressionAsync(
            Command(AtomicMoneyDomain.CapitalAsset, AtomicMoneyOperation.Delete, 42, "capital-rule"),
            "CapitalAsset:42", "Expense:53", "WorkOrder:54");
        await fixture.AssertDistributionAsync(
            DistributionCommand(43, 72),
            "OwnerDistribution:43", "OwnerEntity:72");
    }

    [Fact]
    public async Task FrozenLegacyExpenseReceiptWithTrailingWhitespaceReplaysWithoutDuplicate()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new SqliteCompatibleRentalCommandDbContext(
            new DbContextOptionsBuilder<RentalCommandDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Portfolios.Add(Portfolio());
        db.Expenses.Add(Expense(81, null));
        await db.SaveChangesAsync();

        const string rawKey = "assistant-expense ";
        var command = AtomicMoneyMutation.Command(
            new WorkspaceReadScope(7, 8, SessionId, 9, 10), CapabilityKeys.MoneyExpensesManage,
            AtomicMoneyDomain.Expense, AtomicMoneyOperation.Create, 0, rawKey,
            new CreateExpenseRequest { Description = "Should replay", Amount = 25m, IncurredAt = BusinessNow },
            BusinessNow);
        var identity = AtomicMoneyMutation.Identity(command);
        var stored = new AtomicMoneyMutationResult(true, true, 81,
            JsonSerializer.Serialize(new ExpenseResponse { Id = 81, PortfolioId = 7, Description = "Stored" }));
        db.AtomicCommandReceipts.Add(new AtomicCommandReceipt
        {
            Id = Guid.NewGuid(), AttemptId = Guid.NewGuid(), CommandType = identity.CommandType,
            IdempotencyKey = identity.IdempotencyKey,
            RequestFingerprint = AtomicCommandFingerprint.Create(command),
            Status = AtomicCommandReceiptStatus.Completed, ResultContract = AtomicMoneyMutation.Codec.ContractName,
            ResultJson = AtomicMoneyMutation.Codec.Serialize(stored), StartedAt = BusinessNow, CompletedAt = BusinessNow,
        });
        await db.SaveChangesAsync();
        var before = await db.Expenses.CountAsync();

        var executor = new Mock<IWriteExecutor>(MockBehavior.Strict);
        executor.Setup(item => item.ExecuteAsync(
                identity.IdempotencyKey,
                It.IsAny<TransactionalWrite<AtomicMoneyMutationCommand, AtomicMoneyMutationResult>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AtomicCommandOutcome<AtomicMoneyMutationResult>(
                stored, AtomicCommandDisposition.Replayed, Guid.NewGuid()));
        var service = new ExpenseService(
            db, Mock.Of<IFileStorage>(), TimeProvider.System, new RequestWriteExecutor(executor.Object));

        var replay = await service.CreateAsync(
            new WorkspaceReadScope(7, 8, SessionId, 9, 10),
            new CreateExpenseRequest { Description = "Should replay", Amount = 25m, IncurredAt = BusinessNow }, rawKey);

        replay!.Id.Should().Be(81);
        replay.Description.Should().Be("Stored");
        (await db.Expenses.CountAsync()).Should().Be(before);
    }

    [Fact]
    public async Task LegacyHandlerArmThrows()
    {
        var command = Command(AtomicMoneyDomain.Expense, AtomicMoneyOperation.Update, 41, "retired");
        var handler = new AtomicMoneyMutationHandler(null!);

        await handler.Invoking(item => item.HandleAsync(command, null!, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Atomic money mutations no longer use the legacy atomic handler.");
    }

    private static AtomicMoneyMutationCommand Command(
        AtomicMoneyDomain domain, AtomicMoneyOperation operation, int entityId, string key) =>
        new(7, 8, SessionId, 9, 10, "money.test", domain, operation, entityId, key, "{}", BusinessNow);

    private static RentalCommandDbContext NewDb() =>
        new(new DbContextOptionsBuilder<RentalCommandDbContext>().Options);

    private static AtomicMoneyMutationCommand DistributionCommand(int entityId, int ownerId) =>
        new(7, 8, SessionId, 9, 1, CapabilityKeys.MoneyDisbursementsManage,
            AtomicMoneyDomain.OwnerDistribution, AtomicMoneyOperation.Update, entityId, "owner-rule",
            JsonSerializer.Serialize(new UpdateOwnerDistributionRequest { OwnerEntityId = ownerId, Amount = 125m }),
            BusinessNow);

    private static Portfolio Portfolio() => new()
    {
        Id = 7, Name = "Lock recorder", ManagementCompanyName = "Test", TimeZone = "UTC",
        CreatedAt = BusinessNow, UpdatedAt = BusinessNow,
    };

    private static Expense Expense(int id, int? workOrderId) => new()
    {
        Id = id, PortfolioId = 7, Description = $"Expense {id}", Amount = 100m,
        IncurredAt = BusinessNow, WorkOrderId = workOrderId, CreatedAt = BusinessNow, UpdatedAt = BusinessNow,
    };

    private sealed class ConditionalLockFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly RentalCommandDbContext _db;

        private ConditionalLockFixture(SqliteConnection connection, RentalCommandDbContext db)
        {
            _connection = connection;
            _db = db;
        }

        public static async Task<ConditionalLockFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new SqliteCompatibleRentalCommandDbContext(
                new DbContextOptionsBuilder<RentalCommandDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            db.Portfolios.Add(Portfolio());
            db.Properties.Add(new Property
            {
                Id = 11, PortfolioId = 7, Name = "Lock property", AddressLine1 = "1 Main",
                City = "Columbus", State = "OH", PostalCode = "43215",
                CreatedAt = BusinessNow, UpdatedAt = BusinessNow,
            });
            db.WorkOrders.AddRange(new[] { 51, 52, 54 }.Select(id => new WorkOrder
            {
                Id = id, PortfolioId = 7, PropertyId = 11, Title = $"Work {id}",
                Description = "Recorder", RequestedAt = BusinessNow, UpdatedAt = BusinessNow,
            }));
            db.Expenses.AddRange(Expense(41, 51), Expense(53, 54));
            db.CapitalAssets.Add(new CapitalAsset
            {
                Id = 42, PortfolioId = 7, PropertyId = 11, SourceExpenseId = 53,
                Description = "Asset", CostBasis = 100m, InServiceDate = BusinessNow,
                CreatedAt = BusinessNow, UpdatedAt = BusinessNow,
            });
            db.OwnerEntities.AddRange(
                new OwnerEntity { Id = 71, PortfolioId = 7, Name = "Current", CreatedAt = BusinessNow, UpdatedAt = BusinessNow },
                new OwnerEntity { Id = 72, PortfolioId = 7, Name = "Effective", CreatedAt = BusinessNow, UpdatedAt = BusinessNow });
            db.OwnerDistributions.Add(new OwnerDistribution
            {
                Id = 43, PortfolioId = 7, OwnerEntityId = 71, Date = BusinessNow, Amount = 100m,
                Status = OwnerDistributionStatus.Draft, CreatedAt = BusinessNow, UpdatedAt = BusinessNow,
            });
            SeedAuthority(db);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            return new ConditionalLockFixture(connection, db);
        }

        public async Task AssertProgressionAsync(AtomicMoneyMutationCommand command, params string[] conditional)
        {
            var (context, acquired) = Recorder();
            await AcquirePrefixAsync(command, context);
            await InvokeAsync(new AtomicMoneyMutationHandler(_db), "AcquireProgressionWorkOrderLockAsync",
                command, context, CancellationToken.None);
            acquired.Should().Equal(Prefix().Concat(conditional));
        }

        public async Task AssertDistributionAsync(AtomicMoneyMutationCommand command, params string[] conditional)
        {
            var (context, acquired) = Recorder();
            await AcquirePrefixAsync(command, context);
            await InvokeAsync(new AtomicMoneyMutationHandler(_db), "MutateDistributionAsync",
                command, context, BusinessNow, BusinessNow, BusinessNow.Date, CancellationToken.None);
            acquired.Should().Equal(Prefix().Concat(conditional));
        }

        public async ValueTask DisposeAsync()
        {
            await _db.DisposeAsync();
            await _connection.DisposeAsync();
        }

        private static void SeedAuthority(RentalCommandDbContext db)
        {
            db.Users.Add(new ApplicationUser { Id = 8, UserName = "recorder@test", DisplayName = "Recorder", CreatedAt = BusinessNow });
            db.WorkspaceAccessContexts.Add(new WorkspaceAccessContext
            {
                Id = 9, UserId = 8, PortfolioId = 7, CreatedAtUtc = BusinessNow, UpdatedAtUtc = BusinessNow,
            });
            db.WorkspaceMemberships.Add(new WorkspaceMembership
            {
                Id = 91, AccessContextId = 9, PortfolioId = 7, EffectiveFromUtc = BusinessNow.AddDays(-1),
                CreatedAtUtc = BusinessNow, UpdatedAtUtc = BusinessNow,
            });
            db.MembershipRoleAssignments.Add(new MembershipRoleAssignment
            {
                Id = 92, WorkspaceMembershipId = 91, PortfolioId = 7, RoleProfileId = 1,
                ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
                EffectiveFromUtc = BusinessNow.AddDays(-1), CreatedAtUtc = BusinessNow, UpdatedAtUtc = BusinessNow,
            });
            db.AuthSessions.Add(new AuthSession
            {
                Id = SessionId, UserId = 8, ActiveAccessContextId = 9,
                CreatedAtUtc = BusinessNow, LastSeenAtUtc = BusinessNow, ExpiresAtUtc = BusinessNow.AddDays(1),
            });
        }

        private static (IAtomicCommandContext Context, List<string> Acquired) Recorder()
        {
            var acquired = new List<string>();
            var recorder = new Mock<IAtomicCommandContext>();
            recorder.Setup(item => item.AcquireLockAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Callback<string, int, CancellationToken>((name, id, _) => acquired.Add($"{name}:{id}"))
                .Returns(Task.CompletedTask);
            recorder.Setup(item => item.AcquireLockAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Callback<string, Guid, CancellationToken>((name, id, _) => acquired.Add($"{name}:{id}"))
                .Returns(Task.CompletedTask);
            recorder.Setup(item => item.FlushBusinessAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AtomicBusinessFlush(0, []));
            return (recorder.Object, acquired);
        }

        private static async Task AcquirePrefixAsync(AtomicMoneyMutationCommand command, IAtomicCommandContext context)
        {
            foreach (var writeLock in AtomicMoneyMutation.Write(command, null!).LockPlan.Locks)
                await writeLock.AcquireAsync(context);
        }

        private static string[] Prefix() =>
            [$"AuthSession:{SessionId}", "WorkspaceAccessContext:9", "Portfolio:7"];

        private static async Task InvokeAsync(object target, string methodName, params object[] args)
        {
            var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(target.GetType().Name, methodName);
            await (Task)method.Invoke(target, args)!;
        }
    }
}
