using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Vendors;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Vendors;
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>PostgreSQL proof for the receipt-backed Vendor W-9 request boundary.</summary>
public sealed class VendorW9AtomicCommandTests : IAsyncLifetime
{
    private static readonly AtomicJsonResultCodec<RequestVendorW9Result> Codec =
        new("vendor-w9.request.result.v1");
    private readonly DateTime _now = new(2026, 7, 11, 23, 0, 0, DateTimeKind.Utc);
    private PostgreSqlContainer? _postgres;
    private ServiceProvider? _services;
    private bool _dockerAvailable;
    private int _portfolioId;
    private int _otherPortfolioId;
    private int _vendorId;
    private Guid _authSessionId;
    private int _accessContextId;
    private long _accessRevision;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand_vendor_w9")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
            await _postgres.StartAsync();
            _dockerAvailable = true;
        }
        catch
        {
            return;
        }

        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<CommandProbe>();
        services.AddSingleton<AuditFailureInterceptor>();
        services.AddScoped<ICurrentActor, TestActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<
            RequestVendorW9Command,
            RequestVendorW9Result,
            RequestVendorW9Handler>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_postgres!.GetConnectionString())
                .UseAtomicPersistenceKernel(provider)
                .AddInterceptors(
                    provider.GetRequiredService<CommandProbe>(),
                    provider.GetRequiredService<AuditFailureInterceptor>()));
        _services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        await using var db = NewContext();
        await db.Database.MigrateAsync();
        var actor = new ApplicationUser
        {
            Id = 73,
            UserName = "vendor-w9-owner@example.test",
            NormalizedUserName = "VENDOR-W9-OWNER@EXAMPLE.TEST",
            Email = "vendor-w9-owner@example.test",
            NormalizedEmail = "VENDOR-W9-OWNER@EXAMPLE.TEST",
            DisplayName = "Vendor W-9 Owner",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = _now,
        };
        var portfolio = new Portfolio
        {
            Name = "W-9 Portfolio",
            ManagementCompanyName = "Sample Management",
            TimeZone = "UTC",
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        var other = new Portfolio
        {
            Name = "Other Portfolio",
            ManagementCompanyName = "Other Management",
            TimeZone = "UTC",
            CreatedAt = _now,
            UpdatedAt = _now,
        };
        db.AddRange(actor, portfolio, other);
        await db.SaveChangesAsync();
        _portfolioId = portfolio.Id;
        _otherPortfolioId = other.Id;

        var accessContext = new WorkspaceAccessContext
        {
            UserId = actor.Id,
            PortfolioId = portfolio.Id,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Management,
            CreatedAtUtc = _now,
            UpdatedAtUtc = _now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = portfolio.Id,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = _now.AddDays(-1),
            CreatedAtUtc = _now,
            UpdatedAtUtc = _now,
        };
        db.Add(membership);
        await db.SaveChangesAsync();

        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembershipId = membership.Id,
            PortfolioId = portfolio.Id,
            RoleProfileId = 2,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = _now.AddDays(-1),
            CreatedAtUtc = _now,
            UpdatedAtUtc = _now,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            UserId = actor.Id,
            ActiveAccessContextId = accessContext.Id,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = _now,
            LastSeenAtUtc = _now,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(1),
        };
        db.AddRange(assignment, session);
        await db.SaveChangesAsync();
        _authSessionId = session.Id;
        _accessContextId = accessContext.Id;
        _accessRevision = accessContext.AccessRevision;

        // Keep the scoped portfolio id and target vendor id distinct so the SQL probe can
        // independently prove that both values were sent to the translated eligibility query.
        db.Vendors.Add(NewVendor(_otherPortfolioId, "+16145550999"));
        await db.SaveChangesAsync();

        var vendor = NewVendor(_portfolioId, "+16145550142");
        db.Vendors.Add(vendor);
        await db.SaveChangesAsync();
        _vendorId = vendor.Id;
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [SkippableFact]
    public async Task ConcurrentReplay_CommitsOneCanonicalReceiptIntentAndAudit()
    {
        SkipIfNoDocker();
        Probe.Clear();
        var identity = Identity("stable-concurrent");
        var command = Command(_portfolioId, _vendorId, "stable-concurrent");

        var outcomes = await Task.WhenAll(
            ExecuteAtomicAsync(identity, command, Codec),
            ExecuteAtomicAsync(identity, command, Codec));

        outcomes.Select(outcome => outcome.Disposition).Should()
            .BeEquivalentTo([AtomicCommandDisposition.Executed, AtomicCommandDisposition.Replayed]);
        outcomes[0].Value.Should().BeEquivalentTo(outcomes[1].Value);
        outcomes[0].Value.Should().BeEquivalentTo(
            new RequestVendorW9Result(RequestVendorW9Outcome.Queued, "+16145550142"));

        await using var db = NewContext();
        (await db.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
        (await db.AtomicAuditLogs.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.CommandIdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
        var intent = await db.OutboxMessages.SingleAsync(row =>
            row.PortfolioId == _portfolioId && row.MessageType == "sms");
        using var payload = JsonDocument.Parse(intent.Payload);
        payload.RootElement.GetProperty("to").GetString().Should().Be("+16145550142");
        payload.RootElement.GetProperty("message").GetString().Should().Contain("Sample Management");

        var eligibility = Probe.Commands
            .Where(command => command.CommandText.Contains(
                "vendor-w9.target-eligibility",
                StringComparison.Ordinal))
            .Should().ContainSingle(
                "the physical execution must issue exactly one tagged DB-side eligibility query")
            .Which;
        eligibility.CommandText.Should().Contain("\"Vendors\"");
        eligibility.CommandText.Should().Contain("\"Portfolios\"");
        eligibility.ParameterValues.Should().Contain(
            value => Equals(value, _vendorId),
            "vendor identity must be enforced by the translated query");
        eligibility.ParameterValues.Should().Contain(
            value => Equals(value, _portfolioId),
            "portfolio scope must be enforced by the translated query");
        typeof(RequestVendorW9Handler).Should()
            .Implement<IAtomicCommandHandler<RequestVendorW9Command, RequestVendorW9Result>>();
    }

    [SkippableFact]
    public async Task Replay_ReauthorizesAndDeniesRevokedSessionBeforeReturningReceipt()
    {
        SkipIfNoDocker();
        var identity = Identity("revoked-replay");
        var command = Command(_portfolioId, _vendorId, "revoked-replay");
        await ExecuteAtomicAsync(identity, command, Codec);

        await using (var mutate = NewContext())
        {
            var session = await mutate.AuthSessions.SingleAsync(row => row.Id == _authSessionId);
            session.Status = AuthSessionStatus.Revoked;
            session.RevokedAtUtc = _now.AddMinutes(1);
            session.RevocationReason = "vendor-w9 replay authorization proof";
            await mutate.SaveChangesAsync();
        }

        await FluentActions.Invoking(() => ExecuteAtomicAsync(identity, command, Codec))
            .Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*vendor W-9 request*");

        await using var verify = NewContext();
        (await verify.OutboxMessages.CountAsync(row => row.PortfolioId == _portfolioId)).Should().Be(1);
        (await verify.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
    }

    [SkippableFact]
    public async Task Replay_ReauthorizesAndDeniesNaturallyExpiredSessionBeforeReturningReceipt()
    {
        SkipIfNoDocker();
        var identity = Identity("expired-replay");
        var command = Command(_portfolioId, _vendorId, "expired-replay");
        await ExecuteAtomicAsync(identity, command, Codec);

        await using (var mutate = NewContext())
        {
            var session = await mutate.AuthSessions.SingleAsync(row => row.Id == _authSessionId);
            session.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
            await mutate.SaveChangesAsync();
        }

        await FluentActions.Invoking(() => ExecuteAtomicAsync(identity, command, Codec))
            .Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*vendor W-9 request*");

        await using var verify = NewContext();
        (await verify.OutboxMessages.CountAsync(row => row.PortfolioId == _portfolioId)).Should().Be(1);
        (await verify.AtomicCommandReceipts.CountAsync(row =>
            row.CommandType == identity.CommandType
            && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
    }

    [SkippableFact]
    public async Task DifferentOperations_AreDeliberateSeparateDeliveryIntents()
    {
        SkipIfNoDocker();

        await ExecuteAtomicAsync(Identity("first"), Command(_portfolioId, _vendorId, "first"), Codec);
        await ExecuteAtomicAsync(Identity("second"), Command(_portfolioId, _vendorId, "second"), Codec);

        await using var db = NewContext();
        (await db.OutboxMessages.CountAsync(row => row.PortfolioId == _portfolioId)).Should().Be(2);
        (await db.AtomicAuditLogs.CountAsync(row => row.CommandType == "vendor-w9.request")).Should().Be(2);
        (await db.AtomicCommandReceipts.CountAsync(row => row.CommandType == "vendor-w9.request")).Should().Be(2);
    }

    [SkippableFact]
    public async Task NoPhoneAndNotFound_AreCanonicalReceiptsWithoutIntentOrAudit()
    {
        SkipIfNoDocker();
        int noPhoneVendorId;
        await using (var seed = NewContext())
        {
            var vendor = NewVendor(_portfolioId, null);
            seed.Vendors.Add(vendor);
            await seed.SaveChangesAsync();
            noPhoneVendorId = vendor.Id;
        }

        var noPhone = await ExecuteAtomicAsync(
            Identity("no-phone", noPhoneVendorId),
            Command(_portfolioId, noPhoneVendorId, "no-phone"),
            Codec);
        var missing = await ExecuteAtomicAsync(
            Identity("missing", 999999),
            Command(_portfolioId, 999999, "missing"),
            Codec);
        var crossScope = await ExecuteAtomicAsync(
            Identity("cross-scope", _vendorId, _otherPortfolioId),
            Command(_otherPortfolioId, _vendorId, "cross-scope"),
            Codec);

        noPhone.Value.Outcome.Should().Be(RequestVendorW9Outcome.VendorHasNoPhone);
        missing.Value.Outcome.Should().Be(RequestVendorW9Outcome.NotFound);
        crossScope.Value.Outcome.Should().Be(RequestVendorW9Outcome.NotFound);
        await using var db = NewContext();
        (await db.AtomicCommandReceipts.CountAsync(row => row.CommandType == "vendor-w9.request"))
            .Should().Be(3);
        (await db.OutboxMessages.CountAsync()).Should().Be(0);
        (await db.AtomicAuditLogs.CountAsync(row => row.CommandType == "vendor-w9.request"))
            .Should().Be(0);
    }

    [SkippableFact]
    public async Task AuditFailure_RollsBackReceiptAndIntent_ThenSameOperationRecovers()
    {
        SkipIfNoDocker();
        var identity = Identity("audit-rollback");
        var command = Command(_portfolioId, _vendorId, "audit-rollback");
        Failure.FailAtomicAudit = true;

        var act = () => ExecuteAtomicAsync(identity, command, Codec);
        var failure = await act.Should().ThrowAsync<DbUpdateException>();
        failure.Which.InnerException.Should().BeOfType<InjectedAuditFailure>();
        Failure.FailAtomicAudit = false;

        await using (var failed = NewContext())
        {
            (await failed.AtomicCommandReceipts.CountAsync(row =>
                row.CommandType == identity.CommandType
                && row.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
            (await failed.AtomicAuditLogs.CountAsync(row =>
                row.CommandType == identity.CommandType
                && row.CommandIdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
            (await failed.OutboxMessages.CountAsync(row => row.PortfolioId == _portfolioId)).Should().Be(0);
        }

        var recovered = await ExecuteAtomicAsync(identity, command, Codec);
        recovered.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        recovered.Value.Outcome.Should().Be(RequestVendorW9Outcome.Queued);
    }

    private async Task<AtomicCommandOutcome<TResult>> ExecuteAtomicAsync<TCommand, TResult>(
        AtomicCommandIdentity identity,
        TCommand command,
        AtomicJsonResultCodec<TResult> resultCodec,
        CancellationToken ct = default)
        where TCommand : notnull, IAtomicCommandData
        where TResult : notnull
    {
        await using var scope = _services!.CreateAsyncScope();
        var atomic = scope.ServiceProvider.GetRequiredService<IAtomicUnitOfWork>();
        return await atomic.ExecuteAsync(identity, command, resultCodec, ct);
    }

    private CommandProbe Probe => _services!.GetRequiredService<CommandProbe>();
    private AuditFailureInterceptor Failure => _services!.GetRequiredService<AuditFailureInterceptor>();

    private RentalCommandDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(_postgres!.GetConnectionString())
            .Options);

    private AtomicCommandIdentity Identity(
        string operationId,
        int? vendorId = null,
        int? portfolioId = null) =>
        new("vendor-w9.request", $"{portfolioId ?? _portfolioId}:{vendorId ?? _vendorId}:{operationId}");

    private RequestVendorW9Command Command(int portfolioId, int vendorId, string operationId) =>
        new(
            portfolioId,
            vendorId,
            operationId,
            73,
            _authSessionId,
            73,
            _accessContextId,
            _accessRevision,
            _now);

    private Vendor NewVendor(int portfolioId, string? phone) => new()
    {
        PortfolioId = portfolioId,
        Name = "Ace Plumbing",
        ServiceType = "Plumbing",
        Phone = phone,
        Is1099Eligible = true,
        CreatedAt = _now,
        UpdatedAt = _now,
    };

    private void SkipIfNoDocker() =>
        Skip.IfNot(_dockerAvailable, "Docker is not available; Vendor W-9 PostgreSQL proof skipped.");

    private sealed class TestActor : ICurrentActor
    {
        public int? UserId => 73;
        public string? ActorLabel => null;
        public string? IpAddress => "127.0.0.1";
    }

    private sealed class CommandProbe : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<CapturedCommand> _commands = new();
        public IReadOnlyCollection<CapturedCommand> Commands => _commands.ToArray();
        public void Clear()
        {
            while (_commands.TryDequeue(out _)) { }
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _commands.Enqueue(new CapturedCommand(
                command.CommandText,
                command.Parameters.Cast<DbParameter>().Select(parameter => parameter.Value).ToArray()));
            return ValueTask.FromResult(result);
        }

        public sealed record CapturedCommand(
            string CommandText,
            IReadOnlyCollection<object?> ParameterValues);
    }

    private sealed class AuditFailureInterceptor : DbCommandInterceptor
    {
        public bool FailAtomicAudit { get; set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfNeeded(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfNeeded(command);
            return ValueTask.FromResult(result);
        }

        private void ThrowIfNeeded(DbCommand command)
        {
            if (FailAtomicAudit
                && command.CommandText.Contains("INSERT INTO \"AtomicAuditLogs\"", StringComparison.Ordinal))
            {
                throw new InjectedAuditFailure();
            }
        }
    }

    private sealed class InjectedAuditFailure : Exception;
}
