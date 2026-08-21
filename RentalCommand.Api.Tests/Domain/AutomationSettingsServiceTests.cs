using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Atomic;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class AutomationSettingsServiceTests : IAsyncLifetime
{
    private const int PortfolioId = 1;

    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _context = null!;
    private ServiceProvider _services = null!;
    private RentalCommandDbContext _db = null!;

    public AutomationSettingsServiceTests(MigratedPostgreSqlFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _context = await _fixture.CreateContextAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
            builder.UseNpgsql(_context.ConnectionString)
                .UseAtomicPersistenceKernel(provider));
        _services = services.BuildServiceProvider();
        _db = _services.GetRequiredService<RentalCommandDbContext>();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task UpdateLateFeeAutomationSettingsAsync_PersistsLateFeesAtomicallyAndKeepsRentChargesOn()
    {
        var scope = _db.SeedAdministratorScope(PortfolioId, nameof(AutomationSettingsServiceTests));
        await _db.Database.OpenConnectionAsync();
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            SET SESSION AUTHORIZATION rentalcommand_api;
            SELECT set_config('app.current_portfolio_id', {scope.PortfolioId.ToString()}, false),
                   set_config('app.auth_session_id', {scope.SessionId.ToString()}, false),
                   set_config('app.current_user_id', {scope.UserId.ToString()}, false),
                   set_config('app.current_access_context_id', {scope.AccessContextId.ToString()}, false),
                   set_config('app.access_revision', {scope.AccessRevision.ToString()}, false);
            """);
        var now = DateTime.UtcNow;
        var settings = await _db.AutomationSettings
            .SingleOrDefaultAsync(row => row.PortfolioId == PortfolioId);
        if (settings is null)
        {
            _db.AutomationSettings.Add(new AutomationSettings
            {
                PortfolioId = PortfolioId,
                EnableRentCharges = true,
                EnableLateFees = false,
                LateFeeGraceDays = 5,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
        }
        else
        {
            settings.EnableRentCharges = true;
            settings.EnableLateFees = false;
            settings.LateFeeGraceDays = 5;
            settings.UpdatedAtUtc = now;
        }
        await _db.SaveChangesAsync();
        var sut = new NotificationFoundationService(
            _db,
            TimeProvider.System,
            _services.GetRequiredService<IRequestWriteExecutor>());

        var saved = await sut.UpdateLateFeeAutomationSettingsAsync(
            scope,
            new UpdateLateFeeAutomationSettingsRequest(true, 3),
            "late-fee-settings-test",
            CancellationToken.None);

        saved.RentChargesAlwaysOn.Should().BeTrue();
        saved.EnableLateFees.Should().BeTrue();
        saved.LateFeeGraceDays.Should().Be(3);
        var persisted = await _db.AutomationSettings.AsNoTracking()
            .SingleAsync(row => row.PortfolioId == PortfolioId);
        persisted.EnableRentCharges.Should().BeTrue();
        persisted.EnableLateFees.Should().BeTrue();
        persisted.LateFeeGraceDays.Should().Be(3);
    }
}
