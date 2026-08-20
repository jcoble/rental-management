using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Writes;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Operations;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;
using RentalCommand.Data.Operations;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

[Collection(MigratedPostgreSqlCollection.Name)]
public sealed class WorkOrderRoleMutationPostgreSqlTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private static readonly DateTime SeededAtUtc = new(2027, 1, 20, 5, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime BusinessNowUtc = new(2027, 1, 21, 5, 0, 0, DateTimeKind.Utc);
    private static readonly AtomicJsonResultCodec<WorkOrderMutationResult> Codec =
        new("ys230-work-order-mutation-tests.v1");

    private readonly MigratedPostgreSqlFixture _fixture;
    private readonly List<string> _commands = [];
    private MigratedPostgreSqlTestContext _context = null!;
    private ServiceProvider _services = null!;

    public WorkOrderRoleMutationPostgreSqlTests(MigratedPostgreSqlFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _context = await _fixture.CreateContextAsync([new QueryRecorder(_commands)]);
        await FreezeSimulationClockAsync();
        _services = AtomicDomainTestKernel.CreateForWorkOrdersPostgreSql(
            _context.ConnectionString,
            new FixedTimeProvider(new DateTimeOffset(BusinessNowUtc)));
    }

    public async Task DisposeAsync()
    {
        if (_services is not null) await _services.DisposeAsync();
        if (_context is not null) await _context.DisposeAsync();
    }

    [Fact]
    public async Task TenantComment_ReplayReturnsExactStoredReceipt_AndChangedPayloadConflicts()
    {
        var scenario = await SeedTenantScenarioAsync();
        var command = TenantComment(scenario, "Leak is worse today.", "comment-replay");
        var identity = Identity("portal.work-order.comment", "comment-replay");

        var executed = await Atomic.ExecuteAsync(identity, command, Codec);
        var replayed = await Atomic.ExecuteAsync(identity, command, Codec);
        var changed = TenantComment(scenario, "Different payload.", "comment-replay");

        executed.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replayed.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replayed.Value.Should().BeEquivalentTo(executed.Value);
        Receipt(executed).Should().BeEquivalentTo(new
        {
            EntityId = scenario.WorkOrderId,
            Outcome = "Applied",
            CommittedAtUtc = BusinessNowUtc,
        }, options => options.ExcludingMissingMembers());
        await Atomic.Invoking(unit => unit.ExecuteAsync(identity, changed, Codec))
            .Should().ThrowAsync<AtomicIdempotencyConflictException>();

        _context.Db.ChangeTracker.Clear();
        (await _context.Db.WorkOrderStatusEvents.CountAsync(e =>
            e.WorkOrderId == scenario.WorkOrderId && e.Kind == "Comment")).Should().Be(1);
        (await _context.Db.AtomicCommandReceipts.CountAsync(r =>
            r.CommandType == identity.CommandType && r.IdempotencyKey == identity.IdempotencyKey)).Should().Be(1);
    }

    [Fact]
    public async Task TenantUpdateCancelAndComment_AreAuthorizedOwnedPublicAtomicMutations()
    {
        var scenario = await SeedTenantScenarioAsync(status: WorkOrderStatus.Scheduled);

        var update = await Atomic.ExecuteAsync(
            Identity("portal.work-order.update", "tenant-update"),
            new UpdateTenantWorkOrderCommand(
                PortfolioId, scenario.TenantUserId, scenario.AuthSessionId, scenario.AccessContextId,
                scenario.AccessRevision, scenario.WorkOrderId, "Updated sink leak",
                "Water is now reaching the cabinet floor.", "Marcus Tenant", "555-9999",
                "marcus+update@example.test", true, true, null, true,
                "Use side door.", "One cat.", "Loose stair tread.",
                BusinessNowUtc, "tenant-update"),
            Codec);

        var cancel = await Atomic.ExecuteAsync(
            Identity("portal.work-order.cancel", "tenant-cancel"),
            new CancelTenantWorkOrderCommand(
                PortfolioId, scenario.TenantUserId, scenario.AuthSessionId, scenario.AccessContextId,
                scenario.AccessRevision, scenario.WorkOrderId, "Issue resolved.", BusinessNowUtc,
                "tenant-cancel"),
            Codec);

        update.Value.Outcome.Should().Be(OperationMutationOutcome.Applied);
        cancel.Value.Outcome.Should().Be(OperationMutationOutcome.Applied);
        _context.Db.ChangeTracker.Clear();
        var workOrder = await _context.Db.WorkOrders.AsNoTracking()
            .SingleAsync(item => item.Id == scenario.WorkOrderId);
        workOrder.Title.Should().Be("Updated sink leak");
        workOrder.RequesterPhone.Should().Be("555-9999");
        workOrder.Status.Should().Be(WorkOrderStatus.Cancelled);

        var activity = await _context.Db.WorkOrderStatusEvents.AsNoTracking()
            .Where(e => e.WorkOrderId == scenario.WorkOrderId)
            .OrderBy(e => e.Id)
            .Select(e => new { e.Kind, e.Visibility, e.Note, e.CreatedAtUtc })
            .ToListAsync();
        activity.Should().Contain(e => e.Kind == "Edit" && e.Visibility == "Public");
        activity.Should().Contain(e => e.Kind == "Status" && e.Note == "Issue resolved.");
        activity.Should().OnlyContain(e => e.CreatedAtUtc == BusinessNowUtc);
    }

    [Fact]
    public async Task TenantPatch_RejectsNoOpBlankAndChronologyViolationsBeforeRowsAreWritten()
    {
        var scenario = await SeedTenantScenarioAsync();
        var blank = new UpdateTenantWorkOrderCommand(
            PortfolioId, scenario.TenantUserId, scenario.AuthSessionId, scenario.AccessContextId,
            scenario.AccessRevision, scenario.WorkOrderId, "   ", null, null, null, null,
            null, null, null, null, null, null, null, BusinessNowUtc, "blank-title");
        var noOp = new UpdateTenantWorkOrderCommand(
            PortfolioId, scenario.TenantUserId, scenario.AuthSessionId, scenario.AccessContextId,
            scenario.AccessRevision, scenario.WorkOrderId, null, null, null, null, null,
            null, null, null, null, null, null, null, BusinessNowUtc, "no-op");
        var tooLong = new UpdateTenantWorkOrderCommand(
            PortfolioId, scenario.TenantUserId, scenario.AuthSessionId, scenario.AccessContextId,
            scenario.AccessRevision, scenario.WorkOrderId, new string('x', 201), null, null, null, null,
            null, null, null, null, null, null, null, BusinessNowUtc, "too-long-title");
        var badEmail = new UpdateTenantWorkOrderCommand(
            PortfolioId, scenario.TenantUserId, scenario.AuthSessionId, scenario.AccessContextId,
            scenario.AccessRevision, scenario.WorkOrderId, null, null, null, null, "invalid-email",
            null, null, null, null, null, null, null, BusinessNowUtc, "bad-email");
        var future = await SeedTenantScenarioAsync(requestedAt: BusinessNowUtc.AddDays(1));
        var futureComment = TenantComment(future, "This should fail chronology.", "future-comment");

        await Atomic.Invoking(unit => unit.ExecuteAsync(Identity("portal.work-order.update", "blank-title"), blank, Codec))
            .Should().ThrowAsync<DomainValidationException>();
        await Atomic.Invoking(unit => unit.ExecuteAsync(Identity("portal.work-order.update", "no-op"), noOp, Codec))
            .Should().ThrowAsync<DomainValidationException>();
        await Atomic.Invoking(unit => unit.ExecuteAsync(Identity("portal.work-order.update", "too-long-title"), tooLong, Codec))
            .Should().ThrowAsync<DomainValidationException>();
        await Atomic.Invoking(unit => unit.ExecuteAsync(Identity("portal.work-order.update", "bad-email"), badEmail, Codec))
            .Should().ThrowAsync<DomainValidationException>();
        await Atomic.Invoking(unit => unit.ExecuteAsync(Identity("portal.work-order.comment", "future-comment"), futureComment, Codec))
            .Should().ThrowAsync<DomainValidationException>();

        (await _context.Db.WorkOrderStatusEvents.CountAsync(e =>
            e.WorkOrderId == scenario.WorkOrderId || e.WorkOrderId == future.WorkOrderId)).Should().Be(0);
    }

    [Fact]
    public async Task ManagerPrivateAndAssignedMaintenancePublicComments_RespectRolePrivacy()
    {
        var scenario = await SeedTenantScenarioAsync();
        var manager = _context.Db.SeedAdministratorScope(PortfolioId, nameof(ManagerPrivateAndAssignedMaintenancePublicComments_RespectRolePrivacy));
        var readOnlyTechnician = await SeedAssignedTechnicianAsync(
            scenario.WorkOrderId,
            scenario.PropertyId,
            canUpdate: false,
            kind: WorkOrderResponsibilityKind.Supporting);
        var technician = await SeedAssignedTechnicianAsync(scenario.WorkOrderId, scenario.PropertyId);

        var readOnlyAccess = await GetAssignedWorkAccessAsAsync(
            readOnlyTechnician.Scope,
            scenario.WorkOrderId);
        readOnlyAccess.RlsVisible.Should().BeTrue("the assigned-work RLS predicate must admit the row");
        readOnlyAccess.ResponsibilityVisible.Should().BeTrue(
            "the correlated responsibility must remain visible to the assigned technician");
        readOnlyAccess.AuthorizedVisible.Should().BeTrue(
            "the same assignment must carry assigned-work read capability for its responsibility");
        (await GetAuthorizedWorkOrderAsAsync(readOnlyTechnician.Scope, scenario.WorkOrderId))
            .Should().NotBeNull("assigned-work read can still open the assigned detail");
        var readOnlyDenied = await Atomic.ExecuteAsync(
            Identity("work-order.comment", "maintenance-read-public-denied"),
            new AddStaffWorkOrderCommentCommand(
                PortfolioId, readOnlyTechnician.Actor, scenario.WorkOrderId, "Read-only public denied.", false,
                BusinessNowUtc, "maintenance-read-public-denied"),
            Codec);
        readOnlyDenied.Value.Outcome.Should().Be(OperationMutationOutcome.NotFound);
        (await _context.Db.WorkOrderStatusEvents.AnyAsync(item =>
            item.WorkOrderId == scenario.WorkOrderId
            && item.Note == "Read-only public denied.")).Should().BeFalse();

        await Atomic.ExecuteAsync(
            Identity("work-order.comment", "manager-private"),
            new AddStaffWorkOrderCommentCommand(
                PortfolioId, Actor(manager), scenario.WorkOrderId, "Internal cost note.", true,
                BusinessNowUtc, "manager-private"),
            Codec);
        await Atomic.ExecuteAsync(
            Identity("work-order.comment", "maintenance-public"),
            new AddStaffWorkOrderCommentCommand(
                PortfolioId, technician.Actor, scenario.WorkOrderId, "Technician public note.", false,
                BusinessNowUtc, "maintenance-public"),
            Codec);

        var service = WorkOrderService();
        var managerDetail = await GetAuthorizedWorkOrderAsAsync(manager, scenario.WorkOrderId, service);
        var maintenanceDetail = await GetAuthorizedWorkOrderAsAsync(
            technician.Scope,
            scenario.WorkOrderId,
            service);
        var tenantDetail = await PortalService().GetWorkOrderDetailAsync(
            scenario.TenantReadScope, scenario.TenantId, scenario.WorkOrderId);

        managerDetail!.Capabilities.CanCommentPrivately.Should().BeTrue();
        managerDetail.Capabilities.CanDeletePhoto.Should().BeTrue();
        managerDetail.Activity.Should().Contain(e => e.Note == "Internal cost note." && e.Visibility == "Private");
        maintenanceDetail!.Capabilities.CanCommentPublicly.Should().BeTrue();
        maintenanceDetail.Capabilities.CanCommentPrivately.Should().BeFalse();
        maintenanceDetail.Capabilities.CanDeletePhoto.Should().BeFalse();
        maintenanceDetail.PortfolioId.Should().BeNull();
        maintenanceDetail.PropertyId.Should().BeNull();
        maintenanceDetail.UnitId.Should().BeNull();
        maintenanceDetail.TenantId.Should().BeNull();
        maintenanceDetail.LeaseManagementId.Should().BeNull();
        maintenanceDetail.VendorId.Should().BeNull();
        maintenanceDetail.RecurringMaintenanceTaskId.Should().BeNull();
        maintenanceDetail.CreatedBy.Should().BeNull();
        maintenanceDetail.PropertyName.Should().StartWith("Tenant role property ");
        maintenanceDetail.UnitNumber.Should().Be("1");
        maintenanceDetail.TenantName.Should().Be("Marcus Tenant");
        maintenanceDetail.ResidentNames.Should().Equal("Marcus Tenant");
        maintenanceDetail.RequesterName.Should().Be("Marcus Tenant");
        maintenanceDetail.RequesterPhone.Should().Be("555-0100");
        maintenanceDetail.RequesterEmail.Should().Be("marcus@example.test");
        maintenanceDetail.CallBeforeEntry.Should().BeTrue();
        maintenanceDetail.PermissionToEnter.Should().BeTrue();
        maintenanceDetail.PetWarnings.Should().Be("One cat in the unit.");
        maintenanceDetail.Activity.Should().NotContain(e => e.Visibility == "Private");
        tenantDetail!.Capabilities.CanViewTenantContact.Should().BeTrue();
        tenantDetail!.Capabilities.CanDeletePhoto.Should().BeFalse();
        tenantDetail.Activity.Should().Contain(e => e.Note == "Technician public note.");
        tenantDetail.Activity.Should().NotContain(e => e.Note == "Internal cost note.");

        var privateDenied = await Atomic.ExecuteAsync(
            Identity("work-order.comment", "maintenance-private-denied"),
            new AddStaffWorkOrderCommentCommand(
                PortfolioId, technician.Actor, scenario.WorkOrderId, "Private denied.", true,
                BusinessNowUtc, "maintenance-private-denied"),
            Codec);
        privateDenied.Value.Outcome.Should().Be(OperationMutationOutcome.NotFound);
        (await _context.Db.WorkOrderStatusEvents.AnyAsync(item =>
            item.WorkOrderId == scenario.WorkOrderId
            && item.Note == "Private denied.")).Should().BeFalse();
    }

    [Fact]
    public async Task DetailActivity_UsesLatestFiftyRowsChronological_AndSqlShowsBoundedSubquery()
    {
        var scenario = await SeedTenantScenarioAsync();
        for (var i = 0; i < 60; i++)
        {
            _context.Db.WorkOrderStatusEvents.Add(new WorkOrderStatusEvent
            {
                PortfolioId = PortfolioId,
                WorkOrderId = scenario.WorkOrderId,
                FromStatus = WorkOrderStatus.New,
                ToStatus = WorkOrderStatus.New,
                Kind = "Comment",
                Visibility = "Public",
                Note = $"activity-{i:00}",
                CreatedAtUtc = BusinessNowUtc.AddMinutes(i),
            });
        }
        await _context.Db.SaveChangesAsync();
        _commands.Clear();

        var detail = await PortalService().GetWorkOrderDetailAsync(
            scenario.TenantReadScope, scenario.TenantId, scenario.WorkOrderId);

        detail!.Activity.Should().HaveCount(50);
        detail.Activity.First().Note.Should().Be("activity-10");
        detail.Activity.Last().Note.Should().Be("activity-59");
        detail.Activity.Select(e => e.CreatedAtUtc).Should().BeInAscendingOrder();
        _commands.Should().Contain(sql =>
            sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("\"CreatedAtUtc\" DESC", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnauthorizedReplay_IsDenied_WhenTenantRelationshipIsRevoked()
    {
        var scenario = await SeedTenantScenarioAsync();
        var identity = Identity("portal.work-order.comment", "tenant-replay-auth");
        var command = TenantComment(scenario, "Authorized first.", "tenant-replay-auth");

        var executed = await Atomic.ExecuteAsync(identity, command, Codec);
        executed.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        await _context.Db.TenantUserAccesses
            .Where(access => access.AccessContextId == scenario.AccessContextId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(access => access.RevokedAtUtc, BusinessNowUtc)
                .SetProperty(access => access.RevokedByUserId, scenario.TenantUserId));

        await Atomic.Invoking(unit => unit.ExecuteAsync(identity, command, Codec))
            .Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task SchedulingExistingTenantWorkOrder_CreatesOneLinkedAppointment_AndReplayDoesNotDuplicateSideEffects()
    {
        var scenario = await SeedTenantScenarioAsync();
        var manager = _context.Db.SeedAdministratorScope(
            PortfolioId,
            nameof(SchedulingExistingTenantWorkOrder_CreatesOneLinkedAppointment_AndReplayDoesNotDuplicateSideEffects));
        var identity = Identity("work-order.update", "ys270-schedule-existing-tenant-work-order");
        var command = ScheduleExistingTenantWorkOrder(scenario, Actor(manager), identity.IdempotencyKey);

        var executed = await Atomic.ExecuteAsync(identity, command, Codec);
        var replayed = await Atomic.ExecuteAsync(identity, command, Codec);

        executed.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replayed.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replayed.Value.Should().BeEquivalentTo(executed.Value);
        _context.Db.ChangeTracker.Clear();

        var persisted = await _context.Db.WorkOrders.AsNoTracking()
            .Where(row => row.Id == scenario.WorkOrderId)
            .Select(row => new
            {
                Appointment = _context.Db.Appointments.AsNoTracking()
                    .Where(appointment => appointment.WorkOrderId == row.Id)
                    .Select(appointment => new
                    {
                        appointment.Id,
                        appointment.WorkOrderId,
                        appointment.Type,
                        appointment.Status,
                        appointment.ScheduledStart,
                        appointment.ScheduledEnd,
                        appointment.Title,
                        NotificationCount = _context.Db.Notifications.AsNoTracking()
                            .Count(notification => notification.RelatedEntityType == nameof(Appointment)
                                && notification.RelatedEntityId == appointment.Id
                                && notification.Type == "TenantAppointmentScheduled"),
                        AppointmentAuditCount = _context.Db.AtomicAuditLogs.AsNoTracking()
                            .Count(audit => audit.EntityType == nameof(Appointment)
                                && audit.EntityId == appointment.Id
                                && audit.Operation == AuditLogOperation.Created),
                        AppointmentOutboxCount = _context.Db.OutboxMessages.AsNoTracking()
                            .Count(outbox => outbox.IdempotencyKey ==
                                $"appointment-work-order-sync:{identity.IdempotencyKey}"),
                    })
                    .Single(),
                AppointmentCount = _context.Db.Appointments.AsNoTracking()
                    .Count(appointment => appointment.WorkOrderId == row.Id),
                WorkOrderUpdateOutboxCount = _context.Db.OutboxMessages.AsNoTracking()
                    .Count(outbox => outbox.IdempotencyKey == $"work-order-update:{identity.IdempotencyKey}"),
                ReceiptCount = _context.Db.AtomicCommandReceipts.AsNoTracking()
                    .Count(receipt => receipt.CommandType == identity.CommandType
                        && receipt.IdempotencyKey == identity.IdempotencyKey),
            })
            .SingleAsync();

        persisted.AppointmentCount.Should().Be(1);
        persisted.Appointment.WorkOrderId.Should().Be(scenario.WorkOrderId);
        persisted.Appointment.Type.Should().Be(AppointmentType.MaintenanceVisit);
        persisted.Appointment.Status.Should().Be(AppointmentStatus.Confirmed);
        persisted.Appointment.ScheduledStart.Should().Be(BusinessNowUtc.AddDays(2));
        persisted.Appointment.ScheduledEnd.Should().Be(BusinessNowUtc.AddDays(2).AddHours(2));
        persisted.Appointment.Title.Should().Be("Sink leak scheduled");
        persisted.Appointment.NotificationCount.Should().Be(1);
        persisted.Appointment.AppointmentAuditCount.Should().Be(1);
        persisted.Appointment.AppointmentOutboxCount.Should().Be(1);
        persisted.WorkOrderUpdateOutboxCount.Should().Be(1);
        persisted.ReceiptCount.Should().Be(1);

        var portalAppointments = await PortalService().GetAppointmentsAsync(
            PortfolioId,
            scenario.AccessContextId,
            scenario.TenantId);

        portalAppointments.Should().ContainSingle();
        portalAppointments[0].Id.Should().Be(persisted.Appointment.Id);
        portalAppointments[0].WorkOrderId.Should().Be(scenario.WorkOrderId);
        portalAppointments[0].Type.Should().Be(AppointmentType.MaintenanceVisit);
    }

    [Fact]
    public async Task StatusOnlyLinkedAppointmentPromotion_UpdatesAuditAndOutboxWithoutDuplicateTenantNotification()
    {
        var scenario = await SeedTenantScenarioAsync();
        var manager = _context.Db.SeedAdministratorScope(
            PortfolioId,
            nameof(StatusOnlyLinkedAppointmentPromotion_UpdatesAuditAndOutboxWithoutDuplicateTenantNotification));
        var actor = Actor(manager);
        var scheduledStart = BusinessNowUtc.AddDays(2).AddHours(14);
        var scheduledEnd = scheduledStart.AddHours(1);
        var scheduleIdentity = Identity("work-order.update", "ys289-link-schedule");
        var promoteIdentity = Identity("work-order.update", "ys289-promote-status-only");
        var visibleChangeIdentity = Identity("work-order.update", "ys289-visible-time-change");

        var scheduled = await Atomic.ExecuteAsync(
            scheduleIdentity,
            LinkedAppointmentUpdate(
                scenario,
                actor,
                scheduleIdentity.IdempotencyKey,
                title: "Sink leak scheduled",
                technicianAccessInstructions: "Tenant approved access.",
                scheduledForUtc: scheduledStart,
                scheduledWindowEndUtc: scheduledEnd),
            Codec);

        var promoted = await Atomic.ExecuteAsync(
            promoteIdentity,
            LinkedAppointmentUpdate(
                scenario,
                actor,
                promoteIdentity.IdempotencyKey,
                status: WorkOrderStatus.Scheduled,
                statusNote: "Vendor confirmed arrival."),
            Codec);

        scheduled.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        promoted.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        _context.Db.ChangeTracker.Clear();

        var afterPromotion = await _context.Db.Appointments.AsNoTracking()
            .Where(appointment => appointment.WorkOrderId == scenario.WorkOrderId)
            .Select(appointment => new
            {
                appointment.Id,
                appointment.Status,
                ScheduledNotificationCount = _context.Db.Notifications.AsNoTracking()
                    .Count(notification => notification.RelatedEntityType == nameof(Appointment)
                        && notification.RelatedEntityId == appointment.Id
                        && notification.Type == "TenantAppointmentScheduled"),
                UpdatedNotificationCount = _context.Db.Notifications.AsNoTracking()
                    .Count(notification => notification.RelatedEntityType == nameof(Appointment)
                        && notification.RelatedEntityId == appointment.Id
                        && notification.Type == "TenantAppointmentUpdated"),
                AppointmentUpdateAuditCount = _context.Db.AtomicAuditLogs.AsNoTracking()
                    .Count(audit => audit.EntityType == nameof(Appointment)
                        && audit.EntityId == appointment.Id
                        && audit.Operation == AuditLogOperation.Updated
                        && audit.ChangeReason == "Updated linked maintenance appointment."),
                PromotionOutboxCount = _context.Db.OutboxMessages.AsNoTracking()
                    .Count(outbox => outbox.IdempotencyKey ==
                        $"appointment-work-order-sync:{promoteIdentity.IdempotencyKey}"),
            })
            .SingleAsync();

        afterPromotion.Status.Should().Be(AppointmentStatus.Confirmed);
        afterPromotion.ScheduledNotificationCount.Should().Be(1);
        afterPromotion.UpdatedNotificationCount.Should().Be(0);
        afterPromotion.AppointmentUpdateAuditCount.Should().Be(1);
        afterPromotion.PromotionOutboxCount.Should().Be(1);

        var rescheduledStart = scheduledStart.AddHours(1);
        await Atomic.ExecuteAsync(
            visibleChangeIdentity,
            LinkedAppointmentUpdate(
                scenario,
                actor,
                visibleChangeIdentity.IdempotencyKey,
                scheduledForUtc: rescheduledStart,
                scheduledWindowEndUtc: rescheduledStart.AddHours(1)),
            Codec);
        _context.Db.ChangeTracker.Clear();

        var afterVisibleChange = await _context.Db.Appointments.AsNoTracking()
            .Where(appointment => appointment.Id == afterPromotion.Id)
            .Select(appointment => new
            {
                appointment.ScheduledStart,
                appointment.ScheduledEnd,
                UpdatedNotificationCount = _context.Db.Notifications.AsNoTracking()
                    .Count(notification => notification.RelatedEntityType == nameof(Appointment)
                        && notification.RelatedEntityId == appointment.Id
                        && notification.Type == "TenantAppointmentUpdated"),
                VisibleChangeOutboxCount = _context.Db.OutboxMessages.AsNoTracking()
                    .Count(outbox => outbox.IdempotencyKey ==
                        $"appointment-work-order-sync:{visibleChangeIdentity.IdempotencyKey}"),
            })
            .SingleAsync();

        afterVisibleChange.ScheduledStart.Should().Be(rescheduledStart);
        afterVisibleChange.ScheduledEnd.Should().Be(rescheduledStart.AddHours(1));
        afterVisibleChange.UpdatedNotificationCount.Should().Be(1);
        afterVisibleChange.VisibleChangeOutboxCount.Should().Be(1);
    }

    [Fact]
    public async Task CancelScheduledTenantWorkOrder_CancelsLinkedAppointment_AndReplayDoesNotDuplicateSideEffects()
    {
        var scenario = await SeedTenantScenarioAsync(status: WorkOrderStatus.Scheduled);
        var appointmentId = await SeedLinkedMaintenanceAppointmentAsync(
            scenario.WorkOrderId,
            AppointmentStatus.Confirmed);
        var identity = Identity("portal.work-order.cancel", "ys270-cancel-scheduled-linked-appointment");
        var command = new CancelTenantWorkOrderCommand(
            PortfolioId,
            scenario.TenantUserId,
            scenario.AuthSessionId,
            scenario.AccessContextId,
            scenario.AccessRevision,
            scenario.WorkOrderId,
            "Resident no longer needs the repair.",
            BusinessNowUtc,
            identity.IdempotencyKey);

        var executed = await Atomic.ExecuteAsync(identity, command, Codec);
        var replayed = await Atomic.ExecuteAsync(identity, command, Codec);

        executed.Disposition.Should().Be(AtomicCommandDisposition.Executed);
        replayed.Disposition.Should().Be(AtomicCommandDisposition.Replayed);
        replayed.Value.Should().BeEquivalentTo(executed.Value);
        _context.Db.ChangeTracker.Clear();

        var persisted = await _context.Db.WorkOrders.AsNoTracking()
            .Where(row => row.Id == scenario.WorkOrderId)
            .Select(row => new
            {
                row.Status,
                AppointmentStatus = _context.Db.Appointments.AsNoTracking()
                    .Where(appointment => appointment.Id == appointmentId
                        && appointment.WorkOrderId == row.Id)
                    .Select(appointment => appointment.Status)
                    .Single(),
                CancelNotificationCount = _context.Db.Notifications.AsNoTracking()
                    .Count(notification => notification.RelatedEntityType == nameof(Appointment)
                        && notification.RelatedEntityId == appointmentId
                        && notification.Type == "TenantAppointmentCancelled"),
                AppointmentAuditCount = _context.Db.AtomicAuditLogs.AsNoTracking()
                    .Count(audit => audit.EntityType == nameof(Appointment)
                        && audit.EntityId == appointmentId
                        && audit.Operation == AuditLogOperation.Updated),
                AppointmentSyncOutboxCount = _context.Db.OutboxMessages.AsNoTracking()
                    .Count(outbox => outbox.IdempotencyKey ==
                        $"appointment-work-order-sync:{identity.IdempotencyKey}"),
                ReceiptCount = _context.Db.AtomicCommandReceipts.AsNoTracking()
                    .Count(receipt => receipt.CommandType == identity.CommandType
                        && receipt.IdempotencyKey == identity.IdempotencyKey),
            })
            .SingleAsync();

        persisted.Status.Should().Be(WorkOrderStatus.Cancelled);
        persisted.AppointmentStatus.Should().Be(AppointmentStatus.Cancelled);
        persisted.CancelNotificationCount.Should().Be(1);
        persisted.AppointmentAuditCount.Should().Be(1);
        persisted.AppointmentSyncOutboxCount.Should().Be(1);
        persisted.ReceiptCount.Should().Be(1);

        var portalAppointments = await PortalService().GetAppointmentsAsync(
            PortfolioId,
            scenario.AccessContextId,
            scenario.TenantId);

        portalAppointments.Should().NotContain(appointment => appointment.Id == appointmentId);
    }

    [Fact]
    public async Task CancelRejectsWhenLinkedAppointmentSetChangesAfterLockedSnapshot_AndRollsBack()
    {
        var scenario = await SeedTenantScenarioAsync(status: WorkOrderStatus.Scheduled);
        var manager = _context.Db.SeedAdministratorScope(
            PortfolioId,
            nameof(CancelRejectsWhenLinkedAppointmentSetChangesAfterLockedSnapshot_AndRollsBack));
        var workOrder = await _context.Db.WorkOrders.AsNoTracking()
            .Where(row => row.Id == scenario.WorkOrderId)
            .Select(row => new
            {
                row.PropertyId,
                row.UnitId,
                row.LeaseManagementId,
                row.TenantId,
                row.Title,
                row.Description,
                row.Status,
                row.UpdatedAt,
            })
            .SingleAsync();
        _context.Db.ChangeTracker.Clear();

        var linkedSetGate = new LinkedAppointmentSetGate();
        await using var workOrderServices = AtomicDomainTestKernel.CreateForWorkOrdersPostgreSql(
            _context.ConnectionString,
            new FixedTimeProvider(new DateTimeOffset(BusinessNowUtc)),
            [linkedSetGate]);
        await using var appointmentServices = AtomicDomainTestKernel.CreateForAppointmentsPostgreSql(
            _context.ConnectionString,
            new FixedTimeProvider(new DateTimeOffset(BusinessNowUtc)));
        using var workOrderScope = workOrderServices.CreateScope();
        using var appointmentScope = appointmentServices.CreateScope();
        var workOrderAtomic = new WorkOrderCrudTestExecutor(workOrderScope.ServiceProvider);
        var appointmentDb = appointmentScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>();
        var appointmentWrites = appointmentScope.ServiceProvider.GetRequiredService<IRequestWriteExecutor>();

        var cancelIdentity = Identity(
            "portal.work-order.cancel",
            "linked-appointment-set-conflict");
        var cancelCommand = new CancelTenantWorkOrderCommand(
            PortfolioId,
            scenario.TenantUserId,
            scenario.AuthSessionId,
            scenario.AccessContextId,
            scenario.AccessRevision,
            scenario.WorkOrderId,
            "Resident no longer needs the repair.",
            BusinessNowUtc,
            cancelIdentity.IdempotencyKey);
        var createIdentity = Identity(
            "appointment.create",
            "linked-appointment-set-conflict-direct-create");
        var createCommand = new CreateAppointmentCommand(
            PortfolioId,
            Actor(manager),
            workOrder.PropertyId,
            workOrder.UnitId,
            workOrder.LeaseManagementId,
            null,
            workOrder.TenantId,
            scenario.WorkOrderId,
            "Direct linked appointment",
            null,
            null,
            AppointmentType.MaintenanceVisit,
            AppointmentStatus.Scheduled,
            BusinessNowUtc.AddDays(2),
            BusinessNowUtc.AddDays(2).AddHours(2),
            null,
            workOrder.Description,
            BusinessNowUtc,
            createIdentity.IdempotencyKey);

        linkedSetGate.Arm();
        var cancelTask = Task.Run(() => workOrderAtomic.ExecuteAsync(cancelIdentity, cancelCommand, Codec));
        try
        {
            await linkedSetGate.SecondSetReadReached.WaitAsync(TimeSpan.FromSeconds(10));

            var created = await Task.Run(() => appointmentWrites.ExecuteAsync(
                    AppointmentCrudWriteSupport.IdempotencyKey(createCommand.DeliveryIdempotencyKey),
                    AppointmentCrudWriteSupport.Write(
                        createCommand,
                        (command, context, ct) => AppointmentCrudWriteSupport.CreateAsync(
                            appointmentDb, command, context, ct),
                        (command, context, ct) => AppointmentCrudWriteSupport.AuthorizeReplayAsync(
                            appointmentDb, command, context, ct))))
                .WaitAsync(TimeSpan.FromSeconds(15));
            created.Value.Outcome.Should().Be(OperationMutationOutcome.Applied);

            linkedSetGate.Release();
            var conflict = await Assert.ThrowsAsync<DomainValidationException>(async () => await cancelTask);
            conflict.StatusCode.Should().Be(409);
            conflict.Message.Should().Be("The linked appointment changed; refresh and retry.");

            _context.Db.ChangeTracker.Clear();
            var persisted = await _context.Db.WorkOrders.AsNoTracking()
                .Where(row => row.Id == scenario.WorkOrderId)
                .Select(row => new
                {
                    row.Status,
                    row.UpdatedAt,
                    StatusEventCount = _context.Db.WorkOrderStatusEvents.AsNoTracking()
                        .Count(statusEvent => statusEvent.WorkOrderId == row.Id),
                    AppointmentCount = _context.Db.Appointments.AsNoTracking()
                        .Count(appointment => appointment.WorkOrderId == row.Id),
                    AppointmentStatus = _context.Db.Appointments.AsNoTracking()
                        .Where(appointment => appointment.WorkOrderId == row.Id)
                        .Select(appointment => appointment.Status)
                        .Single(),
                    CancelAuditCount = _context.Db.AtomicAuditLogs.AsNoTracking()
                        .Count(audit => audit.CommandIdempotencyKey == cancelIdentity.IdempotencyKey),
                    CancelNotificationCount = _context.Db.Notifications.AsNoTracking()
                        .Count(notification => notification.RelatedEntityType == nameof(Appointment)
                            && notification.RelatedEntityId == created.Value.EntityId
                            && notification.Type == "TenantAppointmentCancelled"),
                    CancelOutboxCount = _context.Db.OutboxMessages.AsNoTracking()
                        .Count(outbox => outbox.IdempotencyKey ==
                                $"tenant-work-order-cancel:{cancelIdentity.IdempotencyKey}" ||
                            outbox.IdempotencyKey ==
                                $"appointment-work-order-sync:{cancelIdentity.IdempotencyKey}"),
                    CancelReceiptCount = _context.Db.AtomicCommandReceipts.AsNoTracking()
                        .Count(receipt => receipt.CommandType == cancelIdentity.CommandType
                            && receipt.IdempotencyKey == cancelIdentity.IdempotencyKey),
                })
                .SingleAsync();

            persisted.Status.Should().Be(workOrder.Status);
            persisted.UpdatedAt.Should().Be(workOrder.UpdatedAt);
            persisted.StatusEventCount.Should().Be(0);
            persisted.AppointmentCount.Should().Be(1);
            persisted.AppointmentStatus.Should().Be(AppointmentStatus.Scheduled);
            persisted.CancelAuditCount.Should().Be(0);
            persisted.CancelNotificationCount.Should().Be(0);
            persisted.CancelOutboxCount.Should().Be(0);
            persisted.CancelReceiptCount.Should().Be(0);
        }
        finally
        {
            linkedSetGate.Release();
        }
    }

    [Fact]
    public void Model_WorkOrderAppointmentIndexIsUniqueFiltered_AndExpenseWorkOrderIndexAllowsMany()
    {
        var appointmentWorkOrderIndex = _context.Db.Model.FindEntityType(typeof(Appointment))!
            .GetIndexes()
            .Single(index => index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(Appointment.WorkOrderId)]));
        var expenseWorkOrderIndex = _context.Db.Model.FindEntityType(typeof(Expense))!
            .GetIndexes()
            .Single(index => index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(Expense.WorkOrderId)]));

        appointmentWorkOrderIndex.IsUnique.Should().BeTrue();
        appointmentWorkOrderIndex.GetFilter().Should().Be("\"WorkOrderId\" IS NOT NULL");
        expenseWorkOrderIndex.IsUnique.Should().BeFalse();
        expenseWorkOrderIndex.GetFilter().Should().BeNull();
    }

    [Fact]
    public async Task InjectedAuditFailure_RollsBackEntityActivityAuditOutboxAndReceipt()
    {
        var scenario = await SeedTenantScenarioAsync();
        await _services.DisposeAsync();
        var failure = new AuditInsertFailureInterceptor();
        _services = AtomicDomainTestKernel.CreateForWorkOrdersPostgreSql(
            _context.ConnectionString,
            new FixedTimeProvider(new DateTimeOffset(BusinessNowUtc)),
            [failure]);
        var identity = Identity("portal.work-order.comment", "rollback-comment");
        var command = TenantComment(scenario, "Rollback this comment.", "rollback-comment");
        failure.FailAtomicAudit = true;

        var thrown = await Atomic.Invoking(unit => unit.ExecuteAsync(identity, command, Codec))
            .Should().ThrowAsync<DbUpdateException>();
        thrown.Which.InnerException.Should().BeOfType<InvalidOperationException>();

        _context.Db.ChangeTracker.Clear();
        (await _context.Db.WorkOrderStatusEvents.CountAsync(e =>
            e.WorkOrderId == scenario.WorkOrderId && e.Note == "Rollback this comment.")).Should().Be(0);
        (await _context.Db.AtomicAuditLogs.CountAsync(a =>
            a.CommandIdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
        (await _context.Db.OutboxMessages.CountAsync(o =>
            o.IdempotencyKey.Contains("rollback-comment"))).Should().Be(0);
        (await _context.Db.AtomicCommandReceipts.CountAsync(r =>
            r.CommandType == identity.CommandType && r.IdempotencyKey == identity.IdempotencyKey)).Should().Be(0);
    }

    [Fact]
    public void Migration_RepairsChronologyInvariantsAndAddsActivityVisibility()
    {
        var migration = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "RentalCommand.Data",
            "Migrations",
            "20260728213000_AddWorkOrderActivityVisibilityAndRepairTimeline.cs"));

        migration.Should().Contain("\"WorkOrderStatusEvents\"");
        migration.Should().Contain("\"CreatedAtUtc\" < w.\"RequestedAt\"");
        migration.Should().Contain("ChronologyRepairOriginalCreatedAtUtc");
        migration.Should().NotContain("SET \"CompletedAt\" = \"RequestedAt\"");
        migration.Should().Contain("ChronologyRepairOriginalUpdatedAtUtc");
        migration.Should().Contain("\"UpdatedAt\" < \"RequestedAt\"");
        migration.Should().Contain("Kind");
        migration.Should().Contain("Visibility");
    }

    private WorkOrderCrudTestExecutor Atomic => new(_services);

    private sealed class WorkOrderCrudTestExecutor(IServiceProvider services)
    {
        private RentalCommandDbContext Db => services.GetRequiredService<RentalCommandDbContext>();
        private IRequestWriteExecutor Writes => services.GetRequiredService<IRequestWriteExecutor>();

        public Task<AtomicCommandOutcome<WorkOrderMutationResult>> ExecuteAsync(
            AtomicCommandIdentity identity, AddStaffWorkOrderCommentCommand command,
            AtomicJsonResultCodec<WorkOrderMutationResult> codec) =>
            Writes.ExecuteAsync(identity.IdempotencyKey, WorkOrderCrudWriteSupport.Write(
                command,
                (request, context, ct) => new AddStaffWorkOrderCommentRule(Db).HandleAsync(request, context, ct),
                (request, context, ct) => new AddStaffWorkOrderCommentRule(Db).AuthorizeReplayAsync(request, context, ct)));

        public Task<AtomicCommandOutcome<WorkOrderMutationResult>> ExecuteAsync(
            AtomicCommandIdentity identity, UpdateWorkOrderCommand command,
            AtomicJsonResultCodec<WorkOrderMutationResult> codec) =>
            Writes.ExecuteAsync(identity.IdempotencyKey, WorkOrderCrudWriteSupport.Write(
                command,
                (request, context, ct) => new UpdateWorkOrderRule(Db).HandleAsync(request, context, ct),
                (request, context, ct) => new UpdateWorkOrderRule(Db).AuthorizeReplayAsync(request, context, ct)));

        public Task<AtomicCommandOutcome<WorkOrderMutationResult>> ExecuteAsync(
            AtomicCommandIdentity identity, AddTenantWorkOrderCommentCommand command,
            AtomicJsonResultCodec<WorkOrderMutationResult> codec) =>
            Writes.ExecuteAsync(identity.IdempotencyKey, WorkOrderCrudWriteSupport.Write(
                command,
                (request, context, ct) => new AddTenantWorkOrderCommentRule(Db).HandleAsync(request, context, ct),
                (request, context, ct) => new AddTenantWorkOrderCommentRule(Db).AuthorizeReplayAsync(request, context, ct)));

        public Task<AtomicCommandOutcome<WorkOrderMutationResult>> ExecuteAsync(
            AtomicCommandIdentity identity, UpdateTenantWorkOrderCommand command,
            AtomicJsonResultCodec<WorkOrderMutationResult> codec) =>
            Writes.ExecuteAsync(identity.IdempotencyKey, WorkOrderCrudWriteSupport.Write(
                command,
                (request, context, ct) => new UpdateTenantWorkOrderRule(Db).HandleAsync(request, context, ct),
                (request, context, ct) => new UpdateTenantWorkOrderRule(Db).AuthorizeReplayAsync(request, context, ct)));

        public Task<AtomicCommandOutcome<WorkOrderMutationResult>> ExecuteAsync(
            AtomicCommandIdentity identity, CancelTenantWorkOrderCommand command,
            AtomicJsonResultCodec<WorkOrderMutationResult> codec) =>
            Writes.ExecuteAsync(identity.IdempotencyKey, WorkOrderCrudWriteSupport.Write(
                command,
                (request, context, ct) => new CancelTenantWorkOrderRule(Db).HandleAsync(request, context, ct),
                (request, context, ct) => new CancelTenantWorkOrderRule(Db).AuthorizeReplayAsync(request, context, ct)));
    }

    private static AtomicCommandIdentity Identity(string type, string key) => new(type, key);

    private static StaffOperationActor Actor(WorkspaceReadScope scope) => new(
        scope.UserId, scope.SessionId, scope.AccessContextId, scope.AccessRevision);

    private static WorkOrderMutationReceipt Receipt(AtomicCommandOutcome<WorkOrderMutationResult> outcome)
    {
        var receipt = outcome.Value.Receipt;
        receipt.Should().NotBeNull();
        return new WorkOrderMutationReceipt
        {
            EntityId = receipt!.EntityId,
            Outcome = receipt.Outcome.ToString(),
            ActivityId = receipt.ActivityId,
            CommittedAtUtc = receipt.CommittedAtUtc,
        };
    }

    private AddTenantWorkOrderCommentCommand TenantComment(TenantScenario scenario, string body, string key) => new(
        PortfolioId, scenario.TenantUserId, scenario.AuthSessionId, scenario.AccessContextId,
        scenario.AccessRevision, scenario.WorkOrderId, body, BusinessNowUtc, key);

    private async Task<int> SeedLinkedMaintenanceAppointmentAsync(
        int workOrderId,
        AppointmentStatus status)
    {
        var workOrder = await _context.Db.WorkOrders.AsNoTracking()
            .Where(row => row.Id == workOrderId)
            .Select(row => new
            {
                row.PortfolioId,
                row.PropertyId,
                row.UnitId,
                row.LeaseManagementId,
                row.TenantId,
                row.Title,
                row.Description,
            })
            .SingleAsync();

        var scheduledStart = BusinessNowUtc.AddDays(2);
        var appointment = new Appointment
        {
            PortfolioId = workOrder.PortfolioId,
            PropertyId = workOrder.PropertyId,
            UnitId = workOrder.UnitId,
            LeaseManagementId = workOrder.LeaseManagementId,
            TenantId = workOrder.TenantId,
            WorkOrderId = workOrderId,
            Title = workOrder.Title,
            Type = AppointmentType.MaintenanceVisit,
            Status = status,
            ScheduledStart = scheduledStart,
            ScheduledEnd = scheduledStart.AddHours(2),
            Notes = workOrder.Description,
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        _context.Db.Appointments.Add(appointment);
        await _context.Db.WorkOrders
            .Where(row => row.Id == workOrderId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.ScheduledFor, scheduledStart)
                .SetProperty(row => row.ScheduledWindowEnd, scheduledStart.AddHours(2))
                .SetProperty(row => row.UpdatedAt, SeededAtUtc));
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
        return appointment.Id;
    }

    private static UpdateWorkOrderCommand ScheduleExistingTenantWorkOrder(
        TenantScenario scenario,
        StaffOperationActor actor,
        string key) => new(
        PortfolioId,
        actor,
        scenario.WorkOrderId,
        UnitId: null,
        ClearUnit: false,
        TenantId: null,
        ClearTenant: false,
        LeaseManagementId: null,
        ClearLeaseManagement: false,
        VendorId: null,
        Title: "Sink leak scheduled",
        Description: null,
        TechnicianAccessInstructions: "Tenant approved access.",
        SubmittedByLabel: null,
        RequesterName: null,
        RequesterPhone: null,
        RequesterEmail: null,
        ResidentMustBePresent: null,
        CallBeforeEntry: null,
        CallIfNotHome: null,
        PermissionToEnter: null,
        EntryNotes: null,
        PetWarnings: null,
        AccessWarnings: null,
        Category: null,
        Priority: null,
        Status: WorkOrderStatus.Scheduled,
        StatusNote: "Scheduled from YS-270 regression.",
        RequestedAtUtc: null,
        ScheduledForUtc: BusinessNowUtc.AddDays(2),
        ScheduledWindowEndUtc: BusinessNowUtc.AddDays(2).AddHours(2),
        CompletedAtUtc: null,
        EstimatedCost: null,
        ActualCost: null,
        BusinessNowUtc,
        key);

    private static UpdateWorkOrderCommand LinkedAppointmentUpdate(
        TenantScenario scenario,
        StaffOperationActor actor,
        string key,
        string? title = null,
        string? technicianAccessInstructions = null,
        WorkOrderStatus? status = null,
        string? statusNote = null,
        DateTime? scheduledForUtc = null,
        DateTime? scheduledWindowEndUtc = null) => new(
        PortfolioId,
        actor,
        scenario.WorkOrderId,
        UnitId: null,
        ClearUnit: false,
        TenantId: null,
        ClearTenant: false,
        LeaseManagementId: null,
        ClearLeaseManagement: false,
        VendorId: null,
        Title: title,
        Description: null,
        TechnicianAccessInstructions: technicianAccessInstructions,
        SubmittedByLabel: null,
        RequesterName: null,
        RequesterPhone: null,
        RequesterEmail: null,
        ResidentMustBePresent: null,
        CallBeforeEntry: null,
        CallIfNotHome: null,
        PermissionToEnter: null,
        EntryNotes: null,
        PetWarnings: null,
        AccessWarnings: null,
        Category: null,
        Priority: null,
        Status: status,
        StatusNote: statusNote,
        RequestedAtUtc: null,
        ScheduledForUtc: scheduledForUtc,
        ScheduledWindowEndUtc: scheduledWindowEndUtc,
        CompletedAtUtc: null,
        EstimatedCost: null,
        ActualCost: null,
        BusinessNowUtc,
        key);

    private PortalService PortalService() => new(
        _context.Db,
        Mock.Of<ILeaseQaService>(),
        new FixedTimeProvider(new DateTimeOffset(BusinessNowUtc)),
        _services.GetRequiredService<IAtomicUnitOfWork>(),
        _services.GetRequiredService<IRequestWriteExecutor>());

    private WorkOrderService WorkOrderService() => new(
        _context.Db,
        Mock.Of<IDataUpdateService>(),
        Mock.Of<IMessagePublisher>(),
        Mock.Of<IFileStorage>(),
        NullLogger<WorkOrderService>.Instance,
        new FixedTimeProvider(new DateTimeOffset(BusinessNowUtc)),
        _services.GetRequiredService<IRequestWriteExecutor>());

    private async Task<WorkOrderDetailResponse?> GetAuthorizedWorkOrderAsAsync(
        WorkspaceReadScope scope,
        int workOrderId,
        WorkOrderService? service = null)
    {
        await _context.ActivateApiScopeAsync(scope);
        try
        {
            return await (service ?? WorkOrderService()).GetAuthorizedAsync(scope, workOrderId);
        }
        finally
        {
            await _context.Db.Database.ExecuteSqlRawAsync("RESET SESSION AUTHORIZATION;");
            await _context.Db.Database.CloseConnectionAsync();
        }
    }

    private async Task<AssignedWorkAccessDiagnostic> GetAssignedWorkAccessAsAsync(
        WorkspaceReadScope scope,
        int workOrderId)
    {
        await _context.ActivateApiScopeAsync(scope);
        try
        {
            var rlsVisible = await _context.Db.WorkOrders
                .AsNoTracking()
                .AnyAsync(item => item.Id == workOrderId && item.PortfolioId == scope.PortfolioId);
            var responsibilityVisible = await _context.Db.WorkOrderResponsibilities
                .AsNoTracking()
                .AnyAsync(item =>
                    item.WorkOrderId == workOrderId
                    && item.PortfolioId == scope.PortfolioId);
            var authorizedVisible = await _context.Db.WorkOrders
                .AsNoTracking()
                .Where(item => item.Id == workOrderId)
                .WhereAuthorized(
                    _context.Db,
                    scope,
                    [CapabilityKeys.AssignedWorkRead],
                    BusinessNowUtc)
                .AnyAsync();
            return new AssignedWorkAccessDiagnostic(
                rlsVisible,
                responsibilityVisible,
                authorizedVisible);
        }
        finally
        {
            await _context.Db.Database.ExecuteSqlRawAsync("RESET SESSION AUTHORIZATION;");
            await _context.Db.Database.CloseConnectionAsync();
        }
    }

    private async Task<TenantScenario> SeedTenantScenarioAsync(
        WorkOrderStatus status = WorkOrderStatus.New,
        DateTime? requestedAt = null)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var securityEffectiveUtc = DateTime.UtcNow.AddMinutes(-5);
        var user = new ApplicationUser
        {
            UserName = $"tenant-{suffix}@example.test",
            NormalizedUserName = $"TENANT-{suffix}@EXAMPLE.TEST",
            Email = $"tenant-{suffix}@example.test",
            NormalizedEmail = $"TENANT-{suffix}@EXAMPLE.TEST",
            DisplayName = "Marcus Tenant",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = SeededAtUtc,
        };
        var accessContext = new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = PortfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Tenant,
            CreatedAtUtc = SeededAtUtc,
            UpdatedAtUtc = SeededAtUtc,
        };
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = $"Tenant role property {suffix}",
            AddressLine1 = "1 Repair Way",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Marcus",
            LastName = "Tenant",
            Email = "marcus@example.test",
            Phone = "555-0100",
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        _context.Db.AddRange(accessContext, property, tenant);
        await _context.Db.SaveChangesAsync();
        var unit = new Unit
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitNumber = "1",
            CreatedAt = SeededAtUtc,
            UpdatedAt = SeededAtUtc,
        };
        _context.Db.Units.Add(unit);
        await _context.Db.SaveChangesAsync();
        var relationship = new LeaseManagement
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            RelationshipNumber = $"LM-{suffix}",
            PossessionGivenAtUtc = SeededAtUtc.AddDays(-30),
            CreatedAtUtc = SeededAtUtc,
            UpdatedAtUtc = SeededAtUtc,
            CreatedByUserId = user.Id,
            RowVersion = Guid.NewGuid(),
        };
        _context.Db.LeaseManagements.Add(relationship);
        await _context.Db.SaveChangesAsync();
        var party = new LeaseManagementParty
        {
            PortfolioId = PortfolioId,
            LeaseManagementId = relationship.Id,
            TenantId = tenant.Id,
            Role = LeaseManagementPartyRole.PrimaryTenant,
            EffectiveFrom = new DateOnly(2020, 1, 1),
            ChangeReason = "YS-230 tenant role proof",
            CreatedAtUtc = SeededAtUtc,
            CreatedByUserId = user.Id,
        };
        _context.Db.LeaseManagementParties.Add(party);
        await _context.Db.SaveChangesAsync();
        _context.Db.TenantUserAccesses.Add(new TenantUserAccess
        {
            PublicId = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            AccessContextId = accessContext.Id,
            ApplicationUserId = user.Id,
            LeaseManagementPartyId = party.Id,
            GrantedAtUtc = SeededAtUtc,
            GrantedByUserId = user.Id,
            Reason = "YS-230 tenant role proof",
        });
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            ActiveAccessContextId = accessContext.Id,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = SeededAtUtc,
            LastSeenAtUtc = SeededAtUtc,
            ExpiresAtUtc = BusinessNowUtc.AddDays(1),
        };
        var workOrder = new WorkOrder
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            TenantId = tenant.Id,
            LeaseManagementId = relationship.Id,
            Title = "Sink leak",
            Description = "Water under cabinet.",
            Category = "Plumbing",
            Priority = WorkOrderPriority.Normal,
            Status = status,
            RequesterName = "Marcus Tenant",
            RequesterPhone = "555-0100",
            RequesterEmail = "marcus@example.test",
            CallBeforeEntry = true,
            PermissionToEnter = true,
            PetWarnings = "One cat in the unit.",
            RequestedAt = requestedAt ?? SeededAtUtc,
            UpdatedAt = requestedAt ?? SeededAtUtc,
        };
        _context.Db.AddRange(session, workOrder);
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
        return new TenantScenario(
            user.Id,
            session.Id,
            accessContext.Id,
            accessContext.AccessRevision,
            tenant.Id,
            property.Id,
            workOrder.Id,
            new PortalTenantReadScope(PortfolioId, user.Id, accessContext.Id, accessContext.AccessRevision));
    }

    private async Task<TechnicianScenario> SeedAssignedTechnicianAsync(
        int workOrderId,
        int propertyId,
        bool canUpdate = true,
        WorkOrderResponsibilityKind kind = WorkOrderResponsibilityKind.Primary)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var user = new ApplicationUser
        {
            UserName = $"tech-{suffix}@example.test",
            NormalizedUserName = $"TECH-{suffix}@EXAMPLE.TEST",
            Email = $"tech-{suffix}@example.test",
            NormalizedEmail = $"TECH-{suffix}@EXAMPLE.TEST",
            DisplayName = "Assigned Technician",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = SeededAtUtc,
        };
        var accessContext = new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = PortfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            LastAuthorizedExperience = WorkspaceExperience.Maintenance,
            CreatedAtUtc = SeededAtUtc,
            UpdatedAtUtc = SeededAtUtc,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = PortfolioId,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Maintenance,
            EffectiveFromUtc = SeededAtUtc,
            CreatedAtUtc = SeededAtUtc,
            UpdatedAtUtc = SeededAtUtc,
        };
        var roleProfileId = canUpdate
            ? AccessCatalog.Roles.Single(role => role.Key == RoleProfileKeys.MaintenanceTechnician).Id
            : await SeedAssignedWorkReadOnlyRoleAsync(suffix);
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = PortfolioId,
            RoleProfileId = roleProfileId,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AssignedWorkOrders,
            EffectiveFromUtc = SeededAtUtc,
            CreatedAtUtc = SeededAtUtc,
            UpdatedAtUtc = SeededAtUtc,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            User = user,
            ActiveAccessContext = accessContext,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = SeededAtUtc,
            LastSeenAtUtc = SeededAtUtc,
            ExpiresAtUtc = BusinessNowUtc.AddDays(1),
        };
        _context.Db.AddRange(assignment, session);
        await _context.Db.SaveChangesAsync();
        _context.Db.WorkOrderResponsibilities.Add(new WorkOrderResponsibility
        {
            Id = Guid.NewGuid(),
            PortfolioId = PortfolioId,
            WorkOrderId = workOrderId,
            PropertyId = propertyId,
            WorkspaceMembershipId = membership.Id,
            MembershipRoleAssignmentId = assignment.Id,
            Kind = kind,
            EffectiveFromUtc = SeededAtUtc,
            AssignedAtUtc = SeededAtUtc,
            AssignedByUserId = user.Id,
            AssignedByAccessContextId = accessContext.Id,
            AssignedReason = "YS-230 assigned comment proof",
        });
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
        var scope = new WorkspaceReadScope(
            PortfolioId, user.Id, session.Id, accessContext.Id, accessContext.AccessRevision);
        return new TechnicianScenario(scope, Actor(scope));
    }

    private async Task FreezeSimulationClockAsync()
    {
        var clock = await _context.Db.SimulationClocks.SingleOrDefaultAsync(item => item.Id == 1);
        if (clock is null)
        {
            clock = new SimulationClock { Id = 1 };
            _context.Db.SimulationClocks.Add(clock);
        }

        clock.Mode = ClockMode.Frozen;
        clock.SimAnchorUtc = BusinessNowUtc;
        clock.RealAnchorUtc = DateTime.UtcNow;
        clock.TimeZoneId = "UTC";
        clock.UpdatedAtRealUtc = DateTime.UtcNow;
        await _context.Db.SaveChangesAsync();
        _context.Db.ChangeTracker.Clear();
    }

    private async Task<int> SeedAssignedWorkReadOnlyRoleAsync(string suffix)
    {
        var roleProfileId = Math.Abs(suffix.GetHashCode()) + 100_000;
        var key = $"ys230-assigned-work-read-{suffix}";
        await _context.Db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "RoleProfiles" ("Id", "Key", "DisplayName", "Description", "DefaultExperience", "DefaultScopeKind")
            VALUES ({roleProfileId}, {key}, 'YS-230 Assigned Work Read', 'Read-only assigned-work proof role.', 'Maintenance', 'AssignedWorkOrders');
            INSERT INTO "RoleProfileCapabilities" ("RoleProfileId", "CapabilityDefinitionId")
            SELECT {roleProfileId}, "Id"
            FROM "CapabilityDefinitions"
            WHERE "Key" = {CapabilityKeys.AssignedWorkRead};
            """);
        return roleProfileId;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RentalCommand.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find repository root.");
    }

    private sealed class QueryRecorder(List<string> commands) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            commands.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class AuditInsertFailureInterceptor : DbCommandInterceptor
    {
        public bool FailAtomicAudit { get; set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            ThrowIfConfigured(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfConfigured(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            ThrowIfConfigured(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfConfigured(command);
            return ValueTask.FromResult(result);
        }

        private void ThrowIfConfigured(DbCommand command)
        {
            if (FailAtomicAudit
                && command.CommandText.Contains(
                    "INSERT INTO \"AtomicAuditLogs\"",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Injected audit failure.");
            }
        }
    }

    private sealed record AssignedWorkAccessDiagnostic(
        bool RlsVisible,
        bool ResponsibilityVisible,
        bool AuthorizedVisible);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class LinkedAppointmentSetGate : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _secondSetReadReached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;
        private int _linkedSetReadCount;

        public Task SecondSetReadReached => _secondSetReadReached.Task;

        public void Arm()
        {
            Interlocked.Exchange(ref _linkedSetReadCount, 0);
            Interlocked.Exchange(ref _armed, 1);
        }

        public void Release() => _release.TrySetResult();

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (ShouldPause(command) && Interlocked.Increment(ref _linkedSetReadCount) == 2)
            {
                _secondSetReadReached.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }

            return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }

        private bool ShouldPause(DbCommand command) =>
            Volatile.Read(ref _armed) == 1 &&
            command.CommandText.Contains("\"Appointments\"", StringComparison.Ordinal) &&
            command.CommandText.Contains("\"WorkOrderId\"", StringComparison.Ordinal) &&
            command.CommandText.Contains("ORDER BY", StringComparison.Ordinal);
    }

    private sealed record TenantScenario(
        int TenantUserId,
        Guid AuthSessionId,
        int AccessContextId,
        long AccessRevision,
        int TenantId,
        int PropertyId,
        int WorkOrderId,
        PortalTenantReadScope TenantReadScope);

    private sealed record TechnicianScenario(WorkspaceReadScope Scope, StaffOperationActor Actor);
}
