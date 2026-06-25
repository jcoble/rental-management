using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

public sealed class PortfolioQaServiceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteConnection _conn;
    private readonly List<string> _commands = [];
    private readonly RentalCommandDbContext _db;

    public PortfolioQaServiceTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .AddInterceptors(new RecordingCommandInterceptor(_commands))
            .Options;

        _db = new PortfolioQaTestDbContext(options);
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
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task RecentExpensesTool_AggregatesFullFilteredWindowInSql_NotReturnedPageOnly()
    {
        var now = DateTime.UtcNow;
        for (var i = 0; i < 51; i++)
        {
            _db.Expenses.Add(new Expense
            {
                PortfolioId = PortfolioId,
                Description = $"Repair {i + 1}",
                Category = ScheduleECategory.Repairs,
                Status = ExpenseStatus.Paid,
                Amount = 1m,
                IncurredAt = now.AddMinutes(-i),
                CreatedAt = now.AddMinutes(-i),
                UpdatedAt = now.AddMinutes(-i),
            });
        }
        await _db.SaveChangesAsync();

        var answer = await AskToolAsync(
            toolName: "list_recent_expenses",
            argsJson: """{"withinDays":90}""",
            question: "Show me my recent expenses");

        using var doc = JsonDocument.Parse(answer);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(50);
        doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("total").GetDecimal().Should().Be(51m);
        _commands.Should().Contain(sql => sql.Contains("SUM", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RecentPaymentsTool_AggregatesFullFilteredWindowInSql_NotReturnedPageOnly()
    {
        var now = DateTime.UtcNow;
        var lease = SeedLease(now);
        for (var i = 0; i < 51; i++)
        {
            _db.Payments.Add(new Payment
            {
                PortfolioId = PortfolioId,
                Lease = lease,
                PaymentType = PaymentType.Rent,
                Status = PaymentStatus.Paid,
                Amount = 1m,
                DueDate = now.AddDays(-1).AddMinutes(-i),
                PaidDate = now.AddMinutes(-i),
                CreatedAt = now.AddMinutes(-i),
                UpdatedAt = now.AddMinutes(-i),
            });
        }
        await _db.SaveChangesAsync();

        var answer = await AskToolAsync(
            toolName: "list_recent_payments",
            argsJson: """{"withinDays":30}""",
            question: "Show me my recent rent payments");

        using var doc = JsonDocument.Parse(answer);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(50);
        doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("total").GetDecimal().Should().Be(51m);
        _commands.Should().Contain(sql => sql.Contains("SUM", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UpcomingEventsTool_MergesSortsAndCapsAppointmentsAndInspectionsInSql()
    {
        var now = DateTime.UtcNow;
        var property = SeedProperty(now);
        for (var i = 0; i < 26; i++)
        {
            _db.Appointments.Add(new Appointment
            {
                PortfolioId = PortfolioId,
                Property = property,
                Title = $"Appointment {i + 1}",
                Type = AppointmentType.MaintenanceVisit,
                Status = AppointmentStatus.Scheduled,
                ScheduledStart = now.AddMinutes(10 + (i * 2)),
                CreatedAt = now,
                UpdatedAt = now,
            });
            _db.Inspections.Add(new Inspection
            {
                PortfolioId = PortfolioId,
                Property = property,
                Type = InspectionType.Routine,
                Status = InspectionStatus.Scheduled,
                ScheduledFor = now.AddMinutes(11 + (i * 2)),
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        await _db.SaveChangesAsync();

        var answer = await AskToolAsync(
            toolName: "list_upcoming_events",
            argsJson: """{"withinDays":2}""",
            question: "What is on my schedule?");

        using var doc = JsonDocument.Parse(answer);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(50);
        var events = doc.RootElement.GetProperty("events");
        events[0].GetProperty("kind").GetString().Should().Be("Appointment");
        events[1].GetProperty("kind").GetString().Should().Be("Inspection");
        _commands.Should().Contain(sql =>
            sql.Contains("UNION", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            (sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) ||
             sql.Contains("FETCH", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task WorkOrdersTool_OrdersByRequestedAtInSql_ThenFormatsDatesForAnswer()
    {
        var now = DateTime.UtcNow;
        var property = SeedProperty(now);
        _db.WorkOrders.AddRange(
            new WorkOrder
            {
                PortfolioId = PortfolioId,
                Property = property,
                Title = "Older lock issue",
                Description = "The lock sticks.",
                Status = WorkOrderStatus.New,
                Priority = WorkOrderPriority.Normal,
                RequestedAt = now.AddDays(-2),
                UpdatedAt = now.AddDays(-2),
            },
            new WorkOrder
            {
                PortfolioId = PortfolioId,
                Property = property,
                Title = "Newest faucet issue",
                Description = "The faucet leaks.",
                Status = WorkOrderStatus.Scheduled,
                Priority = WorkOrderPriority.High,
                RequestedAt = now.AddDays(-1),
                ScheduledFor = now.AddDays(1),
                UpdatedAt = now.AddDays(-1),
            });
        await _db.SaveChangesAsync();

        var answer = await AskToolAsync(
            toolName: "list_work_orders",
            argsJson: """{"openOnly":true}""",
            question: "What maintenance is open?");

        using var doc = JsonDocument.Parse(answer);
        var rows = doc.RootElement;
        rows.GetArrayLength().Should().Be(2);
        rows[0].GetProperty("title").GetString().Should().Be("Newest faucet issue");
        rows[0].GetProperty("requestedAt").GetString().Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}$");

        _commands.Should().Contain(sql => sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string> AskToolAsync(string toolName, string argsJson, string question)
    {
        _commands.Clear();
        var sut = new PortfolioQaService(
            _db,
            new ToolEchoLlmProvider(toolName, argsJson),
            new ThrowingAccountingService(),
            new NoopMessagePublisher(),
            new EmptyKnowledgeBaseService(),
            NullLogger<PortfolioQaService>.Instance);

        var response = await sut.AskAsync(PortfolioId, question, history: null);
        response.ToolsUsed.Should().ContainSingle().Which.Should().Be(toolName);
        return response.Answer;
    }

    private Lease SeedLease(DateTime now)
    {
        var property = SeedProperty(now);
        var unit = new Unit
        {
            Property = property,
            UnitNumber = "A",
            MarketRent = 1400m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Avery",
            LastName = "Brooks",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var lease = new Lease
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            Tenant = tenant,
            LeaseNumber = "MGD-A-1",
            Status = LeaseStatus.Active,
            StartDate = now.AddMonths(-1),
            EndDate = now.AddYears(1),
            MonthlyRent = 1400m,
            SecurityDeposit = 1400m,
            LateFeeAmount = 50m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Leases.Add(lease);
        _db.SaveChanges();
        return lease;
    }

    private Property SeedProperty(DateTime now)
    {
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Maple Grove Duplex",
            AddressLine1 = "10 Maple",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Properties.Add(property);
        _db.SaveChanges();
        return property;
    }

    private sealed class ToolEchoLlmProvider : ILlmProvider
    {
        private readonly string _toolName;
        private readonly string _argsJson;
        private int _calls;

        public ToolEchoLlmProvider(string toolName, string argsJson)
        {
            _toolName = toolName;
            _argsJson = argsJson;
        }

        public Task<string> ChatAsync(string prompt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ExtractedFields> ExtractAsync(
            byte[] documentBytes,
            string contentType,
            string instructions,
            IReadOnlyList<ExtractionFieldSpec> fields,
            string? groundingContext = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<LlmToolResult> ChatWithToolsAsync(
            string systemPrompt,
            IReadOnlyList<LlmChatMessage> messages,
            IReadOnlyList<LlmToolSpec> tools,
            CancellationToken ct = default)
        {
            if (_calls++ == 0)
            {
                tools.Select(t => t.Name).Should().Contain(_toolName);
                return Task.FromResult(new LlmToolResult(
                    StopReason: "tool_use",
                    Text: null,
                    ToolCalls: [new LlmToolCall("call-1", _toolName, _argsJson)],
                    InputTokens: 0,
                    OutputTokens: 0,
                    ModelId: "fake"));
            }

            var toolResult = messages.Last(m => m.Role == "tool").Content ?? "{}";
            return Task.FromResult(new LlmToolResult(
                StopReason: "end",
                Text: toolResult,
                ToolCalls: [],
                InputTokens: 0,
                OutputTokens: 0,
                ModelId: "fake"));
        }
    }

    private sealed class ThrowingAccountingService : IAccountingService
    {
        public Task<AccountingSummaryResponse> GetSummaryAsync(int portfolioId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<MoneySnapshotResponse> GetSnapshotAsync(int portfolioId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<PastDueResponse> GetPastDueAsync(int portfolioId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<AccountingReportsResponse> GetReportsAsync(int portfolioId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<AccountingTransactionsResponse> GetTransactionsAsync(
            int portfolioId,
            AccountingTransactionsQuery query,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<YearEndPacketData> GetYearEndPacketDataAsync(int portfolioId, int year, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<byte[]> GetYearEndPacketAsync(int portfolioId, int year, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoopMessagePublisher : IMessagePublisher
    {
        public Task PublishAsync<TPayload>(int portfolioId, string messageType, TPayload payload, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class EmptyKnowledgeBaseService : IKnowledgeBaseService
    {
        public IReadOnlyList<KbArticleSummary> ListArticles() => [];
        public KbArticle? GetArticle(string slug) => null;
        public IReadOnlyList<KbSnippet> Search(string query, int max) => [];
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

    private sealed class PortfolioQaTestDbContext : RentalCommandDbContext
    {
        public PortfolioQaTestDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<ScanDraft>().Property(e => e.ExtractedFields).HasColumnType("TEXT");
            modelBuilder.Entity<AuditLog>().Property(e => e.OldValues).HasColumnType("TEXT");
            modelBuilder.Entity<AuditLog>().Property(e => e.NewValues).HasColumnType("TEXT");
            modelBuilder.Entity<OutboxMessage>().Property(e => e.Payload).HasColumnType("TEXT");
            modelBuilder.Entity<QueuedJob>().Property(e => e.Payload).HasColumnType("TEXT");
            modelBuilder.Entity<Expense>().Property(e => e.ReceiptData).HasColumnType("TEXT");
            modelBuilder.Entity<Lease>().ToTable("Leases");
            modelBuilder.Entity<VendorRating>().ToTable("VendorRatings");
        }
    }
}
