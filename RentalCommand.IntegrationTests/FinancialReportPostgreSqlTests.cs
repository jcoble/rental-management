using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data.Reporting;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FinancialReportPostgreSqlCollection : ICollectionFixture<MigratedPostgreSqlFixture>
{
    public const string Name = "Financial reports PostgreSQL";
}

[Collection(FinancialReportPostgreSqlCollection.Name)]
public sealed class FinancialReportPostgreSqlTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private static readonly DateTime Now = new(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);

    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly List<CapturedCommand> _commands = [];
    private MigratedPostgreSqlTestContext _context = null!;

    public FinancialReportPostgreSqlTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync() =>
        _context = await _fixture.CreateContextAsync([new QueryRecorder(_commands)]);

    public async Task DisposeAsync()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
    }

    [Fact]
    public async Task CapturedPlans_AreWrittenForVerifier()
    {
        var property = await SeedPropertyAsync("Maple");
        var lease = await SeedLeaseAsync(property);
        var scope = await SeedScopeAsync(
            "plan-capture-financial-reports@example.test",
            RoleProfileKeys.WorkspaceAdministrator,
            MembershipRoleAssignmentScopeKind.AllProperties);
        await SeedPaymentAsync(lease, 100m, new DateTime(2026, 4, 5, 0, 0, 0, DateTimeKind.Utc));

        var service = NewService();
        _commands.Clear();

        await service.GetCashFlowAsync(scope, new ReportRangeQuery
        {
            From = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc),
        });
        var selects = CaptureCommandSql(_commands);
        await WriteSqlArtifactAsync("captured-plans", selects.Select(command => command.Sql));
        await WriteExplainArtifactAsync("captured-plans", await ExplainAsync(selects));

        selects.Should().NotBeEmpty("the verifier artifacts are written from captured PostgreSQL commands");
    }

    private ReportsService NewService() =>
        new(
            _context.Db,
            new OwnerStatementService(_context.Db, TimeProvider.System),
            new ScheduleEService(_context.Db),
            new PropertyDispositionService(_context.Db, TimeProvider.System),
            TimeProvider.System);

    private async Task<WorkspaceReadScope> SeedScopeAsync(
        string email,
        string roleKey,
        MembershipRoleAssignmentScopeKind scopeKind,
        IReadOnlyCollection<int>? selectedPropertyIds = null)
    {
        var authNow = DateTime.UtcNow;
        var user = new ApplicationUser
        {
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = email,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = authNow,
        };
        var accessContext = new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = PortfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = authNow,
            UpdatedAtUtc = authNow,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = PortfolioId,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = authNow.AddMinutes(-1),
            CreatedAtUtc = authNow,
            UpdatedAtUtc = authNow,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = PortfolioId,
            RoleProfileId = AccessCatalog.Roles.Single(role => role.Key == roleKey).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = scopeKind,
            EffectiveFromUtc = authNow.AddMinutes(-1),
            CreatedAtUtc = authNow,
            UpdatedAtUtc = authNow,
        };
        foreach (var propertyId in selectedPropertyIds ?? [])
        {
            assignment.SelectedProperties.Add(new MembershipRoleAssignmentProperty
            {
                PortfolioId = PortfolioId,
                PropertyId = propertyId,
            });
        }
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            User = user,
            ActiveAccessContext = accessContext,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = authNow,
            LastSeenAtUtc = authNow,
            ExpiresAtUtc = authNow.AddHours(1),
        };

        _context.Db.AddRange(assignment, session);
        await _context.Db.SaveChangesAsync();

        return new WorkspaceReadScope(
            PortfolioId, user.Id, session.Id, accessContext.Id, accessContext.AccessRevision);
    }

    private async Task<Property> SeedPropertyAsync(string name)
    {
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = name,
            AddressLine1 = $"{name} Main",
            City = "Columbus",
            State = "OH",
            PostalCode = "43219",
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        _context.Db.Properties.Add(property);
        await _context.Db.SaveChangesAsync();
        return property;
    }

    private async Task<Unit> SeedUnitAsync(Property property, string unitNumber)
    {
        var unit = new Unit
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitNumber = unitNumber,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        _context.Db.Units.Add(unit);
        await _context.Db.SaveChangesAsync();
        return unit;
    }

    private async Task<Expense> SeedExpenseAsync(int? propertyId, decimal amount, DateTime paidAt)
    {
        var expense = new Expense
        {
            PortfolioId = PortfolioId,
            OperationalScope = propertyId is null
                ? ExpenseOperationalScope.Portfolio
                : ExpenseOperationalScope.Property,
            PropertyId = propertyId,
            Category = ScheduleECategory.Repairs,
            Description = propertyId is null ? "Portfolio expense" : "Property expense",
            Status = ExpenseStatus.Paid,
            Amount = amount,
            IncurredAt = paidAt,
            PaidAt = paidAt,
            CreatedAt = paidAt,
            UpdatedAt = paidAt,
        };
        _context.Db.Expenses.Add(expense);
        await _context.Db.SaveChangesAsync();
        return expense;
    }

    private async Task SeedOwnerAllocatedExpenseAsync(int propertyId, decimal amount, DateTime paidAt)
    {
        var owner = new OwnerEntity
        {
            PortfolioId = PortfolioId,
            Name = "Owner LLC",
            OwnerEntityType = OwnerEntityType.LLC,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        _context.Db.OwnerEntities.Add(owner);
        await _context.Db.SaveChangesAsync();
        var expense = await SeedExpenseAsync(propertyId, amount, paidAt);
        _context.Db.ExpenseAllocations.Add(new ExpenseAllocation
        {
            PortfolioId = PortfolioId,
            ExpenseId = expense.Id,
            TargetKind = ExpenseAllocationTargetKind.OwnerEntity,
            OwnerEntityId = owner.Id,
            Amount = amount,
            CreatedAt = paidAt,
        });
        await _context.Db.SaveChangesAsync();
    }

    private async Task SeedUnitAllocatedExpenseAsync(Unit unit, decimal amount, DateTime paidAt)
    {
        var expense = await SeedExpenseAsync(null, amount, paidAt);
        expense.OperationalScope = ExpenseOperationalScope.Unit;
        expense.PropertyId = unit.PropertyId;
        expense.UnitId = unit.Id;
        _context.Db.ExpenseAllocations.Add(new ExpenseAllocation
        {
            PortfolioId = PortfolioId,
            ExpenseId = expense.Id,
            TargetKind = ExpenseAllocationTargetKind.Unit,
            UnitId = unit.Id,
            Amount = amount,
            CreatedAt = paidAt,
        });
        await _context.Db.SaveChangesAsync();
    }

    private async Task SeedWorkOrderExpenseAsync(Unit unit, decimal amount, DateTime paidAt)
    {
        var workOrder = new WorkOrder
        {
            PortfolioId = PortfolioId,
            PropertyId = unit.PropertyId,
            UnitId = unit.Id,
            Title = "Financial report work order",
            Description = "Financial report work order",
            Category = "General",
            RequestedAt = paidAt,
            UpdatedAt = paidAt,
        };
        _context.Db.WorkOrders.Add(workOrder);
        await _context.Db.SaveChangesAsync();
        var expense = await SeedExpenseAsync(unit.PropertyId, amount, paidAt);
        expense.OperationalScope = ExpenseOperationalScope.WorkOrder;
        expense.UnitId = unit.Id;
        expense.WorkOrderId = workOrder.Id;
        await _context.Db.SaveChangesAsync();
    }

    private async Task<LeaseAgreement> SeedLeaseAsync(Property property)
    {
        var unit = await SeedUnitAsync(property, "1");
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Ann",
            LastName = "Acre",
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        _context.Db.Tenants.Add(tenant);
        await _context.Db.SaveChangesAsync();

        var management = new LeaseManagement
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = $"LM-{property.Id}-{Guid.NewGuid():N}"[..16],
            PlannedPossessionAtUtc = Now.AddMonths(-1),
            PossessionGivenAtUtc = Now.AddMonths(-1),
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
            CreatedByUserId = 1,
            RowVersion = Guid.NewGuid(),
        };
        _context.Db.LeaseManagements.Add(management);
        await _context.Db.SaveChangesAsync();

        var account = new TenantAccount
        {
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            AccountNumber = $"TA-{management.Id}",
            Currency = "USD",
            OpenedAtUtc = Now.AddMonths(-1),
            CreatedAtUtc = Now,
            CreatedByUserId = 1,
        };
        var party = new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = DateOnly.FromDateTime(Now.AddMonths(-1)),
            ChangeReason = "Financial report test",
            CreatedAtUtc = Now,
            CreatedByUserId = 1,
        };
        var agreement = new LeaseAgreement
        {
            PortfolioId = PortfolioId,
            LeaseManagementId = management.Id,
            VersionNumber = 1,
            AgreementNumber = $"A-{property.Id}-{Guid.NewGuid():N}"[..16],
            ChangeType = LeaseAgreementChangeType.Initial,
            TermType = LeaseAgreementTermType.FixedTerm,
            TermStartOn = DateOnly.FromDateTime(Now.AddMonths(-1)),
            TermEndOn = DateOnly.FromDateTime(Now.AddYears(1)),
            GoverningFromOn = DateOnly.FromDateTime(Now.AddMonths(-1)),
            BaseRentAmount = 1000m,
            RentDueDay = 1,
            SecurityDepositObligation = 1000m,
            LateFeeAmount = 50m,
            GracePeriodDays = 5,
            Currency = "USD",
            TermsSchemaVersion = 1,
            TermsPayload = "{}",
            DocumentSourceVersion = LegalDocumentSourceVersionTestData.BuiltIn(
                PortfolioId, 1, Now),
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
            CreatedByUserId = 1,
            LeaseManagement = management,
        };
        management.TenantAccount = account;
        _context.Db.AddRange(account, party, agreement);
        await _context.Db.SaveChangesAsync();
        return agreement;
    }

    private async Task SeedPaymentAsync(LeaseAgreement lease, decimal amount, DateTime paidAt)
    {
        var account = lease.LeaseManagement!.TenantAccount!;
        var charge = new TenantLedgerEntry
        {
            PortfolioId = PortfolioId,
            TenantAccountId = account.Id,
            EntryType = TenantLedgerEntryType.RentCharge,
            Direction = TenantLedgerDirection.Debit,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(paidAt),
            DueOn = DateOnly.FromDateTime(paidAt),
            PostedAtUtc = paidAt,
            Description = "Rent charge",
            BusinessKey = $"charge:{Guid.NewGuid():N}",
            LeaseAgreementId = lease.Id,
            CreatedByUserId = 1,
        };
        _context.Db.TenantLedgerEntries.Add(charge);
        await _context.Db.SaveChangesAsync();
        var receipt = new TenantLedgerEntry
        {
            PortfolioId = PortfolioId,
            TenantAccountId = account.Id,
            EntryType = TenantLedgerEntryType.PaymentReceipt,
            Direction = TenantLedgerDirection.Credit,
            Amount = amount,
            Currency = "USD",
            EffectiveOn = DateOnly.FromDateTime(paidAt),
            PostedAtUtc = paidAt,
            Description = "Rent receipt",
            BusinessKey = $"receipt:{Guid.NewGuid():N}",
            CreatedByUserId = 1,
        };
        _context.Db.TenantLedgerEntries.Add(receipt);
        await _context.Db.SaveChangesAsync();
        _context.Db.TenantLedgerAllocations.Add(new TenantLedgerAllocation
        {
            PortfolioId = PortfolioId,
            TenantAccountId = account.Id,
            DebitEntryId = charge.Id,
            CreditEntryId = receipt.Id,
            Amount = amount,
            AllocatedAtUtc = paidAt,
            BusinessKey = $"allocation:{Guid.NewGuid():N}",
            CreatedByUserId = 1,
        });
        await _context.Db.SaveChangesAsync();
    }

    private async Task SeedSecurityDepositAsync(
        LeaseAgreement lease,
        DateTime receiptAt,
        DateTime transferredAt,
        string suffix = "transfer",
        decimal received = 1000m,
        decimal transferred = 400m)
    {
        var depositAccount = new SecurityDepositAccount
        {
            PortfolioId = PortfolioId,
            TenantAccountId = lease.LeaseManagement!.TenantAccount!.Id,
            OriginatingAgreementId = lease.Id,
            Currency = "USD",
            CreatedAtUtc = receiptAt,
            CreatedByUserId = 1,
        };
        _context.Db.SecurityDepositAccounts.Add(depositAccount);
        await _context.Db.SaveChangesAsync();
        _context.Db.SecurityDepositEntries.AddRange(
            new SecurityDepositEntry
            {
                PortfolioId = PortfolioId,
                SecurityDepositAccountId = depositAccount.Id,
                EntryType = SecurityDepositEntryType.Receipt,
                Direction = SecurityDepositDirection.Increase,
                Amount = received,
                Currency = "USD",
                EffectiveOn = DateOnly.FromDateTime(receiptAt),
                PostedAtUtc = receiptAt,
                BusinessKey = $"deposit:receipt:{suffix}",
                Description = "Deposit received",
                LeaseAgreementId = lease.Id,
                CreatedByUserId = 1,
            },
            new SecurityDepositEntry
            {
                PortfolioId = PortfolioId,
                SecurityDepositAccountId = depositAccount.Id,
                EntryType = SecurityDepositEntryType.TransferOut,
                Direction = SecurityDepositDirection.Decrease,
                Amount = transferred,
                Currency = "USD",
                EffectiveOn = DateOnly.FromDateTime(transferredAt),
                PostedAtUtc = transferredAt,
                BusinessKey = $"deposit:transfer-out:{suffix}",
                Description = "Deposit transferred out",
                TransferPublicId = Guid.NewGuid(),
                LeaseAgreementId = lease.Id,
                CreatedByUserId = 1,
            });
        await _context.Db.SaveChangesAsync();
    }

    private async Task WriteArtifactsAsync(string name, IReadOnlyCollection<CapturedCommand> commands)
    {
        var selects = CaptureCommandSql(commands);
        await WriteSqlArtifactAsync(name, selects.Select(command => command.Sql));
        await WriteExplainArtifactAsync(name, await ExplainAsync(selects));
    }

    private static IReadOnlyList<CapturedCommand> CaptureCommandSql(IReadOnlyCollection<CapturedCommand> commands) =>
        commands
            .Where(command => command.Sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private async Task<IReadOnlyList<string>> ExplainAsync(IEnumerable<CapturedCommand> captured)
    {
        var explain = new List<string>();
        foreach (var command in captured)
        {
            explain.Add(await ExplainAsync(command));
        }

        return explain;
    }

    private async Task<string> ExplainAsync(CapturedCommand captured)
    {
        await using var connection = new NpgsqlConnection(_context.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT) {captured.Sql}",
            connection);
        foreach (var parameter in captured.Parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return string.Join(Environment.NewLine, rows);
    }

    private static Task WriteSqlArtifactAsync(string section, IEnumerable<string> entries) =>
        WriteArtifactAsync("FOUNDATION_REPORT_SQL_OUTPUT", "L05-reports-sql.txt", section, entries);

    private static Task WriteExplainArtifactAsync(string section, IEnumerable<string> entries) =>
        WriteArtifactAsync("FOUNDATION_REPORT_EXPLAIN_OUTPUT", "L05-reports-explain.txt", section, entries);

    private static async Task WriteArtifactAsync(
        string environmentVariable,
        string fallbackFileName,
        string section,
        IEnumerable<string> entries)
    {
        var path = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            path = ResolveArtifactPath(
                Path.Combine("Docs/Testing/Results/2026-07-16-foundation-parallel", fallbackFileName));
        }
        else
        {
            path = ResolveArtifactPath(path);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.AppendAllTextAsync(path, $"""

            ## {section}

            {string.Join($"{Environment.NewLine}{Environment.NewLine}---{Environment.NewLine}{Environment.NewLine}", entries)}

            """);
    }

    private static string ResolveArtifactPath(string path)
    {
        if (Path.IsPathFullyQualified(path)) return path;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !Directory.Exists(Path.Combine(directory.FullName, "RentalCommand.Data")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found."),
            path);
    }

    private static bool ContainsSqlOrParameter(CapturedCommand command, string value) =>
        command.Sql.Contains(value, StringComparison.Ordinal) ||
        command.Parameters.Any(parameter =>
            string.Equals(Convert.ToString(parameter.Value), value, StringComparison.Ordinal));

    private sealed record CapturedParameter(string Name, object? Value);

    private sealed record CapturedCommand(string Sql, IReadOnlyList<CapturedParameter> Parameters);

    private sealed class QueryRecorder(List<CapturedCommand> commands) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Capture(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Capture(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result)
        {
            Capture(command);
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Capture(command);
            return ValueTask.FromResult(result);
        }

        private void Capture(DbCommand command)
        {
            commands.Add(new CapturedCommand(
                command.CommandText,
                command.Parameters
                    .Cast<DbParameter>()
                    .Select(parameter => new CapturedParameter(parameter.ParameterName, parameter.Value))
                    .ToArray()));
        }
    }
}
