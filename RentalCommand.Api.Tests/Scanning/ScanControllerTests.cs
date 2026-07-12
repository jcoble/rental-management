using System.Data.Common;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Scanning;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Scanning;

public class ScanControllerTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly RentalCommandDbContext _db;
    private readonly List<string> _executedSql = [];

    public ScanControllerTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .AddInterceptors(new RecordingCommandInterceptor(_executedSql))
            .Options;

        _db = new ScanControllerTestDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task Upload_WithWorkOrderTarget_CreatesDraft()
    {
        var scan = new Mock<IScanService>(MockBehavior.Strict);
        var uploads = new Mock<IScanUploadService>(MockBehavior.Strict);
        uploads.Setup(s => s.UploadAsync(
                42,
                7,
                "scan-op",
                "WorkOrder",
                false,
                null,
                It.Is<IReadOnlyList<ScanUploadFilePayload>>(payloads =>
                    payloads.Count == 1
                    && payloads[0].Bytes.SequenceEqual(new byte[] { 1, 2, 3 })
                    && payloads[0].ContentType == "image/jpeg"
                    && payloads[0].FileName == "work-order.jpg"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FinalizeScanUploadResult(
                null, null, "WorkOrder", [new FinalizedScanDraft(17, "Pending", "uploads/work-order.jpg")]));

        var controller = CreateController(scan.Object, uploads: uploads.Object);
        var file = new FormFile(new MemoryStream([1, 2, 3]), 0, 3, "file", "work-order.jpg")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/jpeg",
        };

        var result = await controller.Upload(file, "WorkOrder", "scan-op", CancellationToken.None);

        var created = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        var body = created.Value.Should().BeOfType<ScanCreatedResponse>().Subject;
        body.DraftId.Should().Be(17);
        body.Status.Should().Be("Pending");
        uploads.VerifyAll();
    }

    [Theory]
    [InlineData("1", false)]      // ?full=1    → original (the bug: a bool param 400'd on "1")
    [InlineData("true", false)]   // ?full=true → original
    [InlineData("True", false)]   // case-insensitive
    [InlineData("0", true)]       // ?full=0    → thumbnail (no 400)
    [InlineData("false", true)]   // ?full=false → thumbnail
    [InlineData(null, true)]      // no param   → thumbnail (default)
    [InlineData("", true)]        // empty      → thumbnail
    public async Task DownloadFile_FullFlag_SelectsOriginalOrThumbnail(string? full, bool expectThumbnail)
    {
        _db.Portfolios.Add(new Portfolio
        {
            Id = 42,
            Name = "Portfolio 42",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.ScanDrafts.Add(new ScanDraft
        {
            Id = 51,
            PortfolioId = 42,
            TargetEntityType = "Expense",
            Status = "Reviewing",
            FilePath = "uploads/scan-51.jpg",
            ThumbnailPath = "thumbs/scan-51.jpg",
            CreatedAt = DateTime.UtcNow,
        });
        _db.StoredFiles.Add(new StoredFile
        {
            PortfolioId = 42,
            FileName = "scan-51.jpg",
            FilePath = "uploads/scan-51.jpg",
            ContentType = "image/jpeg",
            FileSize = 1,
        });
        await _db.SaveChangesAsync();

        var files = new Mock<IFileStorage>(MockBehavior.Strict);
        files.Setup(f => f.DownloadAsync("thumbs/scan-51.jpg", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream([1]));
        files.Setup(f => f.DownloadAsync("uploads/scan-51.jpg", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream([2]));

        var controller = CreateController(Mock.Of<IScanService>(), files.Object);

        var result = await controller.DownloadFile(51, full, CancellationToken.None);

        result.Should().BeOfType<FileStreamResult>();
        var disposition = controller.Response.Headers["Content-Disposition"].ToString();

        if (expectThumbnail)
        {
            disposition.Should().Contain("scan-51-preview");
            files.Verify(f => f.DownloadAsync("thumbs/scan-51.jpg", It.IsAny<CancellationToken>()), Times.Once);
            files.Verify(f => f.DownloadAsync("uploads/scan-51.jpg", It.IsAny<CancellationToken>()), Times.Never);
        }
        else
        {
            disposition.Should().Contain("scan-51\"").And.NotContain("preview");
            files.Verify(f => f.DownloadAsync("uploads/scan-51.jpg", It.IsAny<CancellationToken>()), Times.Once);
            files.Verify(f => f.DownloadAsync("thumbs/scan-51.jpg", It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    [Fact]
    public async Task ListPage_IncludesCreatedUnitId_ForUnitTiedConfirmedRecords()
    {
        var now = DateTime.UtcNow;
        _db.Portfolios.Add(new Portfolio
        {
            Id = 42,
            Name = "Portfolio 42",
            ManagementCompanyName = "Test Co",
            TimeZone = "UTC",
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.Properties.Add(new Property
        {
            Id = 10,
            PortfolioId = 42,
            Name = "Test Property",
            AddressLine1 = "1 Main St",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.Units.Add(new Unit
        {
            Id = 11,
            PropertyId = 10,
            UnitNumber = "A",
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.Tenants.Add(new Tenant
        {
            Id = 12,
            PortfolioId = 42,
            FirstName = "Test",
            LastName = "Tenant",
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.Leases.Add(new Lease
        {
            Id = 13,
            PortfolioId = 42,
            PropertyId = 10,
            UnitId = 11,
            TenantId = 12,
            LeaseNumber = "L-13",
            Status = LeaseStatus.Active,
            StartDate = now.Date,
            EndDate = now.Date.AddYears(1),
            MonthlyRent = 1200m,
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.Payments.Add(new Payment
        {
            Id = 14,
            PortfolioId = 42,
            LeaseId = 13,
            Amount = 1200m,
            DueDate = now.Date,
            CreatedAt = now,
            UpdatedAt = now,
        });
        _db.RentalApplications.Add(new RentalApplication
        {
            Id = 15,
            PortfolioId = 42,
            PropertyId = 10,
            UnitId = 11,
            FirstName = "Applicant",
            LastName = "One",
            CreatedAt = now,
            UpdatedAt = now,
            SubmittedAtUtc = now,
        });
        _db.ScanDrafts.AddRange(
            new ScanDraft
            {
                Id = 21,
                PortfolioId = 42,
                TargetEntityType = "Payment",
                Status = "Confirmed",
                ConfirmedEntityId = 14,
                FilePath = "uploads/payment.jpg",
                CreatedAt = now,
            },
            new ScanDraft
            {
                Id = 22,
                PortfolioId = 42,
                TargetEntityType = "Application",
                Status = "Confirmed",
                ConfirmedEntityId = 15,
                FilePath = "uploads/application.jpg",
                CreatedAt = now.AddSeconds(1),
            });
        await _db.SaveChangesAsync();

        var controller = CreateController(Mock.Of<IScanService>());

        _executedSql.Clear();
        var result = await controller.ListPage(new ListQuery { Skip = 0, Take = 20 }, "Confirmed", CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<ScanDraftListResponse>().Subject;
        body.Items.Should().HaveCount(2);
        body.Items.Should().Contain(i =>
            i.CreatedEntityType == "Payment" &&
            i.CreatedEntityId == 14 &&
            i.CreatedUnitId == 11);
        body.Items.Should().Contain(i =>
            i.CreatedEntityType == "Application" &&
            i.CreatedEntityId == 15 &&
            i.CreatedUnitId == 11);
        _executedSql.Should().HaveCount(2, "the page uses one count and one DB-shaped item query");
        _executedSql[1].Should().ContainEquivalentOf("ORDER BY");
        _executedSql[1].Should().ContainEquivalentOf("LIMIT");
        _executedSql[1].Should().NotContain("StoredFiles");
    }

    [Fact]
    public async Task Confirm_WithoutStableOperationId_IsRejectedBeforePreparationOrAtomicAdmission()
    {
        var scan = new Mock<IScanService>(MockBehavior.Strict);
        var atomic = new RecordingAtomicUnitOfWork();
        var controller = CreateController(scan.Object, atomic: atomic);

        var result = await controller.Confirm(
            17,
            new ConfirmScanRequest { ClientOperationId = "   " },
            CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        atomic.Calls.Should().Be(0);
        scan.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(AtomicCommandDisposition.Executed, false)]
    [InlineData(AtomicCommandDisposition.Replayed, true)]
    public async Task Confirm_UsesTypedAtomicCommandAndReportsExecutionDisposition(
        AtomicCommandDisposition disposition,
        bool replayed)
    {
        var command = ExpenseCommand(17);
        var scan = new Mock<IScanService>(MockBehavior.Strict);
        scan.Setup(service => service.PrepareConfirmationAsync(
                42, 17, 7, "{}", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanConfirmationPreparation(
                ScanConfirmationPreparationOutcome.Ready, command));
        var atomic = new RecordingAtomicUnitOfWork
        {
            Outcome = new AtomicCommandOutcome<ConfirmScanDraftResult>(
                new ConfirmScanDraftResult(
                    ConfirmScanDraftOutcome.Confirmed, 17, "Expense", 91, 12),
                disposition,
                Guid.NewGuid()),
        };
        var controller = CreateController(scan.Object, atomic: atomic);

        var result = await controller.Confirm(
            17,
            new ConfirmScanRequest { ClientOperationId = "mobile-confirm-17" },
            CancellationToken.None);

        var body = result.Should().BeOfType<OkObjectResult>().Subject.Value!;
        Property(body, "entityType").Should().Be("Expense");
        Property(body, "entityId").Should().Be(91);
        Property(body, "unitId").Should().Be(12);
        Property(body, "replayed").Should().Be(replayed);
        Property(body, "atomicDisposition").Should().Be(disposition.ToString());
        atomic.Calls.Should().Be(1);
        atomic.Command.Should().BeSameAs(command);
        atomic.Identity!.CommandType.Should().Be("scan.confirm");
        atomic.Identity.IdempotencyKey.Should().StartWith("42:17:");
        scan.VerifyAll();
    }

    [Fact]
    public async Task Confirm_AlreadyConfirmed_ReturnsCanonicalEntityWithoutLegacyWrite()
    {
        var scan = ReadyScan(ExpenseCommand(17));
        var atomic = new RecordingAtomicUnitOfWork
        {
            Outcome = new AtomicCommandOutcome<ConfirmScanDraftResult>(
                new ConfirmScanDraftResult(
                    ConfirmScanDraftOutcome.AlreadyConfirmed, 17, "Payment", 88, 4),
                AtomicCommandDisposition.Executed,
                Guid.NewGuid()),
        };
        var controller = CreateController(scan.Object, atomic: atomic);

        var result = await controller.Confirm(
            17,
            new ConfirmScanRequest { ClientOperationId = "second-operation" },
            CancellationToken.None);

        var body = result.Should().BeOfType<OkObjectResult>().Subject.Value!;
        Property(body, "status").Should().Be("alreadyConfirmed");
        Property(body, "paymentId").Should().Be(88);
        Property(body, "entityType").Should().Be("Payment");
        scan.VerifyAll();
    }

    [Fact]
    public async Task Confirm_MapsNotFoundRejectedAndValidationResponses()
    {
        var notFoundScan = new Mock<IScanService>(MockBehavior.Strict);
        notFoundScan.Setup(service => service.PrepareConfirmationAsync(
                42, 17, 7, "{}", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanConfirmationPreparation(
                ScanConfirmationPreparationOutcome.DraftNotFound,
                Error: "Scan draft not found."));
        var unusedAtomic = new RecordingAtomicUnitOfWork();
        var notFound = await CreateController(notFoundScan.Object, atomic: unusedAtomic).Confirm(
            17, new ConfirmScanRequest { ClientOperationId = "missing" }, CancellationToken.None);
        notFound.Should().BeOfType<NotFoundObjectResult>();
        unusedAtomic.Calls.Should().Be(0);

        var rejectedScan = ReadyScan(ExpenseCommand(17));
        var rejectedAtomic = new RecordingAtomicUnitOfWork
        {
            Outcome = new AtomicCommandOutcome<ConfirmScanDraftResult>(
                new ConfirmScanDraftResult(
                    ConfirmScanDraftOutcome.DraftRejected, 17, "Expense", null, Error: "Draft is rejected."),
                AtomicCommandDisposition.Executed,
                Guid.NewGuid()),
        };
        var rejected = await CreateController(rejectedScan.Object, atomic: rejectedAtomic).Confirm(
            17, new ConfirmScanRequest { ClientOperationId = "rejected" }, CancellationToken.None);
        rejected.Should().BeOfType<ConflictObjectResult>();

        var validationScan = ReadyScan(ExpenseCommand(17));
        var validationAtomic = new RecordingAtomicUnitOfWork
        {
            Exception = new ScanConfirmationValidationException("Draft is not ready to confirm."),
        };
        var validation = await CreateController(validationScan.Object, atomic: validationAtomic).Confirm(
            17, new ConfirmScanRequest { ClientOperationId = "not-ready" }, CancellationToken.None);
        validation.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Confirm_LeasePreparation_ReturnsTemporaryUnavailableWithoutAtomicOrLegacyWriter()
    {
        var scan = new Mock<IScanService>(MockBehavior.Strict);
        scan.Setup(service => service.PrepareConfirmationAsync(
                42, 17, 7, "{}", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanConfirmationPreparation(
                ScanConfirmationPreparationOutcome.TemporarilyUnavailable,
                Error: "Lease scan confirmation is temporarily unavailable."));
        var atomic = new RecordingAtomicUnitOfWork();

        var result = await CreateController(scan.Object, atomic: atomic).Confirm(
            17,
            new ConfirmScanRequest { ClientOperationId = "lease-confirm" },
            CancellationToken.None);

        var unavailable = result.Should().BeOfType<ObjectResult>().Subject;
        unavailable.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        atomic.Calls.Should().Be(0);
        scan.VerifyAll();
    }

    private ScanController CreateController(
        IScanService scan,
        IFileStorage? files = null,
        IAtomicUnitOfWork? atomic = null,
        IScanUploadService? uploads = null)
    {
        var controller = new ScanController(
            scan,
            uploads ?? Mock.Of<IScanUploadService>(),
            atomic ?? Mock.Of<IAtomicUnitOfWork>(),
            _db,
            files ?? Mock.Of<IFileStorage>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim("portfolioId", "42"),
                        new Claim(ClaimTypes.NameIdentifier, "7"),
                    ], "test")),
                },
            },
        };

        return controller;
    }

    private static Mock<IScanService> ReadyScan(ConfirmScanDraftCommand command)
    {
        var scan = new Mock<IScanService>(MockBehavior.Strict);
        scan.Setup(service => service.PrepareConfirmationAsync(
                42, 17, 7, "{}", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanConfirmationPreparation(
                ScanConfirmationPreparationOutcome.Ready, command));
        return scan;
    }

    private static ConfirmScanDraftCommand ExpenseCommand(int draftId) => new(
        42,
        draftId,
        7,
        DateTime.UtcNow,
        ScanConfirmationDraftFingerprint.Create("Expense", null, null),
        new ScanConfirmationTargetData(
            ScanConfirmationTargetKind.Expense,
            Expense: new ScanExpenseTargetData(
                new ScanReceiptData(
                    null, null, null, null, null, null, null, null, null, null,
                    null, null, null, 25m, null, null, null, null, null, null, [],
                    null, null, null, []),
                true,
                null,
                null,
                null)));

    private static object? Property(object value, string name) =>
        value.GetType().GetProperty(name)!.GetValue(value);

    private sealed class RecordingAtomicUnitOfWork : IAtomicUnitOfWork
    {
        public object? Outcome { get; init; }
        public Exception? Exception { get; init; }
        public int Calls { get; private set; }
        public AtomicCommandIdentity? Identity { get; private set; }
        public object? Command { get; private set; }

        public Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
            AtomicCommandIdentity identity,
            TCommand command,
            IAtomicResultCodec<TResult> resultCodec,
            CancellationToken ct = default)
            where TCommand : notnull, IAtomicCommandData
            where TResult : notnull
        {
            Calls++;
            Identity = identity;
            Command = command;
            if (Exception is not null)
                return Task.FromException<AtomicCommandOutcome<TResult>>(Exception);
            return Task.FromResult((AtomicCommandOutcome<TResult>)Outcome!);
        }
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

internal sealed class ScanControllerTestDbContext : RentalCommandDbContext
{
    public ScanControllerTestDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }
}
