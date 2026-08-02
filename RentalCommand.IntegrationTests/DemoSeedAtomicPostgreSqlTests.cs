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
using RentalCommand.Data.Accounting;
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
                RoleAssignments =
                [
                    new MembershipRoleAssignment
                    {
                        PortfolioId = 1,
                        RoleProfileId = 1,
                        ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
                        Status = MembershipRoleAssignmentStatus.Active,
                        EffectiveFromUtc = BusinessNowUtc.AddDays(-1),
                        CreatedAtUtc = BusinessNowUtc.AddDays(-1),
                        UpdatedAtUtc = BusinessNowUtc.AddDays(-1),
                    },
                ],
            },
        });
        await new ChartOfAccountsSeedService(_context.Db).SeedAsync(1);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();

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
            builder.UseNpgsql(_context.ConnectionString)
                .AddInterceptors(provider.GetRequiredService<FailOnOutboxInsertInterceptor>())
                .UseAtomicPersistenceKernel(provider));
        _services = services.BuildServiceProvider(new ServiceProviderOptions
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
        (await _context.Db.JournalEntries.CountAsync(row => row.PortfolioId == 1))
            .Should().Be(1);
        (await _context.Db.JournalLines.CountAsync(row => row.JournalEntry.PortfolioId == 1))
            .Should().Be(2);
        (await _context.Db.OutboxMessages.CountAsync(row =>
            row.PortfolioId == 1
            && row.IdempotencyKey == "demo-seed/1/demo-seed-atomic-contract")).Should().Be(1);
    }

    private async Task<AtomicCommandOutcome<TResult>> ExecuteAtomicAsync<TCommand, TResult>(
        AtomicCommandIdentity identity,
        TCommand command,
        AtomicJsonResultCodec<TResult> resultCodec,
        CancellationToken ct = default)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        await using var scope = _services.CreateAsyncScope();
        var atomic = scope.ServiceProvider.GetRequiredService<IAtomicUnitOfWork>();
        return await atomic.ExecuteAsync(identity, command, resultCodec, ct);
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
