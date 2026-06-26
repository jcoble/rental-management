using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Auditing;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public class UnitServiceListTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly List<string> _commands = [];
    private readonly SqliteTestContext _ctx;
    private readonly UnitService _sut;

    public UnitServiceListTests()
    {
        _ctx = new SqliteTestContext([new RecordingCommandInterceptor(_commands)]);
        _sut = new UnitService(_ctx.Db, Mock.Of<IDataUpdateService>());
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task ListWithHealthPageAsync_ReturnsSqlCountAndRequestedWindow()
    {
        SeedUnit("A", "Cedar Point Flats", openWorkOrders: 1);
        SeedUnit("B", "Cedar Point Flats", openWorkOrders: 3);
        SeedUnit("C", "Harbor View Apartments", openWorkOrders: 0);
        SeedUnit("D", "Harbor View Apartments", openWorkOrders: 2);

        _commands.Clear();
        var result = await _sut.ListWithHealthPageAsync(PortfolioId, new UnitHealthListQuery
        {
            Sort = "-openWorkOrderCount",
            Skip = 1,
            Take = 2,
        });

        result.TotalCount.Should().Be(4);
        result.Skip.Should().Be(1);
        result.Take.Should().Be(2);
        result.Items.Select(u => u.UnitNumber).Should().Equal("D", "A");
        result.Items.Select(u => u.OpenWorkOrderCount).Should().Equal(2, 1);

        _commands.Should().Contain(sql =>
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("FROM \"Units\"", StringComparison.OrdinalIgnoreCase));
        _commands.Should().Contain(sql =>
            sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("WorkOrders", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListWithHealthPageAsync_NoticeGivenOccupiedUnitDoesNotFallBackToVacant()
    {
        var now = DateTime.UtcNow;
        var (property, unit) = SeedUnitShell("101", "Westview Four-Plex", UnitStatus.Occupied, now);
        var tenant = SeedTenant(now);
        _ctx.Db.Leases.Add(new Lease
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            Tenant = tenant,
            LeaseNumber = "L-NOTICE",
            Status = LeaseStatus.NoticeGiven,
            StartDate = now.AddMonths(-10),
            EndDate = now.AddDays(30),
            MoveOutDate = now.AddDays(30),
            MonthlyRent = 925m,
            SecurityDeposit = 925m,
            CreatedAt = now,
            UpdatedAt = now,
        });
        _ctx.Db.SaveChanges();

        var result = await _sut.ListWithHealthPageAsync(PortfolioId, new UnitHealthListQuery());

        var row = result.Items.Should().ContainSingle(u => u.Id == unit.Id).Subject;
        row.Status.Should().Be(UnitStatus.Occupied.ToString());
        row.SimpleStage.Should().Be("Move-Out");
    }

    [Fact]
    public async Task ListWithHealthPageAsync_DocsCountMatchesUnitDashboardRollupDefinition()
    {
        var now = DateTime.UtcNow;
        var (property, unit) = SeedUnitShell("2A", "Maple Heights", UnitStatus.Occupied, now);
        var tenant = SeedTenant(now);
        var lease = new Lease
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            Tenant = tenant,
            LeaseNumber = "L-DOCS",
            Status = LeaseStatus.Active,
            StartDate = now.AddMonths(-2),
            EndDate = now.AddMonths(10),
            MonthlyRent = 1200m,
            SecurityDeposit = 1200m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var payment = new Payment
        {
            PortfolioId = PortfolioId,
            Lease = lease,
            PaymentType = PaymentType.Rent,
            Status = PaymentStatus.Paid,
            Amount = 1200m,
            DueDate = now.AddDays(-5),
            PaidDate = now.AddDays(-5),
            CreatedAt = now,
            UpdatedAt = now,
        };
        var workOrder = new WorkOrder
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            Title = "Repair",
            Description = "Open repair",
            Status = WorkOrderStatus.New,
            Priority = WorkOrderPriority.Normal,
            RequestedAt = now,
            UpdatedAt = now,
        };
        var directExpense = new Expense
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            Description = "Unit receipt",
            Amount = 35m,
            IncurredAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var workOrderExpense = new Expense
        {
            PortfolioId = PortfolioId,
            Property = property,
            WorkOrder = workOrder,
            Description = "Repair receipt",
            Amount = 65m,
            IncurredAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var inspection = new Inspection
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            Type = InspectionType.MoveIn,
            Status = InspectionStatus.Completed,
            ScheduledFor = now,
            CompletedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.AddRange(lease, payment, workOrder, directExpense, workOrderExpense, inspection);
        _ctx.Db.SaveChanges();

        _ctx.Db.StoredFiles.AddRange(
            StoredFile("Unit", unit.Id, "unit-photo.jpg", now),
            StoredFile("Lease", lease.Id, "lease.pdf", now),
            StoredFile("Payment", payment.Id, "rent-check.jpg", now),
            StoredFile("Expense", directExpense.Id, "unit-receipt.jpg", now),
            StoredFile("Expense", workOrderExpense.Id, "repair-receipt.jpg", now),
            StoredFile("WorkOrder", workOrder.Id, "repair-photo.jpg", now),
            StoredFile("Inspection", inspection.Id, "inspection.pdf", now),
            StoredFile("Tenant", tenant.Id, "tenant-only.pdf", now));
        _ctx.Db.SaveChanges();

        _commands.Clear();
        var list = await _sut.ListWithHealthPageAsync(PortfolioId, new UnitHealthListQuery());
        var listSql = _commands.ToList();
        var dashboard = await new UnitDashboardService(_ctx.Db, new AuditDescriber(), new AuditDiffBuilder())
            .GetDashboardAsync(PortfolioId, unit.Id, CancellationToken.None);

        var row = list.Items.Should().ContainSingle().Subject;
        dashboard.Should().NotBeNull();
        row.DocsNeedingReviewCount.Should().Be(7);
        row.DocsNeedingReviewCount.Should().Be(dashboard!.Header.DocsNeedingReviewCount);
        listSql.Should().Contain(sql =>
            sql.Contains("StoredFiles", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("Leases", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("Payments", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("WorkOrders", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("Inspections", StringComparison.OrdinalIgnoreCase));
    }

    private void SeedUnit(string unitNumber, string propertyName, int openWorkOrders)
    {
        var now = DateTime.UtcNow;
        var (property, unit) = SeedUnitShell(unitNumber, propertyName, UnitStatus.Vacant, now);

        for (var i = 0; i < openWorkOrders; i++)
        {
            _ctx.Db.WorkOrders.Add(new WorkOrder
            {
                PortfolioId = PortfolioId,
                PropertyId = property.Id,
                UnitId = unit.Id,
                Title = $"{unitNumber} repair {i}",
                Description = "Open repair",
                Status = WorkOrderStatus.New,
                Priority = WorkOrderPriority.Normal,
                RequestedAt = now,
                UpdatedAt = now,
            });
        }

        _ctx.Db.WorkOrders.Add(new WorkOrder
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            Title = $"{unitNumber} closed repair",
            Description = "Closed repair",
            Status = WorkOrderStatus.Completed,
            Priority = WorkOrderPriority.Normal,
            RequestedAt = now,
            UpdatedAt = now,
        });
        _ctx.Db.SaveChanges();
    }

    private (Property Property, Unit Unit) SeedUnitShell(
        string unitNumber,
        string propertyName,
        UnitStatus status,
        DateTime now)
    {
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = propertyName,
            AddressLine1 = "100 Test Street",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = unitNumber,
            Status = status,
            MarketRent = 1250m,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _ctx.Db.Units.Add(unit);
        _ctx.Db.SaveChanges();

        return (property, unit);
    }

    private Tenant SeedTenant(DateTime now)
    {
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Jordan",
            LastName = "Smith",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Tenants.Add(tenant);
        _ctx.Db.SaveChanges();
        return tenant;
    }

    private static StoredFile StoredFile(string entityType, int entityId, string fileName, DateTime now) => new()
    {
        PortfolioId = PortfolioId,
        EntityType = entityType,
        EntityId = entityId,
        FileName = fileName,
        FilePath = $"test/{fileName}",
        ContentType = fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            ? "application/pdf"
            : "image/jpeg",
        FileSize = 1024,
        UploadedAt = now,
    };

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
