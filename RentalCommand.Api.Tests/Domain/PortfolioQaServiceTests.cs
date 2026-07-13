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
        _db.Users.Add(new ApplicationUser
        {
            Id = 1,
            UserName = "portfolio-qa@example.test",
            NormalizedUserName = "PORTFOLIO-QA@EXAMPLE.TEST",
            Email = "portfolio-qa@example.test",
            NormalizedEmail = "PORTFOLIO-QA@EXAMPLE.TEST",
            DisplayName = "Portfolio QA Actor",
            CreatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
        _db.Database.ExecuteSqlRaw("""
            CREATE VIEW "vw_lease_management_lifecycle" AS
            SELECT management."PortfolioId" AS "PortfolioId",
                   management."Id" AS "LeaseManagementId",
                   management."UnitId" AS "UnitId",
                   CASE WHEN agreement."TermEndOn" IS NOT NULL
                             AND agreement."TermEndOn" < date('now')
                        THEN 'Closed' ELSE 'Occupied' END AS "Lifecycle",
                   agreement."Id" AS "CurrentAgreementId",
                   trim(tenant."FirstName" || ' ' || tenant."LastName") AS "CurrentPrimaryTenantName"
            FROM "LeaseManagements" AS management
            JOIN "LeaseAgreements" AS agreement
              ON agreement."PortfolioId" = management."PortfolioId"
             AND agreement."LeaseManagementId" = management."Id"
             AND agreement."VoidedAtUtc" IS NULL
             AND agreement."DraftCanceledAtUtc" IS NULL
            LEFT JOIN "LeaseManagementParties" AS party
              ON party."PortfolioId" = management."PortfolioId"
             AND party."LeaseManagementId" = management."Id"
             AND party."Role" = 'PrimaryTenant'
             AND party."EffectiveThrough" IS NULL
            LEFT JOIN "Tenants" AS tenant
              ON tenant."PortfolioId" = party."PortfolioId"
             AND tenant."Id" = party."TenantId"
            WHERE management."CanceledAtUtc" IS NULL
              AND management."AccountClosedAtUtc" IS NULL
            """);
        _db.Database.ExecuteSqlRaw("""
            CREATE VIEW "vw_tenant_charge_balances" AS
            SELECT entry."PortfolioId" AS "PortfolioId",
                   entry."TenantAccountId" AS "TenantAccountId",
                   entry."Id" AS "TenantLedgerEntryId",
                   date('now') AS "BusinessDate",
                   entry."EntryType" AS "EntryType",
                   entry."Currency" AS "Currency",
                   entry."EffectiveOn" AS "EffectiveOn",
                   entry."DueOn" AS "DueOn",
                   entry."Amount" AS "OriginalAmount",
                   0 AS "ReversedAmount",
                   COALESCE((
                     SELECT sum(allocation."Amount")
                     FROM "TenantLedgerAllocations" AS allocation
                     WHERE allocation."PortfolioId" = entry."PortfolioId"
                       AND allocation."TenantAccountId" = entry."TenantAccountId"
                       AND allocation."DebitEntryId" = entry."Id"
                   ), 0) AS "NetAllocations",
                   max(entry."Amount" - COALESCE((
                     SELECT sum(allocation."Amount")
                     FROM "TenantLedgerAllocations" AS allocation
                     WHERE allocation."PortfolioId" = entry."PortfolioId"
                       AND allocation."TenantAccountId" = entry."TenantAccountId"
                       AND allocation."DebitEntryId" = entry."Id"
                   ), 0), 0) AS "OpenAmount",
                   entry."DueOn" < date('now') AND entry."Amount" > COALESCE((
                     SELECT sum(allocation."Amount")
                     FROM "TenantLedgerAllocations" AS allocation
                     WHERE allocation."PortfolioId" = entry."PortfolioId"
                       AND allocation."TenantAccountId" = entry."TenantAccountId"
                       AND allocation."DebitEntryId" = entry."Id"
                   ), 0) AS "IsPastDue"
            FROM "TenantLedgerEntries" AS entry
            WHERE entry."Direction" = 'Debit'
            """);
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
            SeedReceipt(lease.Account, 1m, now.AddMinutes(-i));
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
    public async Task OverdueRentTool_ExcludesClosedLeaseRelationshipCharges()
    {
        var now = DateTime.UtcNow;
        var current = SeedLease(now);
        var stale = SeedLease(now);
        current.Agreement.AgreementNumber = "CURRENT-1";
        stale.Agreement.AgreementNumber = "STALE-1";
        stale.Agreement.TermEndOn = DateOnly.FromDateTime(now.AddDays(-1));
        stale.Management.PossessionReturnedAtUtc = now.AddDays(-1);
        stale.Management.AccountClosedAtUtc = now.AddDays(-1);
        SeedRentCharge(current, 1400m, now.AddDays(-5));
        SeedRentCharge(stale, 1400m, now.AddDays(-30));
        await _db.SaveChangesAsync();

        var answer = await AskToolAsync(
            toolName: "list_overdue_rent",
            argsJson: "{}",
            question: "Who is overdue?");

        using var doc = JsonDocument.Parse(answer);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeFalse();
        var overdue = doc.RootElement.GetProperty("overdue");
        overdue.GetArrayLength().Should().Be(1);
        overdue[0].GetProperty("leaseNumber").GetString().Should().Be("CURRENT-1");
    }

    [Fact]
    public async Task OverdueRentTool_PagesAndDetectsTruncationInSql()
    {
        var now = DateTime.UtcNow;
        var lease = SeedLease(now);
        for (var i = 0; i < 51; i++)
        {
            SeedRentCharge(lease, 1000m + i, now.Date.AddDays(-60 + i));
        }
        await _db.SaveChangesAsync();

        var answer = await AskToolAsync(
            toolName: "list_overdue_rent",
            argsJson: "{}",
            question: "Who is overdue?");

        using var doc = JsonDocument.Parse(answer);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(50);
        doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("overdue").GetArrayLength().Should().Be(50);
        _commands.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            (sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) ||
             sql.Contains("FETCH", StringComparison.OrdinalIgnoreCase)));
        _commands.Should().Contain(sql => sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FinancialSummaryTool_ReturnsPeriodLabelsAndUnmatchedBankContext()
    {
        var now = DateTime.UtcNow;
        var connection = new BankConnection
        {
            PortfolioId = PortfolioId,
            Provider = "Manual",
            InstitutionName = "Sample Bank",
            AccountName = "Operating checking",
            Status = "Active",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.BankConnections.Add(connection);
        _db.BankTransactions.Add(new BankTransaction
        {
            PortfolioId = PortfolioId,
            BankConnection = connection,
            ProviderTransactionId = "manual-deposit-1",
            PostedAt = now,
            Description = "Rent deposit",
            Amount = 1200m,
            IsoCurrencyCode = "USD",
            MatchStatus = "Unmatched",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await _db.SaveChangesAsync();

        var accounting = new StubAccountingService(
            summary: new AccountingSummaryResponse
            {
                PortfolioId = PortfolioId,
                Payments = new PaymentRollup
                {
                    Collected = 2325m,
                    Outstanding = 0m,
                    Overdue = 0m,
                    OverdueCount = 0,
                },
                TotalExpenses = 63.75m,
                ExpensesByCategory =
                [
                    new ScheduleECategoryTotal
                    {
                        Category = ScheduleECategory.Repairs,
                        CategoryName = "Repairs",
                        Total = 63.75m,
                        Count = 1,
                    },
                ],
            },
            snapshot: new MoneySnapshotResponse
            {
                PortfolioId = PortfolioId,
                PeriodLabel = "June 2026 (so far)",
                PeriodStart = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                PeriodEnd = new DateTime(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc),
                Collected = 1200m,
                Spent = 0m,
                Net = 1200m,
                PastDueAmount = 0m,
                PastDueCount = 0,
                CollectedLast30Days = 1200m,
                SpentLast30Days = 0m,
                NetLast30Days = 1200m,
            });

        var answer = await AskToolAsync(
            toolName: "get_financial_summary",
            argsJson: "{}",
            question: "How much did I collect?",
            accounting: accounting);

        using var doc = JsonDocument.Parse(answer);
        var root = doc.RootElement;

        root.GetProperty("currentLedger").GetProperty("scope").GetString()
            .Should().Contain("not limited to this month");
        root.GetProperty("currentLedger").GetProperty("collected").GetDecimal().Should().Be(2325m);
        root.GetProperty("monthToDate").GetProperty("periodLabel").GetString().Should().Be("June 2026 (so far)");
        root.GetProperty("monthToDate").GetProperty("collected").GetDecimal().Should().Be(1200m);
        root.GetProperty("last30Days").GetProperty("collected").GetDecimal().Should().Be(1200m);
        root.GetProperty("unmatchedBankDeposits").GetProperty("count").GetInt32().Should().Be(1);
        root.GetProperty("unmatchedBankDeposits").GetProperty("total").GetDecimal().Should().Be(1200m);
        root.GetProperty("unmatchedBankDeposits").GetProperty("note").GetString()
            .Should().Contain("mention separately");
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

    private async Task<string> AskToolAsync(
        string toolName,
        string argsJson,
        string question,
        IAccountingService? accounting = null)
    {
        _commands.Clear();
        var sut = new PortfolioQaService(
            _db,
            new ToolEchoLlmProvider(toolName, argsJson),
            accounting ?? new ThrowingAccountingService(),
            new NoopMessagePublisher(),
            new EmptyKnowledgeBaseService(),
            NullLogger<PortfolioQaService>.Instance,
            TimeProvider.System);

        var response = await sut.AskAsync(PortfolioId, question, history: null);
        response.ToolsUsed.Should().ContainSingle().Which.Should().Be(toolName);
        return response.Answer;
    }

    private LeaseFixture SeedLease(DateTime now)
    {
        var property = SeedProperty(now);
        var unit = new Unit
        {
            PortfolioId = PortfolioId,
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
        _db.AddRange(unit, tenant);
        _db.SaveChanges();

        var management = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = $"MGD-A-{Guid.NewGuid():N}",
            PossessionGivenAtUtc = now.AddMonths(-1),
            PossessionAgreementExceptionReason = "Projection fixture with imported terms pending execution",
            PossessionAgreementExceptionAuthorizedByUserId = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = 1,
            RowVersion = Guid.NewGuid(),
        };
        _db.LeaseManagements.Add(management);
        _db.SaveChanges();

        var account = new TenantAccount
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            AccountNumber = $"TA-{management.Id}",
            Currency = "USD",
            OpenedAtUtc = now,
            CreatedAtUtc = now,
            CreatedByUserId = 1,
        };
        var agreement = new LeaseAgreement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            VersionNumber = 1,
            AgreementNumber = $"MGD-A-{management.Id}",
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = DateOnly.FromDateTime(now.AddMonths(-1)),
            TermEndOn = DateOnly.FromDateTime(now.AddYears(1)),
            GoverningFromOn = DateOnly.FromDateTime(now.AddMonths(-1)),
            BaseRentAmount = 1400m,
            RentDueDay = 1,
            SecurityDepositObligation = 1400m,
            LateFeeAmount = 50m,
            GracePeriodDays = 5,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = 1,
        };
        var party = new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = DateOnly.FromDateTime(now.AddMonths(-1)),
            ChangeReason = "Canonical portfolio QA fixture",
            CreatedAtUtc = now,
            CreatedByUserId = 1,
        };
        _db.AddRange(account, agreement, party);
        _db.SaveChanges();
        return new LeaseFixture(management, account, agreement);
    }

    private void SeedReceipt(TenantAccount account, decimal amount, DateTime effectiveAt)
    {
        _db.TenantLedgerEntries.Add(new TenantLedgerEntry
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            TenantAccountId = account.Id,
            EntryType = TenantLedgerEntryType.PaymentReceipt,
            Direction = TenantLedgerDirection.Credit,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(effectiveAt),
            PostedAtUtc = effectiveAt,
            Description = "Rent receipt",
            BusinessKey = $"qa-receipt:{Guid.NewGuid():N}",
            CreatedByUserId = 1,
        });
    }

    private void SeedRentCharge(LeaseFixture lease, decimal amount, DateTime dueAt)
    {
        _db.TenantLedgerEntries.Add(new TenantLedgerEntry
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            TenantAccountId = lease.Account.Id,
            EntryType = TenantLedgerEntryType.RentCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(dueAt),
            DueOn = DateOnly.FromDateTime(dueAt),
            PostedAtUtc = dueAt,
            Description = "Rent charge",
            BusinessKey = $"qa-charge:{Guid.NewGuid():N}",
            LeaseAgreementId = lease.Agreement.Id,
            CreatedByUserId = 1,
        });
    }

    private sealed record LeaseFixture(
        LeaseManagement Management,
        TenantAccount Account,
        LeaseAgreement Agreement);

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

    private sealed class StubAccountingService : IAccountingService
    {
        private readonly AccountingSummaryResponse _summary;
        private readonly MoneySnapshotResponse _snapshot;

        public StubAccountingService(AccountingSummaryResponse summary, MoneySnapshotResponse snapshot)
        {
            _summary = summary;
            _snapshot = snapshot;
        }

        public Task<AccountingSummaryResponse> GetSummaryAsync(int portfolioId, CancellationToken ct = default) =>
            Task.FromResult(_summary);

        public Task<MoneySnapshotResponse> GetSnapshotAsync(int portfolioId, CancellationToken ct = default) =>
            Task.FromResult(_snapshot);

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
        public Task PublishAsync<TPayload>(int portfolioId, string messageType, string idempotencyKey, TPayload payload, CancellationToken ct = default) =>
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

    private sealed class PortfolioQaTestDbContext : RentalCommand.TestCommon.SqliteCompatibleRentalCommandDbContext
    {
        public PortfolioQaTestDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }
    }
}
