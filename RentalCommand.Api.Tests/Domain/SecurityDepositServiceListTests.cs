using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public class SecurityDepositServiceListTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly List<string> _commands = [];
    private readonly SqliteTestContext _ctx;
    private readonly SecurityDepositService _sut;

    public SecurityDepositServiceListTests()
    {
        _ctx = new SqliteTestContext([new RecordingCommandInterceptor(_commands)]);
        _sut = new SecurityDepositService(
            _ctx.Db,
            Mock.Of<IFileStorage>(),
            Mock.Of<IMoveOutStatementPdfGenerator>(),
            new NoopAuditTrailService(),
            Mock.Of<ICurrentActor>(),
            Mock.Of<ILogger<SecurityDepositService>>());
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task ListPageAsync_ReturnsSqlCountAndRequestedWindow()
    {
        SeedDeposit("A-100", 900m);
        SeedDeposit("B-200", 1_100m);
        SeedDeposit("C-300", 1_300m);
        SeedDeposit("D-400", 1_500m);

        _commands.Clear();
        var result = await _sut.ListPageAsync(PortfolioId, leaseId: null, new ListQuery
        {
            Sort = "amount",
            Skip = 1,
            Take = 2,
        });

        result.TotalCount.Should().Be(4);
        result.Skip.Should().Be(1);
        result.Take.Should().Be(2);
        result.Items.Select(d => d.Amount).Should().Equal(1_100m, 1_300m);

        _commands.Should().Contain(sql =>
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("FROM \"SecurityDepositHoldings\"", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReturnAsync_WithDeductionsAndFullNetRefund_MarksReturnedAndClosesDeductionLifecycle()
    {
        var deposit = SeedDeposit("R-100", 1_325m);

        var withDeduction = await _sut.AddDeductionAsync(PortfolioId, deposit.Id, new AddDeductionRequest
        {
            Reason = "Move-out cleaning",
            Amount = 150m,
            Notes = "Documented from photo evidence.",
        });
        var returned = await _sut.ReturnAsync(PortfolioId, deposit.Id, new ReturnDepositRequest
        {
            Notes = "Net refund returned by ACH.",
        });
        var deductionAfterReturn = await _sut.AddDeductionAsync(PortfolioId, deposit.Id, new AddDeductionRequest
        {
            Reason = "Late damage",
            Amount = 25m,
        });

        withDeduction.Should().NotBeNull();
        returned.Should().NotBeNull();
        returned!.Status.Should().Be(SecurityDepositStatus.Returned.ToString());
        returned.ReturnedAmount.Should().Be(1_175m);
        returned.TotalDeductions.Should().Be(150m);
        deductionAfterReturn.Should().BeNull("a fully returned deposit must be closed to new deductions");

        var fromDb = await _ctx.Db.SecurityDepositHoldings.AsNoTracking().SingleAsync(h => h.Id == deposit.Id);
        fromDb.Status.Should().Be(SecurityDepositStatus.Returned);
        fromDb.ReturnedAmount.Should().Be(1_175m);
    }

    private SecurityDepositHolding SeedDeposit(string leaseNumber, decimal amount)
    {
        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = leaseNumber,
            LastName = "Tenant",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = $"Deposit Property {leaseNumber}",
            AddressLine1 = "1 Test Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Tenants.Add(tenant);
        _ctx.Db.Properties.Add(property);
        _ctx.Db.SaveChanges();

        var unit = new Unit
        {
            PropertyId = property.Id,
            UnitNumber = leaseNumber,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Units.Add(unit);
        _ctx.Db.SaveChanges();

        var lease = new Lease
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            TenantId = tenant.Id,
            LeaseNumber = leaseNumber,
            StartDate = now.AddMonths(-1),
            EndDate = now.AddYears(1),
            MonthlyRent = amount,
            SecurityDeposit = amount,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Leases.Add(lease);
        _ctx.Db.SaveChanges();

        var deposit = new SecurityDepositHolding
        {
            PortfolioId = PortfolioId,
            LeaseId = lease.Id,
            Amount = amount,
            Status = SecurityDepositStatus.Held,
            HeldAt = now,
            DeductionsJson = "[]",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.SecurityDepositHoldings.Add(deposit);
        _ctx.Db.SaveChanges();
        return deposit;
    }

    private sealed class NoopAuditTrailService : IAuditTrailService
    {
        public Task LogAsync(
            int portfolioId,
            string entityType,
            int entityId,
            AuditLogOperation operation,
            int? userId = null,
            string? actorLabel = null,
            string? oldValues = null,
            string? newValues = null,
            string? changeReason = null,
            string? ipAddress = null,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingCommandInterceptor(List<string> commands) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            commands.Add(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
