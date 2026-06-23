using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services;
using RentalCommand.Api.Services.Auditing;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data.Auditing;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

public sealed class LeaseServiceAuditTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteTestContext _ctx = new();
    private readonly LeaseService _sut;

    public LeaseServiceAuditTests()
    {
        _sut = new LeaseService(
            _ctx.Db,
            new NoopDataUpdateService(),
            Mock.Of<IFileStorage>(),
            Mock.Of<ILeaseAgreementPdfGenerator>(),
            new AuditTrailService(_ctx.Db, new AuditScope()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LeaseService>.Instance);
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task UpdateAsync_RecordsNotesChangeInAuditHistoryDiff()
    {
        var lease = SeedLease(notes: "Imported from scanned lease document.");

        await _sut.UpdateAsync(PortfolioId, lease.Id, new UpdateLeaseRequest
        {
            Notes = "Imported from scanned lease document. Pass 37 lease detail edit proof.",
        });

        var row = await _ctx.Db.AuditLogs
            .AsNoTracking()
            .Where(a => a.EntityType == "Lease"
                && a.EntityId == lease.Id
                && a.Operation == AuditLogOperation.Updated)
            .SingleAsync();

        var changes = new AuditDiffBuilder().Build(row);

        changes.Should().ContainSingle(c => c.Field == "Notes"
            && c.OldValue == "Imported from scanned lease document."
            && c.NewValue == "Imported from scanned lease document. Pass 37 lease detail edit proof.");
    }

    [Fact]
    public async Task UpdateAsync_ClearsMoveOutDateWhenNoticeIsCancelled()
    {
        var moveOutDate = new DateTime(2026, 7, 31, 0, 0, 0, DateTimeKind.Utc);
        var lease = SeedLease(
            notes: "Notice was given by mistake.",
            status: LeaseStatus.NoticeGiven,
            moveOutDate: moveOutDate);

        var result = await _sut.UpdateAsync(PortfolioId, lease.Id, new UpdateLeaseRequest
        {
            Status = LeaseStatus.Active,
        });

        result.Should().NotBeNull();
        result!.Status.Should().Be(LeaseStatus.Active);
        result.MoveOutDate.Should().BeNull();

        var reloaded = await _ctx.Db.Leases.AsNoTracking().SingleAsync(l => l.Id == lease.Id);
        reloaded.Status.Should().Be(LeaseStatus.Active);
        reloaded.MoveOutDate.Should().BeNull();

        var row = await _ctx.Db.AuditLogs
            .AsNoTracking()
            .Where(a => a.EntityType == "Lease"
                && a.EntityId == lease.Id
                && a.Operation == AuditLogOperation.Updated)
            .SingleAsync();

        row.ChangeReason.Should().Contain("move-out date 2026-07-31→none");
    }

    private Lease SeedLease(
        string notes,
        LeaseStatus status = LeaseStatus.Active,
        DateTime? moveOutDate = null)
    {
        var now = new DateTime(2026, 6, 23, 12, 0, 0, DateTimeKind.Utc);
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Cedar Point Flats",
            AddressLine1 = "742 Evergreen St",
            City = "Columbus",
            State = "OH",
            PostalCode = "43200",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var unit = new Unit
        {
            Property = property,
            UnitNumber = "1A",
            MarketRent = 1125m,
            Status = UnitStatus.Occupied,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Avery",
            LastName = "Ellis",
            Email = "avery.ellis@example.local",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var lease = new Lease
        {
            PortfolioId = PortfolioId,
            Property = property,
            Unit = unit,
            Tenant = tenant,
            LeaseNumber = "QA-2026-001-1A",
            Status = status,
            StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            MoveOutDate = moveOutDate,
            MonthlyRent = 1125m,
            SecurityDeposit = 1125m,
            RentDueDay = 1,
            Notes = notes,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _ctx.Db.Leases.Add(lease);
        _ctx.Db.SaveChanges();
        return lease;
    }

    private sealed class NoopDataUpdateService : IDataUpdateService
    {
        public Task BroadcastEntityUpdateAsync(int portfolioId, string entityType, int entityId, object data, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task BroadcastEntityDeleteAsync(int portfolioId, string entityType, int entityId, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
