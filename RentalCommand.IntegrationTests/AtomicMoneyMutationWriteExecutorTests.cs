using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Money;
using RentalCommand.Data;

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
    public void ConditionalLockCodeRetainsTheThreeRequiredOrders()
    {
        var sourcePath = Path.Combine(
            FindRepositoryRoot(), "RentalCommand.Api", "Services", "Domain", "AtomicMoneyMutation.cs");
        var source = File.ReadAllText(sourcePath);

        AssertOrdered(source,
            "if (command.Domain == AtomicMoneyDomain.Expense)",
            "await context.AcquireLockAsync(\"Expense\", command.EntityId, ct);",
            "await WorkOrderProgressionLock.AcquireAsync(");
        AssertOrdered(source,
            "command.Operation == AtomicMoneyOperation.Delete)",
            "await context.AcquireLockAsync(\"CapitalAsset\", command.EntityId, ct);",
            "await context.AcquireLockAsync(\"Expense\", sourceExpenseId.Value, ct);",
            "await WorkOrderProgressionLock.AcquireAsync(");
        AssertOrdered(source,
            "await attempt.AcquireLockAsync(\n                \"OwnerDistribution\", command.EntityId, ct);",
            "await attempt.AcquireLockAsync(\"OwnerEntity\", ownerId, ct);");
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

    private static void AssertOrdered(string source, params string[] values)
    {
        var cursor = 0;
        foreach (var value in values)
        {
            var next = source.IndexOf(value, cursor, StringComparison.Ordinal);
            next.Should().BeGreaterThanOrEqualTo(0, $"'{value}' must remain in the lock sequence");
            cursor = next + value.Length;
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RentalCommand.sln")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
