using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
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
        db.Portfolios.AddRange(portfolio, other);
        await db.SaveChangesAsync();
        _portfolioId = portfolio.Id;
        _otherPortfolioId = other.Id;
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
            Atomic.ExecuteAsync(identity, command, Codec),
            Atomic.ExecuteAsync(identity, command, Codec));

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

        Probe.Commands.Count(sql =>
            sql.Contains("FROM \"Vendors\"", StringComparison.Ordinal)
            && sql.Contains("INNER JOIN \"Portfolios\"", StringComparison.Ordinal))
            .Should().Be(1, "the physical execution resolves vendor, portfolio, and phone in one DB query");
    }

    [SkippableFact]
    public async Task DifferentOperations_AreDeliberateSeparateDeliveryIntents()
    {
        SkipIfNoDocker();

        await Atomic.ExecuteAsync(Identity("first"), Command(_portfolioId, _vendorId, "first"), Codec);
        await Atomic.ExecuteAsync(Identity("second"), Command(_portfolioId, _vendorId, "second"), Codec);

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

        var noPhone = await Atomic.ExecuteAsync(
            Identity("no-phone", noPhoneVendorId),
            Command(_portfolioId, noPhoneVendorId, "no-phone"),
            Codec);
        var missing = await Atomic.ExecuteAsync(
            Identity("missing", 999999),
            Command(_portfolioId, 999999, "missing"),
            Codec);
        var crossScope = await Atomic.ExecuteAsync(
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

        var act = () => Atomic.ExecuteAsync(identity, command, Codec);
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

        var recovered = await Atomic.ExecuteAsync(identity, command, Codec);
        recovered.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        recovered.Value.Outcome.Should().Be(RequestVendorW9Outcome.Queued);
    }

    private IAtomicUnitOfWork Atomic => _services!.GetRequiredService<IAtomicUnitOfWork>();
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
        new(portfolioId, vendorId, operationId, 73, _now);

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
        private readonly ConcurrentQueue<string> _commands = new();
        public IReadOnlyCollection<string> Commands => _commands.ToArray();
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
            _commands.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }
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
