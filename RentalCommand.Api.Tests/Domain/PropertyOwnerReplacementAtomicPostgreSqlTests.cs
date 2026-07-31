using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class PropertyOwnerReplacementAtomicPostgreSqlTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private static readonly DateTime SeededAtUtc = new(2027, 1, 1, 5, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime BusinessNowUtc = new(2027, 1, 25, 5, 0, 0, DateTimeKind.Utc);

    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _context = null!;
    private ServiceProvider _services = null!;
    private WorkspaceReadScope _scope;

    public PropertyOwnerReplacementAtomicPostgreSqlTests(MigratedPostgreSqlFixture fixture) =>
        _fixture = fixture;

    public async Task InitializeAsync()
    {
        _context = await _fixture.CreateContextAsync();
        _scope = _context.Db.SeedAdministratorScope(PortfolioId, nameof(PropertyOwnerReplacementAtomicPostgreSqlTests));
        _services = BuildServices(_context.ConnectionString, new FixedTimeProvider(new DateTimeOffset(BusinessNowUtc)));
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task UpdateAsync_ReplacesOwnerWithInjectedBusinessTimeAndAuditsOwnershipLifecycle()
    {
        var seeded = await SeedPropertyWithOwnersAsync();
        var service = CreateService();

        var updated = await service.UpdateAsync(
            _scope,
            seeded.PropertyId,
            new UpdatePropertyRequest
            {
                Name = "Property 49 edited",
                Ownerships =
                [
                    new PropertyOwnershipRequest
                    {
                        OwnerEntityId = seeded.ReplacementOwnerId,
                        OwnershipSharePercent = 100m,
                    },
                ],
            },
            "property-owner-replace-business-time");

        updated.Should().NotBeNull();
        updated!.UpdatedAt.Should().Be(BusinessNowUtc);
        updated.Ownerships.Should().ContainSingle().Which.OwnerEntityId.Should().Be(seeded.ReplacementOwnerId);
        _context.Db.ChangeTracker.Clear();

        var lifecycle = await (
            from property in _context.Db.Properties.AsNoTracking()
            where property.Id == seeded.PropertyId
            select new
            {
                property.Name,
                property.UpdatedAt,
                OldEndedAt = _context.Db.PropertyOwnerships
                    .Where(ownership => ownership.Id == seeded.OriginalOwnershipId)
                    .Select(ownership => ownership.EffectiveToUtc)
                    .Single(),
                Replacement = _context.Db.PropertyOwnerships
                    .Where(ownership => ownership.PropertyId == seeded.PropertyId
                        && ownership.OwnerEntityId == seeded.ReplacementOwnerId)
                    .Select(ownership => new
                    {
                        ownership.Id,
                        ownership.EffectiveFromUtc,
                        ownership.EffectiveToUtc,
                        ownership.OwnershipSharePercent,
                    })
                    .Single(),
                ActiveCount = _context.Db.PropertyOwnerships.Count(ownership =>
                    ownership.PortfolioId == PortfolioId
                    && ownership.PropertyId == seeded.PropertyId
                    && ownership.EffectiveFromUtc <= BusinessNowUtc
                    && (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > BusinessNowUtc)),
                ActiveShare = _context.Db.PropertyOwnerships
                    .Where(ownership => ownership.PortfolioId == PortfolioId
                        && ownership.PropertyId == seeded.PropertyId
                        && ownership.EffectiveFromUtc <= BusinessNowUtc
                        && (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > BusinessNowUtc))
                    .Sum(ownership => ownership.OwnershipSharePercent),
                AuditAttemptIds = _context.Db.AtomicAuditLogs
                    .Where(audit => audit.PortfolioId == PortfolioId
                        && (audit.EntityType == nameof(Property)
                            || audit.EntityType == nameof(PropertyOwnership)))
                    .Select(audit => audit.AttemptId)
                    .Distinct()
                    .ToList(),
                OwnershipAuditCount = _context.Db.AtomicAuditLogs.Count(audit =>
                    audit.PortfolioId == PortfolioId
                    && audit.EntityType == nameof(PropertyOwnership)),
            }).SingleAsync();

        lifecycle.Name.Should().Be("Property 49 edited");
        lifecycle.UpdatedAt.Should().Be(BusinessNowUtc);
        lifecycle.OldEndedAt.Should().Be(BusinessNowUtc);
        lifecycle.Replacement.EffectiveFromUtc.Should().Be(BusinessNowUtc);
        lifecycle.Replacement.EffectiveToUtc.Should().BeNull();
        lifecycle.Replacement.OwnershipSharePercent.Should().Be(100m);
        lifecycle.ActiveCount.Should().Be(1);
        lifecycle.ActiveShare.Should().Be(100m);
        lifecycle.AuditAttemptIds.Should().ContainSingle("property and ownership audits are emitted by the same atomic attempt");
        lifecycle.OwnershipAuditCount.Should().Be(2);
    }

    [Fact]
    public async Task UpdateAsync_OwnershipOnlyAtUnchangedPropertyTimestampAuditsOnlyOwnershipLifecycle()
    {
        var seeded = await SeedPropertyWithOwnersAsync(BusinessNowUtc);
        var service = CreateService();

        var updated = await service.UpdateAsync(
            _scope,
            seeded.PropertyId,
            new UpdatePropertyRequest
            {
                Ownerships =
                [
                    new PropertyOwnershipRequest
                    {
                        OwnerEntityId = seeded.ReplacementOwnerId,
                        OwnershipSharePercent = 100m,
                    },
                ],
            },
            "property-owner-only-unchanged-property-timestamp");

        updated.Should().NotBeNull();
        updated!.UpdatedAt.Should().Be(BusinessNowUtc);
        updated.Ownerships.Should().ContainSingle().Which.OwnerEntityId.Should().Be(seeded.ReplacementOwnerId);
        _context.Db.ChangeTracker.Clear();

        var readback = await (
            from property in _context.Db.Properties.AsNoTracking()
            where property.Id == seeded.PropertyId
            select new
            {
                property.UpdatedAt,
                OriginalOwnerEndedAt = _context.Db.PropertyOwnerships
                    .Where(ownership => ownership.Id == seeded.OriginalOwnershipId)
                    .Select(ownership => ownership.EffectiveToUtc)
                    .Single(),
                ActiveOwnerId = _context.Db.PropertyOwnerships
                    .Where(ownership =>
                        ownership.PortfolioId == PortfolioId
                        && ownership.PropertyId == seeded.PropertyId
                        && ownership.EffectiveFromUtc <= BusinessNowUtc
                        && (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > BusinessNowUtc))
                    .Select(ownership => ownership.OwnerEntityId)
                    .Single(),
                PropertyAuditCount = _context.Db.AtomicAuditLogs.Count(audit =>
                    audit.PortfolioId == PortfolioId
                    && audit.CommandIdempotencyKey.Contains("property-owner-only-unchanged-property-timestamp")
                    && audit.EntityType == nameof(Property)),
                OwnershipAuditCount = _context.Db.AtomicAuditLogs.Count(audit =>
                    audit.PortfolioId == PortfolioId
                    && audit.CommandIdempotencyKey.Contains("property-owner-only-unchanged-property-timestamp")
                    && audit.EntityType == nameof(PropertyOwnership)),
                ReceiptCount = _context.Db.AtomicCommandReceipts.Count(receipt =>
                    receipt.CommandType == "rental.property.update"
                    && receipt.IdempotencyKey.Contains("property-owner-only-unchanged-property-timestamp")),
            }).SingleAsync();

        readback.UpdatedAt.Should().Be(BusinessNowUtc);
        readback.OriginalOwnerEndedAt.Should().Be(BusinessNowUtc);
        readback.ActiveOwnerId.Should().Be(seeded.ReplacementOwnerId);
        readback.PropertyAuditCount.Should().Be(0);
        readback.OwnershipAuditCount.Should().Be(2);
        readback.ReceiptCount.Should().Be(1);
    }

    [Fact]
    public async Task UpdateAsync_ReplayDoesNotDuplicateReplacementOwnershipOrAudit()
    {
        var seeded = await SeedPropertyWithOwnersAsync();
        var service = CreateService();
        var request = new UpdatePropertyRequest
        {
            Ownerships =
            [
                new PropertyOwnershipRequest
                {
                    OwnerEntityId = seeded.ReplacementOwnerId,
                    OwnershipSharePercent = 100m,
                },
            ],
        };

        var first = await service.UpdateAsync(_scope, seeded.PropertyId, request, "property-owner-replay");
        var replay = await service.UpdateAsync(_scope, seeded.PropertyId, request, "property-owner-replay");

        replay.Should().BeEquivalentTo(first);
        _context.Db.ChangeTracker.Clear();
        (await _context.Db.PropertyOwnerships.CountAsync(ownership =>
            ownership.PropertyId == seeded.PropertyId)).Should().Be(2);
        (await _context.Db.PropertyOwnerships.CountAsync(ownership =>
            ownership.PropertyId == seeded.PropertyId
            && ownership.OwnerEntityId == seeded.ReplacementOwnerId
            && ownership.EffectiveFromUtc == BusinessNowUtc)).Should().Be(1);
        (await _context.Db.AtomicAuditLogs.CountAsync(audit =>
            audit.PortfolioId == PortfolioId
            && audit.EntityType == nameof(PropertyOwnership))).Should().Be(2);
        (await _context.Db.AtomicCommandReceipts.CountAsync(receipt =>
            receipt.CommandType == "rental.property.update")).Should().Be(1);
    }

    [Fact]
    public async Task UpdateAsync_SameInstantOverlappingOwnershipKeepsRequestedOwnerAndClosesSameInstantRow()
    {
        var seeded = await SeedPropertyWithSameInstantOverlappingOwnersAsync();
        var service = CreateService();
        var request = new UpdatePropertyRequest
        {
            Name = "Property 49 edited",
            Status = PropertyStatus.Active,
            YearBuilt = 2008,
            Ownerships =
            [
                new PropertyOwnershipRequest
                {
                    OwnerEntityId = seeded.CobleOwnerId,
                    OwnershipSharePercent = 100m,
                },
            ],
        };

        var first = await service.UpdateAsync(
            _scope,
            seeded.PropertyId,
            request,
            "property-owner-same-instant-replay");
        var replay = await service.UpdateAsync(
            _scope,
            seeded.PropertyId,
            request,
            "property-owner-same-instant-replay");

        replay.Should().BeEquivalentTo(first);
        _context.Db.ChangeTracker.Clear();
        var readback = await (
            from property in _context.Db.Properties.AsNoTracking()
            where property.Id == seeded.PropertyId
            select new
            {
                property.Name,
                property.YearBuilt,
                ActiveCount = _context.Db.PropertyOwnerships.Count(ownership =>
                    ownership.PortfolioId == PortfolioId
                    && ownership.PropertyId == seeded.PropertyId
                    && ownership.EffectiveFromUtc <= BusinessNowUtc
                    && (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > BusinessNowUtc)),
                ActiveOwnerId = _context.Db.PropertyOwnerships
                    .Where(ownership =>
                        ownership.PortfolioId == PortfolioId
                        && ownership.PropertyId == seeded.PropertyId
                        && ownership.EffectiveFromUtc <= BusinessNowUtc
                        && (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > BusinessNowUtc))
                    .Select(ownership => ownership.OwnerEntityId)
                    .Single(),
                ActiveShare = _context.Db.PropertyOwnerships
                    .Where(ownership =>
                        ownership.PortfolioId == PortfolioId
                        && ownership.PropertyId == seeded.PropertyId
                        && ownership.EffectiveFromUtc <= BusinessNowUtc
                        && (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > BusinessNowUtc))
                    .Sum(ownership => ownership.OwnershipSharePercent),
                CobleOwnership = _context.Db.PropertyOwnerships
                    .Where(ownership => ownership.Id == seeded.CobleOwnershipId)
                    .Select(ownership => new
                    {
                        ownership.EffectiveFromUtc,
                        ownership.EffectiveToUtc,
                        ownership.OwnershipSharePercent,
                    })
                    .Single(),
                SameInstantOwnership = _context.Db.PropertyOwnerships
                    .Where(ownership => ownership.Id == seeded.SameInstantOwnershipId)
                    .Select(ownership => new
                    {
                        ownership.EffectiveFromUtc,
                        ownership.EffectiveToUtc,
                    })
                    .Single(),
                OwnershipAuditCount = _context.Db.AtomicAuditLogs.Count(audit =>
                    audit.PortfolioId == PortfolioId
                    && audit.EntityType == nameof(PropertyOwnership)),
                ReceiptCount = _context.Db.AtomicCommandReceipts.Count(receipt =>
                    receipt.CommandType == "rental.property.update"
                    && receipt.IdempotencyKey.Contains("property-owner-same-instant-replay")),
            }).SingleAsync();

        readback.Name.Should().Be("Property 49 edited");
        readback.YearBuilt.Should().Be(2008);
        readback.ActiveCount.Should().Be(1);
        readback.ActiveOwnerId.Should().Be(seeded.CobleOwnerId);
        readback.ActiveShare.Should().Be(100m);
        readback.CobleOwnership.EffectiveFromUtc.Should().Be(seeded.CobleEffectiveFromUtc);
        readback.CobleOwnership.EffectiveToUtc.Should().BeNull();
        readback.CobleOwnership.OwnershipSharePercent.Should().Be(100m);
        readback.SameInstantOwnership.EffectiveFromUtc.Should().Be(BusinessNowUtc.AddTicks(-10));
        readback.SameInstantOwnership.EffectiveToUtc.Should().Be(BusinessNowUtc);
        readback.OwnershipAuditCount.Should().Be(2);
        readback.ReceiptCount.Should().Be(1);
    }

    [Fact]
    public async Task UpdateAsync_ClearsOptionalAddressLine2AndReplacesCurrentOwnerInOneCommand()
    {
        var oldOwner = new OwnerEntity
        {
            PortfolioId = PortfolioId,
            Name = "Jordan",
            Email = "jordan@example.test",
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        var replacementOwner = new OwnerEntity
        {
            PortfolioId = PortfolioId,
            Name = "Juniper",
            Email = "juniper@example.test",
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Property 32",
            AddressLine1 = "610 Evergreen Lane",
            AddressLine2 = "A",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            Status = PropertyStatus.Active,
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        var originalOwnership = new PropertyOwnership
        {
            PortfolioId = PortfolioId,
            Property = property,
            OwnerEntity = oldOwner,
            OwnershipSharePercent = 100m,
            EffectiveFromUtc = SeededAtUtc,
            StatementRecipientName = oldOwner.Name,
            StatementRecipientEmail = oldOwner.Email,
            PayeeName = oldOwner.Name,
        };

        _context.Db.AddRange(oldOwner, replacementOwner, property, originalOwnership);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();

        var service = CreateService();
        var updated = await service.UpdateAsync(
            _scope,
            property.Id,
            new UpdatePropertyRequest
            {
                AddressLine1 = "610 Evergreen Street",
                AddressLine2 = "",
                Ownerships =
                [
                    new PropertyOwnershipRequest
                    {
                        OwnerEntityId = replacementOwner.Id,
                        OwnershipSharePercent = 100m,
                    },
                ],
            },
            "property32-clear-address2-replace-owner");

        updated.Should().NotBeNull();
        updated!.AddressLine1.Should().Be("610 Evergreen Street");
        updated.AddressLine2.Should().BeNull();
        updated.Ownerships.Should().ContainSingle().Which.OwnerEntityId.Should().Be(replacementOwner.Id);
        _context.Db.ChangeTracker.Clear();

        var readback = await (
            from savedProperty in _context.Db.Properties.AsNoTracking()
            where savedProperty.Id == property.Id
            select new
            {
                savedProperty.AddressLine1,
                savedProperty.AddressLine2,
                OriginalOwnerEndedAt = _context.Db.PropertyOwnerships
                    .Where(ownership => ownership.Id == originalOwnership.Id)
                    .Select(ownership => ownership.EffectiveToUtc)
                    .Single(),
                ActiveOwners = _context.Db.PropertyOwnerships
                    .Where(ownership =>
                        ownership.PortfolioId == PortfolioId
                        && ownership.PropertyId == property.Id
                        && ownership.EffectiveFromUtc <= BusinessNowUtc
                        && (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > BusinessNowUtc))
                    .Select(ownership => new
                    {
                        ownership.OwnerEntityId,
                        ownership.OwnershipSharePercent,
                    })
                    .ToList(),
                ActiveShare = _context.Db.PropertyOwnerships
                    .Where(ownership =>
                        ownership.PortfolioId == PortfolioId
                        && ownership.PropertyId == property.Id
                        && ownership.EffectiveFromUtc <= BusinessNowUtc
                        && (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > BusinessNowUtc))
                    .Sum(ownership => ownership.OwnershipSharePercent),
                AttemptIds = _context.Db.AtomicAuditLogs
                    .Where(audit => audit.PortfolioId == PortfolioId
                        && (audit.EntityType == nameof(Property)
                            || audit.EntityType == nameof(PropertyOwnership)))
                    .Select(audit => audit.AttemptId)
                    .Distinct()
                    .ToList(),
                ReceiptCount = _context.Db.AtomicCommandReceipts.Count(receipt =>
                    receipt.CommandType == "rental.property.update"
                    && receipt.IdempotencyKey.Contains("property32-clear-address2-replace-owner")),
            }).SingleAsync();

        readback.AddressLine1.Should().Be("610 Evergreen Street");
        readback.AddressLine2.Should().BeNull();
        readback.OriginalOwnerEndedAt.Should().Be(BusinessNowUtc);
        readback.ActiveOwners.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { OwnerEntityId = replacementOwner.Id, OwnershipSharePercent = 100m });
        readback.ActiveShare.Should().Be(100m);
        readback.AttemptIds.Should().ContainSingle();
        readback.ReceiptCount.Should().Be(1);
    }

    [Fact]
    public async Task UpdateAsync_PreservesFutureScheduledOwnerDuringCurrentReplacement()
    {
        var currentOwner = new OwnerEntity
        {
            PortfolioId = PortfolioId,
            Name = "Jordan Current",
            Email = "jordan-current@example.test",
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        var replacementOwner = new OwnerEntity
        {
            PortfolioId = PortfolioId,
            Name = "Juniper",
            Email = "juniper-scheduled@example.test",
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        var futureOwner = new OwnerEntity
        {
            PortfolioId = PortfolioId,
            Name = "Maple Future",
            Email = "maple-future@example.test",
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Property 32",
            AddressLine1 = "610 Evergreen Lane",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            Status = PropertyStatus.Active,
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        var currentOwnership = new PropertyOwnership
        {
            PortfolioId = PortfolioId,
            Property = property,
            OwnerEntity = currentOwner,
            OwnershipSharePercent = 100m,
            EffectiveFromUtc = SeededAtUtc,
            StatementRecipientName = currentOwner.Name,
            StatementRecipientEmail = currentOwner.Email,
            PayeeName = currentOwner.Name,
        };
        var futureEffectiveFromUtc = BusinessNowUtc.AddDays(30);
        var futureOwnership = new PropertyOwnership
        {
            PortfolioId = PortfolioId,
            Property = property,
            OwnerEntity = futureOwner,
            OwnershipSharePercent = 100m,
            EffectiveFromUtc = futureEffectiveFromUtc,
            StatementRecipientName = futureOwner.Name,
            StatementRecipientEmail = futureOwner.Email,
            PayeeName = futureOwner.Name,
        };

        _context.Db.AddRange(currentOwner, replacementOwner, futureOwner, property, currentOwnership, futureOwnership);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();

        var service = CreateService();
        var updated = await service.UpdateAsync(
            _scope,
            property.Id,
            new UpdatePropertyRequest
            {
                Ownerships =
                [
                    new PropertyOwnershipRequest
                    {
                        OwnerEntityId = replacementOwner.Id,
                        OwnershipSharePercent = 100m,
                    },
                ],
            },
            "property32-replace-current-preserve-future-owner");

        updated.Should().NotBeNull();
        updated!.Ownerships.Should().ContainSingle().Which.OwnerEntityId.Should().Be(replacementOwner.Id);
        _context.Db.ChangeTracker.Clear();

        var readback = await (
            from savedProperty in _context.Db.Properties.AsNoTracking()
            where savedProperty.Id == property.Id
            select new
            {
                CurrentOwnerEndedAt = _context.Db.PropertyOwnerships
                    .Where(ownership => ownership.Id == currentOwnership.Id)
                    .Select(ownership => ownership.EffectiveToUtc)
                    .Single(),
                FutureOwnership = _context.Db.PropertyOwnerships
                    .Where(ownership => ownership.Id == futureOwnership.Id)
                    .Select(ownership => new
                    {
                        ownership.OwnerEntityId,
                        ownership.EffectiveFromUtc,
                        ownership.EffectiveToUtc,
                        ownership.OwnershipSharePercent,
                    })
                    .Single(),
                ActiveOwners = _context.Db.PropertyOwnerships
                    .Where(ownership =>
                        ownership.PortfolioId == PortfolioId
                        && ownership.PropertyId == property.Id
                        && ownership.EffectiveFromUtc <= BusinessNowUtc
                        && (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > BusinessNowUtc))
                    .Select(ownership => new
                    {
                        ownership.OwnerEntityId,
                        ownership.OwnershipSharePercent,
                    })
                    .ToList(),
                ReceiptCount = _context.Db.AtomicCommandReceipts.Count(receipt =>
                    receipt.CommandType == "rental.property.update"
                    && receipt.IdempotencyKey.Contains("property32-replace-current-preserve-future-owner")),
            }).SingleAsync();

        readback.CurrentOwnerEndedAt.Should().Be(BusinessNowUtc);
        readback.FutureOwnership.Should().BeEquivalentTo(new
        {
            OwnerEntityId = futureOwner.Id,
            EffectiveFromUtc = futureEffectiveFromUtc,
            EffectiveToUtc = (DateTime?)null,
            OwnershipSharePercent = 100m,
        });
        readback.ActiveOwners.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { OwnerEntityId = replacementOwner.Id, OwnershipSharePercent = 100m });
        readback.ReceiptCount.Should().Be(1);
    }

    [Fact]
    public async Task UpdateAsync_OmittedOptionalAddressLine2DoesNotOverwriteExistingValue()
    {
        var seeded = await SeedPropertyWithOwnersAsync();
        var service = CreateService();

        await service.UpdateAsync(
            _scope,
            seeded.PropertyId,
            new UpdatePropertyRequest
            {
                AddressLine2 = "A",
            },
            "property-address2-seed");
        var updated = await service.UpdateAsync(
            _scope,
            seeded.PropertyId,
            new UpdatePropertyRequest
            {
                AddressLine1 = "49 Evergreen Street",
            },
            "property-address1-only");

        updated.Should().NotBeNull();
        updated!.AddressLine1.Should().Be("49 Evergreen Street");
        updated.AddressLine2.Should().Be("A");
        _context.Db.ChangeTracker.Clear();

        var readback = await _context.Db.Properties.AsNoTracking()
            .Where(property => property.Id == seeded.PropertyId)
            .Select(property => new
            {
                property.AddressLine1,
                property.AddressLine2,
                ReceiptCount = _context.Db.AtomicCommandReceipts.Count(receipt =>
                    receipt.CommandType == "rental.property.update"
                    && (receipt.IdempotencyKey.Contains("property-address2-seed")
                        || receipt.IdempotencyKey.Contains("property-address1-only"))),
            })
            .SingleAsync();

        readback.AddressLine1.Should().Be("49 Evergreen Street");
        readback.AddressLine2.Should().Be("A");
        readback.ReceiptCount.Should().Be(2);
    }

    [Fact]
    public async Task UpdateAsync_WhenSameInstantOwnershipCloseFails_RollsBackPropertyAndOwnershipChanges()
    {
        var seeded = await SeedPropertyWithSameInstantOverlappingOwnersAsync();
        await _services.DisposeAsync();
        _services = BuildServices(
            _context.ConnectionString,
            new FixedTimeProvider(new DateTimeOffset(BusinessNowUtc)),
            [new ThrowOnPropertyOwnershipUpdateInterceptor()]);
        var service = CreateService();

        var act = async () => await service.UpdateAsync(
            _scope,
            seeded.PropertyId,
            new UpdatePropertyRequest
            {
                Name = "Should roll back",
                Status = PropertyStatus.Active,
                YearBuilt = 2008,
                Ownerships =
                [
                    new PropertyOwnershipRequest
                    {
                        OwnerEntityId = seeded.CobleOwnerId,
                        OwnershipSharePercent = 100m,
                    },
                ],
            },
            "property-owner-same-instant-rollback");

        var exception = await act.Should().ThrowAsync<DbUpdateException>();
        exception.Which.InnerException.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("Injected PropertyOwnership update failure.");
        _context.Db.ChangeTracker.Clear();

        var readback = await (
            from property in _context.Db.Properties.AsNoTracking()
            where property.Id == seeded.PropertyId
            select new
            {
                property.Name,
                property.YearBuilt,
                SameInstantCount = _context.Db.PropertyOwnerships.Count(ownership =>
                    ownership.Id == seeded.SameInstantOwnershipId
                    && ownership.EffectiveToUtc == null),
                Coble = _context.Db.PropertyOwnerships
                    .Where(ownership => ownership.Id == seeded.CobleOwnershipId)
                    .Select(ownership => new
                    {
                        ownership.EffectiveFromUtc,
                        ownership.EffectiveToUtc,
                        ownership.OwnershipSharePercent,
                    })
                    .Single(),
                ReceiptCount = _context.Db.AtomicCommandReceipts.Count(receipt =>
                    receipt.CommandType == "rental.property.update"
                    && receipt.IdempotencyKey.Contains("property-owner-same-instant-rollback")),
            }).SingleAsync();

        readback.Name.Should().Be("Property 49");
        readback.YearBuilt.Should().Be(2008);
        readback.SameInstantCount.Should().Be(1);
        readback.Coble.EffectiveFromUtc.Should().Be(seeded.CobleEffectiveFromUtc);
        readback.Coble.EffectiveToUtc.Should().BeNull();
        readback.Coble.OwnershipSharePercent.Should().Be(100m);
        readback.ReceiptCount.Should().Be(0);
    }

    [Fact]
    public async Task UpdateAsync_WhenOwnershipInsertFails_RollsBackPropertyAndOwnershipChanges()
    {
        var seeded = await SeedPropertyWithOwnersAsync(BusinessNowUtc);
        await _services.DisposeAsync();
        _services = BuildServices(
            _context.ConnectionString,
            new FixedTimeProvider(new DateTimeOffset(BusinessNowUtc)),
            [new ThrowOnPropertyOwnershipInsertInterceptor()]);
        var service = CreateService();

        var act = async () => await service.UpdateAsync(
            _scope,
            seeded.PropertyId,
            new UpdatePropertyRequest
            {
                Ownerships =
                [
                    new PropertyOwnershipRequest
                    {
                        OwnerEntityId = seeded.ReplacementOwnerId,
                        OwnershipSharePercent = 100m,
                    },
                ],
            },
            "property-owner-rollback");

        var exception = await act.Should().ThrowAsync<DbUpdateException>();
        exception.Which.InnerException.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("Injected PropertyOwnership insert failure.");
        _context.Db.ChangeTracker.Clear();

        var readback = await (
            from property in _context.Db.Properties.AsNoTracking()
            where property.Id == seeded.PropertyId
            select new
            {
                property.Name,
                property.UpdatedAt,
                OriginalEnd = _context.Db.PropertyOwnerships
                    .Where(ownership => ownership.Id == seeded.OriginalOwnershipId)
                    .Select(ownership => ownership.EffectiveToUtc)
                    .Single(),
                ReplacementCount = _context.Db.PropertyOwnerships.Count(ownership =>
                    ownership.PropertyId == seeded.PropertyId
                    && ownership.OwnerEntityId == seeded.ReplacementOwnerId),
                ReceiptCount = _context.Db.AtomicCommandReceipts.Count(receipt =>
                    receipt.CommandType == "rental.property.update"
                    && receipt.IdempotencyKey.Contains("property-owner-rollback")),
            }).SingleAsync();

        readback.Name.Should().Be("Property 49");
        readback.UpdatedAt.Should().Be(BusinessNowUtc);
        readback.OriginalEnd.Should().BeNull();
        readback.ReplacementCount.Should().Be(0);
        readback.ReceiptCount.Should().Be(0);
    }

    private PropertyService CreateService() => new(
        _context.Db,
        Mock.Of<IDataUpdateService>(),
        new FixedTimeProvider(new DateTimeOffset(BusinessNowUtc)),
        _services.GetRequiredService<IAtomicUnitOfWork>());

    private async Task<SeededOwnership> SeedPropertyWithOwnersAsync(DateTime? propertyUpdatedAtUtc = null)
    {
        var originalOwner = new OwnerEntity
        {
            PortfolioId = PortfolioId,
            Name = "Blue Door",
            Email = "blue-door@example.test",
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        var replacementOwner = new OwnerEntity
        {
            PortfolioId = PortfolioId,
            Name = "Coble",
            Email = "coble@example.test",
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Property 49",
            AddressLine1 = "49 Frozen Lane",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = SeededAtUtc,
            UpdatedAt = propertyUpdatedAtUtc ?? SeededAtUtc,
        };
        var ownership = new PropertyOwnership
        {
            PortfolioId = PortfolioId,
            Property = property,
            OwnerEntity = originalOwner,
            OwnershipSharePercent = 100m,
            EffectiveFromUtc = SeededAtUtc,
            StatementRecipientName = originalOwner.Name,
            StatementRecipientEmail = originalOwner.Email,
            PayeeName = originalOwner.Name,
        };

        _context.Db.AddRange(originalOwner, replacementOwner, property, ownership);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
        return new SeededOwnership(property.Id, originalOwner.Id, replacementOwner.Id, ownership.Id);
    }

    private async Task<SeededSameInstantOwnership> SeedPropertyWithSameInstantOverlappingOwnersAsync()
    {
        var sameInstantOwner = new OwnerEntity
        {
            PortfolioId = PortfolioId,
            Name = "Blue Door",
            Email = "blue-door-same-instant@example.test",
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        var cobleOwner = new OwnerEntity
        {
            PortfolioId = PortfolioId,
            Name = "Coble",
            Email = "coble-same-instant@example.test",
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Property 49",
            AddressLine1 = "49 Frozen Lane",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            Status = PropertyStatus.Active,
            YearBuilt = 2008,
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        var sameInstantOwnership = new PropertyOwnership
        {
            PortfolioId = PortfolioId,
            Property = property,
            OwnerEntity = sameInstantOwner,
            OwnershipSharePercent = 100m,
            EffectiveFromUtc = BusinessNowUtc,
            StatementRecipientName = sameInstantOwner.Name,
            StatementRecipientEmail = sameInstantOwner.Email,
            PayeeName = sameInstantOwner.Name,
        };
        var cobleEffectiveFromUtc = new DateTime(2026, 7, 28, 7, 36, 15, 581, DateTimeKind.Utc)
            .AddTicks(4290);
        var cobleOwnership = new PropertyOwnership
        {
            PortfolioId = PortfolioId,
            Property = property,
            OwnerEntity = cobleOwner,
            OwnershipSharePercent = 100m,
            EffectiveFromUtc = cobleEffectiveFromUtc,
            StatementRecipientName = cobleOwner.Name,
            StatementRecipientEmail = cobleOwner.Email,
            PayeeName = cobleOwner.Name,
        };

        _context.Db.AddRange(sameInstantOwner, cobleOwner, property, sameInstantOwnership, cobleOwnership);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
        return new SeededSameInstantOwnership(
            property.Id,
            sameInstantOwner.Id,
            cobleOwner.Id,
            sameInstantOwnership.Id,
            cobleOwnership.Id,
            cobleEffectiveFromUtc);
    }

    private static ServiceProvider BuildServices(
        string connectionString,
        TimeProvider timeProvider,
        IEnumerable<IInterceptor>? interceptors = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(timeProvider);
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddAtomicCommandHandler<
            AtomicCoreCrudMutationCommand,
            AtomicCoreCrudMutationResult,
            AtomicCoreCrudMutationHandler>();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
        {
            builder.UseNpgsql(connectionString)
                .UseAtomicPersistenceKernel(provider);
            if (interceptors is not null)
            {
                builder.AddInterceptors(interceptors);
            }
        });
        return services.BuildServiceProvider();
    }

    private sealed record SeededOwnership(
        int PropertyId,
        int OriginalOwnerId,
        int ReplacementOwnerId,
        int OriginalOwnershipId);

    private sealed record SeededSameInstantOwnership(
        int PropertyId,
        int SameInstantOwnerId,
        int CobleOwnerId,
        int SameInstantOwnershipId,
        int CobleOwnershipId,
        DateTime CobleEffectiveFromUtc);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class ThrowOnPropertyOwnershipInsertInterceptor : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            ThrowIfOwnershipInsert(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfOwnershipInsert(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private static void ThrowIfOwnershipInsert(DbCommand command)
        {
            if (command.CommandText.Contains("INSERT INTO \"PropertyOwnerships\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Injected PropertyOwnership insert failure.");
            }
        }
    }

    private sealed class ThrowOnPropertyOwnershipUpdateInterceptor : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            ThrowIfOwnershipUpdate(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfOwnershipUpdate(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private static void ThrowIfOwnershipUpdate(DbCommand command)
        {
            if (command.CommandText.Contains("UPDATE \"PropertyOwnerships\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Injected PropertyOwnership update failure.");
            }
        }
    }
}
