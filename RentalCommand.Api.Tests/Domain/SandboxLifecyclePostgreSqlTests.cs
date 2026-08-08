using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Core.Atomic;
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
    private static readonly AtomicJsonResultCodec<SandboxLifecycleResult> Codec =
        new("sandbox-lifecycle-result:v1");
    private readonly MigratedPostgreSqlFixture _fixture;

    public SandboxLifecyclePostgreSqlTests(MigratedPostgreSqlFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task LiveOnboardingChoice_AuditsPortfolioUsingAggregateId()
    {
        await using var setup = await _fixture.CreateContextAsync();
        var scope = setup.Db.SeedAdministratorScope(1, nameof(LiveOnboardingChoice_AuditsPortfolioUsingAggregateId));
        await using var services = BuildServices(setup.ConnectionString);
        await using var serviceScope = services.CreateAsyncScope();
        var atomic = serviceScope.ServiceProvider.GetRequiredService<IAtomicUnitOfWork>();
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

        var outcome = await atomic.ExecuteAsync(
            new AtomicCommandIdentity("sandbox.onboarding-choice", command.DeliveryIdempotencyKey),
            command,
            Codec);

        outcome.Value.PortfolioFound.Should().BeTrue();
        outcome.Value.OnboardingChoicePending.Should().BeFalse();
        (await setup.Db.AtomicAuditLogs.AsNoTracking().SingleAsync(audit =>
            audit.CommandType == "sandbox.onboarding-choice"
            && audit.EntityType == "Portfolio"))
            .EntityId.Should().Be(scope.PortfolioId);
    }

    private static ServiceProvider BuildServices(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<
            SandboxLifecycleCommand,
            SandboxLifecycleResult,
            SandboxLifecycleCommandHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(connectionString).UseAtomicPersistenceKernel(provider));
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }
}
