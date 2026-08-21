using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Vendors;
using RentalCommand.Data;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Vendors;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class VendorRequestW9ServiceTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _ctx = null!;
    private ServiceProvider _services = null!;
    private WorkspaceReadScope _scope;

    public VendorRequestW9ServiceTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync()
    {
        _ctx = await _fixture.CreateContextAsync();
        _scope = _ctx.Db.SeedAdministratorScope(PortfolioId, nameof(VendorRequestW9ServiceTests));
        await _ctx.ActivateApiScopeAsync(_scope);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddAtomicCommandHandler<
            RequestVendorW9Command,
            RequestVendorW9Result,
            RequestVendorW9Handler>();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(_ctx.ConnectionString)
                .AddInterceptors(new RequestGucConnectionInterceptor(_scope))
                .UseAtomicPersistenceKernel(provider));
        _services = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _ctx.DisposeAsync();
    }

    private VendorService CreateSut() => new(
        _services.GetRequiredService<RentalCommandDbContext>(),
        Mock.Of<IDataUpdateService>(),
        TimeProvider.System,
        _services.GetRequiredService<IRequestWriteExecutor>());

    private Vendor SeedVendor(string? phone)
    {
        var now = DateTime.UtcNow;
        var vendor = new Vendor
        {
            PortfolioId = PortfolioId,
            Name = "Ace Plumbing",
            ServiceType = "Plumbing",
            Phone = phone,
            Is1099Eligible = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Vendors.Add(vendor);
        _ctx.Db.SaveChanges();
        return vendor;
    }

    [Fact]
    public async Task RequestW9_AtomicallyQueuesOneSmsAuditAndReceipt_AndReplays()
    {
        var vendor = SeedVendor("(614) 555-0142");
        var sut = CreateSut();

        var first = await sut.RequestW9Async(_scope, vendor.Id, "stable-op", 7);
        var replay = await sut.RequestW9Async(_scope, vendor.Id, "stable-op", 7);

        first.Should().BeEquivalentTo(RequestW9Result.Queued("+16145550142"));
        replay.Should().BeEquivalentTo(first);
        (await _ctx.Db.OutboxMessages.CountAsync()).Should().Be(1);
        (await _ctx.Db.AtomicAuditLogs.CountAsync()).Should().Be(1);
        (await _ctx.Db.AtomicCommandReceipts.CountAsync()).Should().Be(1);
        var outbox = await _ctx.Db.OutboxMessages.SingleAsync();
        outbox.MessageType.Should().Be("sms");
        using var payload = JsonDocument.Parse(outbox.Payload);
        payload.RootElement.GetProperty("to").GetString().Should().Be("+16145550142");
    }

    [Fact]
    public async Task RequestW9_DifferentOperationIds_AreDeliberateSeparateRequests()
    {
        var vendor = SeedVendor("+16145550142");
        var sut = CreateSut();

        await sut.RequestW9Async(_scope, vendor.Id, "first-action", 7);
        await sut.RequestW9Async(_scope, vendor.Id, "second-action", 7);

        (await _ctx.Db.OutboxMessages.CountAsync()).Should().Be(2);
        (await _ctx.Db.AtomicAuditLogs.CountAsync()).Should().Be(2);
        (await _ctx.Db.AtomicCommandReceipts.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task RequestW9_ReturnsNoPhoneWithReceipt_AndNoIntentOrAudit()
    {
        var vendor = SeedVendor(null);

        var result = await CreateSut().RequestW9Async(_scope, vendor.Id, "no-phone", 7);

        result.Outcome.Should().Be(RequestW9Outcome.VendorHasNoPhone);
        (await _ctx.Db.AtomicCommandReceipts.CountAsync()).Should().Be(1);
        (await _ctx.Db.OutboxMessages.CountAsync()).Should().Be(0);
        (await _ctx.Db.AtomicAuditLogs.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(1, 9999)]
    [InlineData(999, 1)]
    public async Task RequestW9_ReturnsNotFoundWithoutCrossPortfolioSideEffects(
        int portfolioId,
        int vendorSelector)
    {
        var vendor = SeedVendor("+16145550142");
        var vendorId = vendorSelector == 1 ? vendor.Id : vendorSelector;

        var scope = new WorkspaceReadScope(
            portfolioId,
            _scope.UserId,
            _scope.SessionId,
            _scope.AccessContextId,
            _scope.AccessRevision);
        var result = await CreateSut().RequestW9Async(
            scope, vendorId, $"not-found-{portfolioId}", 7);

        result.Outcome.Should().Be(RequestW9Outcome.NotFound);
        (await _ctx.Db.OutboxMessages.CountAsync()).Should().Be(0);
        (await _ctx.Db.AtomicAuditLogs.CountAsync()).Should().Be(0);
    }

    private sealed class RequestGucConnectionInterceptor(WorkspaceReadScope scope) : DbConnectionInterceptor
    {
        public override async Task ConnectionOpenedAsync(
            DbConnection connection,
            ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SET SESSION AUTHORIZATION rentalcommand_api;
                SELECT set_config('app.current_portfolio_id', @portfolio_id, false),
                       set_config('app.auth_session_id', @auth_session_id, false),
                       set_config('app.current_user_id', @user_id, false),
                       set_config('app.current_access_context_id', @access_context_id, false),
                       set_config('app.access_revision', @access_revision, false);
                """;
            AddParameter(command, "portfolio_id", scope.PortfolioId.ToString());
            AddParameter(command, "auth_session_id", scope.SessionId.ToString());
            AddParameter(command, "user_id", scope.UserId.ToString());
            AddParameter(command, "access_context_id", scope.AccessContextId.ToString());
            AddParameter(command, "access_revision", scope.AccessRevision.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static void AddParameter(DbCommand command, string name, string value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $"@{name}";
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
    }
}
