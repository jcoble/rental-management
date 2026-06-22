using System.Data.Common;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using Testcontainers.PostgreSql;
using Xunit;

namespace RentalCommand.IntegrationTests;

/// <summary>
/// Verifies the fresh-DB Engine startup path where multiple workers ask for per-portfolio
/// notification settings at the same time. This depends on PostgreSQL's real unique index
/// and conflict semantics, so SQLite cannot prove it.
/// </summary>
public sealed class NotificationSettingsConcurrencyTests : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private bool _dockerAvailable;
    private string _ownerConnString = string.Empty;
    private int _portfolioId;

    public async Task InitializeAsync()
    {
        try
        {
            _pg = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("rentalcommand")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();
            await _pg.StartAsync();
            _dockerAvailable = true;
        }
        catch
        {
            _dockerAvailable = false;
            return;
        }

        _ownerConnString = _pg.GetConnectionString();

        await using var ctx = NewContext(_ownerConnString);
        await ctx.Database.MigrateAsync();

        var now = DateTime.UtcNow;
        var portfolio = new Portfolio
        {
            Name = "Fresh Startup Portfolio",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
        };
        ctx.Portfolios.Add(portfolio);
        await ctx.SaveChangesAsync();
        _portfolioId = portfolio.Id;
    }

    public async Task DisposeAsync()
    {
        if (_pg is not null)
        {
            await _pg.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task GetRuntimeAsync_ConcurrentFreshPortfolioCreation_DoesNotUseFailedInsertRace()
    {
        Skip.IfNot(_dockerAvailable, "Docker is not available; Postgres notification-settings concurrency verification skipped.");

        const int callers = 8;
        var interceptor = new NotificationSettingsRaceInterceptor(callers);

        var tasks = Enumerable.Range(0, callers)
            .Select(_ => Task.Run(async () =>
            {
                await using var db = NewContext(_ownerConnString, interceptor);
                var service = new NotificationSettingsService(
                    db,
                    new EphemeralDataProtectionProvider(),
                    Array.Empty<ISmsProvider>());

                return await service.GetRuntimeAsync(_portfolioId);
            }))
            .ToArray();

        var configs = await Task.WhenAll(tasks);

        configs.Should().HaveCount(callers);

        interceptor.CommandFailures.Should().Be(
            0,
            "concurrent get-or-create should be a normal DB-side upsert path, not a caught unique-index failure");

        await using var verify = NewContext(_ownerConnString);
        var rows = await verify.NotificationSettings
            .Where(s => s.PortfolioId == _portfolioId)
            .ToListAsync();
        rows.Should().ContainSingle();
    }

    private static RentalCommandDbContext NewContext(
        string connString,
        IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseNpgsql(connString);
        if (interceptor is not null)
            options.AddInterceptors(interceptor);
        return new RentalCommandDbContext(options.Options);
    }

    private sealed class NotificationSettingsRaceInterceptor : DbCommandInterceptor
    {
        private readonly int _expectedInitialSelects;
        private readonly TaskCompletionSource _releaseInitialSelects =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _initialSelects;
        private int _commandFailures;

        public int CommandFailures => Volatile.Read(ref _commandFailures);

        public NotificationSettingsRaceInterceptor(int expectedInitialSelects)
        {
            _expectedInitialSelects = expectedInitialSelects;
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (IsNotificationSettingsRead(command.CommandText))
            {
                var count = Interlocked.Increment(ref _initialSelects);
                if (count == _expectedInitialSelects)
                    _releaseInitialSelects.SetResult();

                if (count <= _expectedInitialSelects)
                    await _releaseInitialSelects.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }

            return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }

        public override Task CommandFailedAsync(
            DbCommand command,
            CommandErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _commandFailures);
            return base.CommandFailedAsync(command, eventData, cancellationToken);
        }

        private static bool IsNotificationSettingsRead(string commandText) =>
            commandText.Contains("FROM \"NotificationSettings\"", StringComparison.Ordinal)
            && commandText.Contains("PortfolioId", StringComparison.Ordinal);
    }
}
