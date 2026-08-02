using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.Services.Auth;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Sandbox;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

[Collection(RoleAuthorityPostgreSqlCollection.Name)]
public sealed class DemoSeedAtomicPostgreSqlTests : IAsyncLifetime
{
    private static readonly DateTime BusinessNowUtc =
        new(2027, 1, 1, 5, 0, 0, DateTimeKind.Utc);
    private static readonly AtomicJsonResultCodec<SeedDemoPortfolioResult> Codec =
        new("demo-portfolio-seed-result:v1");

    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly FailOnOutboxInsertInterceptor _failure = new();
    private MigratedPostgreSqlTestContext _context = null!;
    private ServiceProvider _services = null!;

    public DemoSeedAtomicPostgreSqlTests(MigratedPostgreSqlFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _context = await _fixture.CreateContextAsync([_failure]);
        var portfolio = await _context.Db.Portfolios.SingleAsync(row => row.Id == 1);
        portfolio.Settings = """{"onboarding":{"choice":"pending"}}""";
        _context.Db.WorkspaceAccessContexts.Add(new WorkspaceAccessContext
        {
            UserId = 1,
            PortfolioId = 1,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Management,
            CreatedAtUtc = BusinessNowUtc.AddDays(-1),
            UpdatedAtUtc = BusinessNowUtc.AddDays(-1),
            Membership = new WorkspaceMembership
            {
                PortfolioId = 1,
                Status = WorkspaceMembershipStatus.Active,
                DefaultExperience = WorkspaceExperience.Management,
                EffectiveFromUtc = BusinessNowUtc.AddDays(-1),
                CreatedAtUtc = BusinessNowUtc.AddDays(-1),
                UpdatedAtUtc = BusinessNowUtc.AddDays(-1),
            },
        });
        await _context.Db.SaveChangesAsync();
        var membershipId = await _context.Db.WorkspaceMemberships
            .Select(membership => membership.Id)
            .SingleAsync();
        _context.Db.MembershipRoleAssignments.Add(new MembershipRoleAssignment
        {
            WorkspaceMembershipId = membershipId,
            PortfolioId = 1,
            RoleProfileId = 1,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = BusinessNowUtc.AddDays(-1),
            CreatedAtUtc = BusinessNowUtc.AddDays(-1),
            UpdatedAtUtc = BusinessNowUtc.AddDays(-1),
        });
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();

        _services = BuildServices();
    }

    private ServiceProvider BuildServices(params IInterceptor[] additionalInterceptors)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddSingleton(_failure);
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<
            SeedDemoPortfolioCommand,
            SeedDemoPortfolioResult,
            DemoSeedCommandHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
        {
            builder.UseNpgsql(_context.ConnectionString)
                .AddInterceptors(provider.GetRequiredService<FailOnOutboxInsertInterceptor>())
                .UseAtomicPersistenceKernel(provider);
            if (additionalInterceptors.Length > 0)
            {
                builder.AddInterceptors(additionalInterceptors);
            }
        });
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        if (_context is not null) await _context.DisposeAsync();
    }

    [Fact]
    public async Task Rich_seed_rolls_back_on_outbox_failure_then_commits_replays_and_conflicts_once()
    {
        var failedIdentity = new AtomicCommandIdentity(
            "sandbox.demo-seed",
            "portfolio:1:rollback");
        var command = new SeedDemoPortfolioCommand(
            1,
            true,
            BusinessNowUtc,
            "demo-seed-atomic-contract");
        _failure.Armed = true;

        await FluentActions.Invoking(() => ExecuteAtomicAsync(
                failedIdentity,
                command,
                Codec))
            .Should().ThrowAsync<DbUpdateException>();

        _context.Db.ChangeTracker.Clear();
        (await _context.Db.Properties.IgnoreQueryFilters().CountAsync(row => row.PortfolioId == 1))
            .Should().Be(0);
        (await _context.Db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == failedIdentity.CommandType
            && row.IdempotencyKey == failedIdentity.IdempotencyKey)).Should().Be(0);

        var identity = new AtomicCommandIdentity(
            "sandbox.demo-seed",
            "portfolio:1:success");
        var first = await ExecuteAtomicAsync(identity, command, Codec);
        var replay = await ExecuteAtomicAsync(identity, command, Codec);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replay.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replay.Value.Should().BeEquivalentTo(first.Value);
        first.Value.Should().Match<SeedDemoPortfolioResult>(result =>
            !result.AlreadyPresent
            && result.PropertyCount == 8
            && result.UnitCount > 8
            && result.TenantCount == 22
            && result.ActiveLeaseManagementCount > 0
            && result.TenantLedgerEntryCount > 0
            && result.ExpenseCount > 0
            && result.WorkOrderCount > 0
            && result.AppointmentCount > 0
            && result.InspectionCount > 0);

        await FluentActions.Invoking(() => ExecuteAtomicAsync(
                identity,
                command with { RequirePendingSandboxOnboarding = false },
                Codec))
            .Should().ThrowAsync<AtomicIdempotencyConflictException>();

        _context.Db.ChangeTracker.Clear();
        (await _context.Db.Properties.IgnoreQueryFilters().CountAsync(row => row.PortfolioId == 1))
            .Should().Be(8);
        (await _context.Db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
        (await _context.Db.AtomicAuditLogs.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.CommandIdempotencyKey == identity.IdempotencyKey
            && row.PortfolioId == 1)).Should().BeGreaterThan(0);
        (await _context.Db.OutboxMessages.CountAsync(row =>
            row.PortfolioId == 1
            && row.IdempotencyKey == "demo-seed/1/demo-seed-atomic-contract")).Should().Be(1);
    }

    [Fact]
    public async Task Runtime_api_role_with_blank_scope_seeds_and_reconciles_a_fresh_portfolio()
    {
        var scopeAuthority = await _context.Db.Database
            .SqlQueryRaw<string>("""
                SELECT pg_get_functiondef('rc_api_scope_allows(integer)'::regprocedure) AS "Value"
                """)
            .SingleAsync();
        scopeAuthority.Should().Contain("sandbox.demo-seed");

        await using var runtimeServices = BuildServices(
            new RuntimeApiRoleInterceptor("portfolio:1:runtime-startup-first"));
        var command = new SeedDemoPortfolioCommand(
            1,
            true,
            BusinessNowUtc,
            "runtime-startup-first");

        var first = await ExecuteAtomicAsync(
            runtimeServices,
            new AtomicCommandIdentity("sandbox.demo-seed", "portfolio:1:runtime-startup-first"),
            command,
            Codec);
        await using var secondRuntimeServices = BuildServices(
            new RuntimeApiRoleInterceptor("portfolio:1:runtime-startup-second"));
        var second = await ExecuteAtomicAsync(
            secondRuntimeServices,
            new AtomicCommandIdentity("sandbox.demo-seed", "portfolio:1:runtime-startup-second"),
            command with
            {
                RequirePendingSandboxOnboarding = false,
                OperationKey = "runtime-startup-second",
            },
            Codec);

        first.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        first.Value.AlreadyPresent.Should().BeFalse();
        await using (var postCommitScope = runtimeServices.CreateAsyncScope())
        {
            var runtimeDb = postCommitScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
            (await runtimeDb.Properties.CountAsync()).Should().Be(8);
        }
        second.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        second.Value.AlreadyPresent.Should().BeTrue();

        _context.Db.ChangeTracker.Clear();
        (await _context.Db.Properties.IgnoreQueryFilters().CountAsync(row => row.PortfolioId == 1))
            .Should().Be(8);
    }

    private async Task<AtomicCommandOutcome<TResult>> ExecuteAtomicAsync<TCommand, TResult>(
        AtomicCommandIdentity identity,
        TCommand command,
        AtomicJsonResultCodec<TResult> resultCodec,
        CancellationToken ct = default)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        return await ExecuteAtomicAsync(_services, identity, command, resultCodec, ct);
    }

    private static async Task<AtomicCommandOutcome<TResult>> ExecuteAtomicAsync<TCommand, TResult>(
        ServiceProvider services,
        AtomicCommandIdentity identity,
        TCommand command,
        AtomicJsonResultCodec<TResult> resultCodec,
        CancellationToken ct = default)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        await using var scope = services.CreateAsyncScope();
        var atomic = scope.ServiceProvider.GetRequiredService<IAtomicUnitOfWork>();
        return await atomic.ExecuteAsync(identity, command, resultCodec, ct);
    }

    private sealed class RuntimeApiRoleInterceptor : DbConnectionInterceptor
    {
        private readonly string _demoSeedIdempotencyKey;

        public RuntimeApiRoleInterceptor(string demoSeedIdempotencyKey) =>
            _demoSeedIdempotencyKey = demoSeedIdempotencyKey;

        public override async Task ConnectionOpenedAsync(
            DbConnection connection,
            ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SET SESSION AUTHORIZATION rentalcommand_api;
                SELECT set_config('app.demo_seed_idempotency_key', @key, false);
                """;
            var parameter = command.CreateParameter();
            parameter.ParameterName = "key";
            parameter.Value = _demoSeedIdempotencyKey;
            command.Parameters.Add(parameter);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => 1;
        public string? ActorLabel => "integration:demo-seed";
        public string? IpAddress => "127.0.0.1";
    }

    private sealed class FailOnOutboxInsertInterceptor : SaveChangesInterceptor
    {
        public bool Armed { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context?.ChangeTracker.Entries<OutboxMessage>()
                    .Any(entry => entry.State == EntityState.Added) == true)
            {
                Armed = false;
                throw new DbUpdateException("injected demo-seed outbox failure");
            }

            return ValueTask.FromResult(result);
        }
    }
}
