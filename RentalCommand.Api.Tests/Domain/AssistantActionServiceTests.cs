using FluentAssertions;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

public sealed class AssistantActionServiceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteConnection _conn;
    private readonly RentalCommandDbContext _db;
    private readonly List<string> _commands = [];
    private readonly AssistantActionService _sut;

    public AssistantActionServiceTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .AddInterceptors(new RecordingCommandInterceptor(_commands))
            .Options;

        _db = new AssistantActionTestDbContext(options);
        _db.Database.EnsureCreated();
        _db.Portfolios.Add(new Portfolio
        {
            Id = PortfolioId,
            Name = "Test Portfolio",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.Properties.Add(new Property
        {
            PortfolioId = PortfolioId,
            Name = "Eastland 8-Plex",
            AddressLine1 = "88 Eastland Ave",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        var expenseService = new ExpenseService(_db, new NoopDataUpdateService(), Mock.Of<IFileStorage>(), TimeProvider.System);
        _sut = new AssistantActionService(_db, expenseService, TimeProvider.System);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task DraftAsync_ForExpenseAction_RequiresWriteModeAndResolvesPropertyDbSide()
    {
        _commands.Clear();

        var result = await _sut.DraftAsync(
            PortfolioId,
            new AssistantActionDraftRequest
            {
                Command = "log a $200 plumbing expense for Eastland",
                WriteModeEnabled = false,
            });

        result.Status.Should().Be(AssistantActionStatus.WriteModeRequired);
        result.RequiresWriteMode.Should().BeTrue();
        result.CanExecute.Should().BeFalse();
        result.Draft.Should().NotBeNull();
        result.Draft!.Kind.Should().Be(AssistantActionKind.CreateExpense);
        result.Draft.Expense!.Amount.Should().Be(200m);
        result.Draft.Expense.Category.Should().Be(ScheduleECategory.Repairs);
        result.Draft.Expense.Status.Should().Be(ExpenseStatus.Paid);
        result.Draft.Expense.PropertyName.Should().Be("Eastland 8-Plex");

        _commands.Should().Contain(sql =>
            sql.Contains("FROM \"Properties\"", StringComparison.OrdinalIgnoreCase) &&
            (sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) ||
             sql.Contains("FETCH", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task DraftAsync_ForReadQuestion_ReturnsUnsupportedWithoutPretendingToWrite()
    {
        var result = await _sut.DraftAsync(
            PortfolioId,
            new AssistantActionDraftRequest
            {
                Command = "who owes rent?",
                WriteModeEnabled = true,
            });

        result.Status.Should().Be(AssistantActionStatus.Unsupported);
        result.CanExecute.Should().BeFalse();
        result.Draft.Should().BeNull();
    }

    [Fact]
    public async Task DraftAsync_WhenPropertyHintIncludesUnitSuffix_ResolvesPropertyAndCleansDescription()
    {
        var result = await _sut.DraftAsync(
            PortfolioId,
            new AssistantActionDraftRequest
            {
                Command = "Create a 42 dollar plumbing expense for Eastland 8-Plex Unit 2 paid today.",
                WriteModeEnabled = true,
            });

        result.Status.Should().Be(AssistantActionStatus.DraftReady);
        result.Draft!.Expense!.PropertyName.Should().Be("Eastland 8-Plex");
        result.Draft.Expense.Description.Should().Be("plumbing");
    }

    [Fact]
    public async Task ExecuteAsync_RequiresWriteModeAndExplicitConfirmation()
    {
        var draft = (await _sut.DraftAsync(
            PortfolioId,
            new AssistantActionDraftRequest
            {
                Command = "log a $200 plumbing expense for Eastland",
                WriteModeEnabled = true,
            })).Draft;

        var noWriteMode = await _sut.ExecuteAsync(
            PortfolioId,
            new AssistantActionExecuteRequest
            {
                WriteModeEnabled = false,
                Confirmed = true,
                Draft = draft,
            });

        noWriteMode.Status.Should().Be(AssistantActionStatus.WriteModeRequired);
        (await _db.Expenses.CountAsync()).Should().Be(0);

        var notConfirmed = await _sut.ExecuteAsync(
            PortfolioId,
            new AssistantActionExecuteRequest
            {
                WriteModeEnabled = true,
                Confirmed = false,
                Draft = draft,
            });

        notConfirmed.Status.Should().Be(AssistantActionStatus.NotConfirmed);
        (await _db.Expenses.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_WithConfirmedExpenseDraft_CreatesExpense()
    {
        var draft = (await _sut.DraftAsync(
            PortfolioId,
            new AssistantActionDraftRequest
            {
                Command = "log a $275.20 plumbing expense for Eastland",
                WriteModeEnabled = true,
            })).Draft;

        var result = await _sut.ExecuteAsync(
            PortfolioId,
            new AssistantActionExecuteRequest
            {
                WriteModeEnabled = true,
                Confirmed = true,
                Draft = draft,
            });

        result.Status.Should().Be(AssistantActionStatus.Created);
        result.EntityId.Should().NotBeNull();
        result.DetailHref.Should().Be($"/expenses/{result.EntityId}");
        result.Expense!.Amount.Should().Be(275.20m);

        var fromDb = await _db.Expenses.AsNoTracking().SingleAsync();
        fromDb.Amount.Should().Be(275.20m);
        fromDb.Category.Should().Be(ScheduleECategory.Repairs);
        fromDb.Description.Should().Contain("plumbing");
        fromDb.Notes.Should().Contain("explicit user confirmation");
    }

    private sealed class NoopDataUpdateService : IDataUpdateService
    {
        public Task BroadcastEntityUpdateAsync(int portfolioId, string entityType, int entityId, object data, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task BroadcastEntityDeleteAsync(int portfolioId, string entityType, int entityId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingCommandInterceptor : DbCommandInterceptor
    {
        private readonly List<string> _commands;

        public RecordingCommandInterceptor(List<string> commands) => _commands = commands;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            _commands.Add(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class AssistantActionTestDbContext : RentalCommand.TestCommon.SqliteCompatibleRentalCommandDbContext
    {
        public AssistantActionTestDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }
    }
}
