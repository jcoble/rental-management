using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Screening;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Documents;
using RentalCommand.Data.Screening;
using RentalCommand.TestCommon;

namespace RentalCommand.IntegrationTests;

[Collection(FinancialReportPostgreSqlCollection.Name)]
public sealed class PdfFinalizationPostgreSqlTests : IAsyncLifetime
{
    private const int PortfolioId = 1;
    private readonly MigratedPostgreSqlFixture _fixture;
    private MigratedPostgreSqlTestContext _context = null!;
    private RentalCommandDbContext _db = null!;
    private ServiceProvider _services = null!;
    private WorkspaceReadScope _scope;

    public PdfFinalizationPostgreSqlTests(MigratedPostgreSqlFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _context = await _fixture.CreateContextAsync();
        _db = _context.Db;
        _scope = await SeedAdministratorScopeAsync();
        _services = BuildServices(_context.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task AdverseActionFinalization_RejectsChangedDecisionInPostgreSql()
    {
        var seeded = await SeedDeclinedCaseAsync();
        await using var serviceScope = _services.CreateAsyncScope();
        var provider = serviceScope.ServiceProvider;
        var writes = provider.GetRequiredService<IWriteExecutor>();
        var prepareCommand = new PrepareAdverseActionNoticeCommand(
                PortfolioId,
                seeded.Application.Id,
                _scope.UserId,
                _scope.SessionId,
                _scope.AccessContextId,
                _scope.AccessRevision,
                "integration-prepare",
                null,
                false);
        var prepareHandler = new PrepareAdverseActionNoticeRule(provider.GetRequiredService<RentalCommandDbContext>());
        var prepared = await writes.ExecuteAsync("integration-prepare",
            ScreeningWriteSupport.Write(prepareCommand, prepareHandler.ExecuteAsync,
                prepareHandler.AuthorizeAsync));

        var pending = await provider.GetRequiredService<IPendingFileUploadStore>().PrepareAsync(
            PortfolioId,
            _scope.UserId,
            "adverse-action-pdf",
            "integration-finalize",
            "integration-pdf-fingerprint",
            prepared.Value.FileName!,
            "application/pdf",
            8,
            seeded.Now);
        seeded.Application.Status = ApplicationStatus.Approved;
        seeded.Screening.Decision = ScreeningDecision.Accept;
        seeded.Screening.ConsumerReportUsedForDecision = false;
        seeded.Screening.DecisionRecordedAtUtc = seeded.Now;
        await _db.SaveChangesAsync();

        var command = new CreateAdverseActionNoticeCommand(
            PortfolioId,
            seeded.Application.Id,
            prepared.Value.ScreeningId,
            prepared.Value.DecisionRecordedAtUtc!.Value,
            prepared.Value.DecisionFingerprint!,
            _scope.UserId,
            _scope.SessionId,
            _scope.AccessContextId,
            _scope.AccessRevision,
            prepared.Value.Reason!,
            prepared.Value.CreditReportingAgencyBlock!,
            pending.Id,
            "adverse-action-pdf",
            PendingFileUploadStore.ComputeOperationKeyHash("integration-finalize"),
            "integration-pdf-fingerprint",
            pending.StoragePath,
            prepared.Value.FileName!,
            "application/pdf",
            8,
            false,
            "integration-delivery",
            prepared.Value.GeneratedAtUtc);

        var finalizeHandler = new CreateAdverseActionNoticeRule(provider.GetRequiredService<RentalCommandDbContext>());
        var act = () => writes.ExecuteAsync("integration-finalize",
            ScreeningWriteSupport.Write(command, finalizeHandler.ExecuteAsync,
                finalizeHandler.AuthorizeAsync));

        var error = await act.Should().ThrowAsync<InvalidOperationException>();
        error.Which.Message.Should().Contain("decision");
        (await _db.PendingFileUploads.AsNoTracking().SingleAsync(upload => upload.Id == pending.Id))
            .State.Should().Be(PendingFileUploadState.Prepared);
        (await _db.Set<AdverseActionNotice>().CountAsync(notice => notice.ApplicationId == seeded.Application.Id))
            .Should().Be(0);
    }

    [Fact]
    public async Task ReopeningInspection_ClearsCompletionArtifactsInPostgreSql()
    {
        var now = DateTime.UtcNow;
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Integration property",
            AddressLine1 = "1 Main Street",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var report = new StoredFile
        {
            PortfolioId = PortfolioId,
            FileName = "old-inspection.pdf",
            FilePath = "old-inspection.pdf",
            ContentType = "application/pdf",
            FileSize = 8,
            EntityType = nameof(Inspection),
            UploadedAt = now.AddMinutes(-2),
        };
        var inspection = new Inspection
        {
            PortfolioId = PortfolioId,
            Property = property,
            Status = InspectionStatus.Completed,
            ScheduledFor = now.AddDays(-1),
            CompletedAt = now.AddMinutes(-1),
            CreatedAt = now.AddDays(-1),
            UpdatedAt = now.AddMinutes(-1),
        };
        _db.AddRange(property, report, inspection);
        await _db.SaveChangesAsync();
        inspection.ReportStoredFileId = report.Id;
        await _db.SaveChangesAsync();

        var command = AtomicInspectionMutation.Command(
            _scope,
            AtomicInspectionMutationDomain.Inspection,
            AtomicInspectionMutationOperation.Update,
            inspection.Id,
            0,
            "integration-inspection-reopen",
            new UpdateInspectionRequest { Status = InspectionStatus.Scheduled },
            now);
        await using var serviceScope = _services.CreateAsyncScope();
        var result = await serviceScope.ServiceProvider.GetRequiredService<IWriteExecutor>().ExecuteAsync(
            AtomicInspectionMutation.Identity(command).IdempotencyKey,
            AtomicInspectionMutation.Write(
                serviceScope.ServiceProvider.GetRequiredService<RentalCommandDbContext>(), command));

        result.Value.Applied.Should().BeTrue();
        var reopened = await _db.Inspections.AsNoTracking().SingleAsync(entity => entity.Id == inspection.Id);
        reopened.Status.Should().Be(InspectionStatus.Scheduled);
        reopened.CompletedAt.Should().BeNull();
        reopened.ReportStoredFileId.Should().BeNull();
        (await _db.StoredFiles.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(file => file.Id == report.Id))
            .DeletedAt.Should().NotBeNull();
    }

    private async Task<(RentalApplication Application, ApplicantScreening Screening, DateTime Now)>
        SeedDeclinedCaseAsync()
    {
        var now = DateTime.UtcNow;
        var application = new RentalApplication
        {
            PortfolioId = PortfolioId,
            FirstName = "Integration",
            LastName = "Decline",
            Email = "integration-decline@example.test",
            ConsentGiven = true,
            ConsentAtUtc = now.AddHours(-1),
            Status = ApplicationStatus.Declined,
            DecisionReason = "Income did not meet the stated requirement.",
            SubmittedAtUtc = now.AddHours(-2),
            ReviewedAtUtc = now.AddMinutes(-30),
            CreatedAt = now.AddHours(-2),
            UpdatedAt = now.AddMinutes(-30),
        };
        var screening = new ApplicantScreening
        {
            PortfolioId = PortfolioId,
            Application = application,
            Mode = ScreeningMode.External,
            Status = ApplicantScreeningStatus.Completed,
            ProviderDisplayName = "Manual checker",
            OperationKey = "integration-screening",
            ConsentConfirmed = true,
            ConsentAtUtc = application.ConsentAtUtc,
            CompletedAtUtc = now.AddHours(-1),
            LastStatusAtUtc = now.AddHours(-1),
            Decision = ScreeningDecision.Decline,
            DecisionReason = "Income did not meet the stated requirement.",
            DecisionRecordedByUserId = _scope.UserId,
            DecisionRecordedAtUtc = now.AddMinutes(-30),
            ConsumerReportUsedForDecision = true,
            CreditReportingAgencyName = "Example Reporting",
            CreditReportingAgencyAddress = "1 Main Street, Columbus, OH 43215",
            CreditReportingAgencyPhone = "555-0100",
            CreatedByUserId = _scope.UserId,
            CreatedAt = now.AddHours(-1),
            UpdatedAt = now.AddMinutes(-30),
        };
        _db.Add(screening);
        await _db.SaveChangesAsync();
        return (application, screening, now);
    }

    private ServiceProvider BuildServices(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddPendingFileUploadStore();
        services.AddDbContext<RentalCommandDbContext>((provider, options) =>
            options.UseNpgsql(connectionString).UseAtomicPersistenceKernel(provider));
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private async Task<WorkspaceReadScope> SeedAdministratorScopeAsync()
    {
        var now = DateTime.UtcNow;
        var email = $"pdf-finalization-{Guid.NewGuid():N}@example.test";
        var user = new ApplicationUser
        {
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = "PDF finalization administrator",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
        };
        var accessContext = new WorkspaceAccessContext
        {
            User = user,
            PortfolioId = PortfolioId,
            Status = WorkspaceAccessContextStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var membership = new WorkspaceMembership
        {
            AccessContext = accessContext,
            PortfolioId = PortfolioId,
            Status = WorkspaceMembershipStatus.Active,
            DefaultExperience = WorkspaceExperience.Management,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var assignment = new MembershipRoleAssignment
        {
            WorkspaceMembership = membership,
            PortfolioId = PortfolioId,
            RoleProfileId = AccessCatalog.Roles.Single(
                role => role.Key == RoleProfileKeys.WorkspaceAdministrator).Id,
            Status = MembershipRoleAssignmentStatus.Active,
            ScopeKind = MembershipRoleAssignmentScopeKind.AllProperties,
            EffectiveFromUtc = now.AddMinutes(-1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var session = new AuthSession
        {
            Id = Guid.NewGuid(),
            User = user,
            ActiveAccessContext = accessContext,
            Status = AuthSessionStatus.Active,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddHours(1),
        };
        _db.AddRange(assignment, session);
        await _db.SaveChangesAsync();
        return new WorkspaceReadScope(
            PortfolioId, user.Id, session.Id, accessContext.Id, accessContext.AccessRevision);
    }
}
