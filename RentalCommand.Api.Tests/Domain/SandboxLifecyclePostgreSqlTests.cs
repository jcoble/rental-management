using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Sandbox;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class SandboxLifecyclePostgreSqlTests
{
    private readonly MigratedPostgreSqlFixture _fixture;

    public SandboxLifecyclePostgreSqlTests(MigratedPostgreSqlFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task LiveOnboardingChoice_AuditsPortfolioUsingAggregateId()
    {
        await using var setup = await _fixture.CreateContextAsync();
        var scope = setup.Db.SeedAdministratorScope(1, nameof(LiveOnboardingChoice_AuditsPortfolioUsingAggregateId));
        await using var services = BuildServices(setup.ConnectionString);
        await using var serviceScope = services.CreateAsyncScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var writes = serviceScope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>();
        var command = new SandboxLifecycleCommand(
            scope.PortfolioId,
            scope.UserId,
            scope.SessionId,
            scope.AccessContextId,
            scope.AccessRevision,
            SandboxLifecycleOperation.ApplyOnboardingChoice,
            SandboxOnboardingChoice.Live,
            DateTime.UtcNow,
            $"sandbox-onboarding-live:{Guid.NewGuid():N}");

        var outcome = await writes.ExecuteExactAsync(
            command.DeliveryIdempotencyKey, SandboxLifecycleWriteSupport.Write(db, command));

        outcome.Value.PortfolioFound.Should().BeTrue();
        outcome.Value.OnboardingChoicePending.Should().BeFalse();
        (await setup.Db.AtomicAuditLogs.AsNoTracking().SingleAsync(audit =>
            audit.CommandType == "sandbox.onboarding-choice"
            && audit.EntityType == "Portfolio"))
            .EntityId.Should().Be(scope.PortfolioId);
    }

    [Fact]
    public async Task GoLive_WipesSandboxBankStatementsAndPreservesBankConnection()
    {
        await using var setup = await _fixture.CreateContextAsync();
        var scope = setup.Db.SeedAdministratorScope(1, nameof(GoLive_WipesSandboxBankStatementsAndPreservesBankConnection));
        var now = DateTime.UtcNow;
        var portfolio = await setup.Db.Portfolios.SingleAsync(row => row.Id == scope.PortfolioId);
        portfolio.IsSandbox = true;
        var connection = new BankConnection
        {
            PortfolioId = scope.PortfolioId,
            Provider = "Manual",
            InstitutionName = "Sandbox Bank",
            AccountName = "Checking",
            AccountMask = "1212",
            Status = "Active",
            CreatedAt = now,
            UpdatedAt = now,
        };
        setup.Db.BankConnections.Add(connection);
        await setup.Db.SaveChangesAsync();
        setup.Db.BankStatements.Add(new BankStatement
        {
            PortfolioId = scope.PortfolioId,
            BankConnectionId = connection.Id,
            PeriodStart = new DateOnly(2027, 1, 1),
            PeriodEnd = new DateOnly(2027, 1, 31),
            OpeningBalance = 1_000m,
            ClosingBalance = 1_250m,
            StatementMovement = 250m,
            IsoCurrencyCode = "USD",
            ImportedAtUtc = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await setup.Db.SaveChangesAsync();

        await using var services = BuildServices(setup.ConnectionString);
        await using var serviceScope = services.CreateAsyncScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var writes = serviceScope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>();
        var command = new SandboxLifecycleCommand(
            scope.PortfolioId,
            scope.UserId,
            scope.SessionId,
            scope.AccessContextId,
            scope.AccessRevision,
            SandboxLifecycleOperation.GoLive,
            null,
            now,
            $"sandbox-go-live-bank-statements:{Guid.NewGuid():N}");

        var outcome = await writes.ExecuteExactAsync(
            command.DeliveryIdempotencyKey, SandboxLifecycleWriteSupport.Write(db, command));

        outcome.Value.IsSandbox.Should().BeFalse();
        setup.Db.ChangeTracker.Clear();
        (await setup.Db.BankStatements.AsNoTracking()
            .CountAsync(row => row.PortfolioId == scope.PortfolioId)).Should().Be(0);
        (await setup.Db.BankConnections.AsNoTracking()
            .CountAsync(row => row.PortfolioId == scope.PortfolioId)).Should().Be(1);
    }

    private static ServiceProvider BuildServices(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(connectionString).UseAtomicPersistenceKernel(provider));
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }
}
