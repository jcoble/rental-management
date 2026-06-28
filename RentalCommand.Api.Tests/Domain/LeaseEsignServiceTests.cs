using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using QuestPDF.Fluent;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Constants;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Tests the lease e-sign workflow: gating (a DISABLED provider returns "not configured" and never
/// changes lease state), the webhook signed-event advancing a PendingSignature lease to Active +
/// EsignStatus.Signed, and the status snapshot shape. Uses SQLite in-memory.
/// </summary>
public sealed class LeaseEsignServiceTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteConnection _conn;
    private readonly RentalCommandDbContext _db;
    private readonly InMemoryFileStorage _storage = new();

    public LeaseEsignServiceTests()
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .Options;

        _db = new EsignTestDbContext(options);
        _db.Database.EnsureCreated();

        _db.Portfolios.Add(new Portfolio
        {
            Id = PortfolioId,
            Name = "Test Portfolio",
            ManagementCompanyName = "Acme Property Management LLC",
            TimeZone = "UTC",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.OwnerEntities.Add(new OwnerEntity
        {
            PortfolioId = PortfolioId,
            Name = "Owner Admin",
            Email = "owner-admin@example.com",
            IsPrimary = true,
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
    public async Task SendForSignature_ProviderDisabled_ReturnsNotConfigured_AndDoesNotChangeLease()
    {
        var lease = SeedLeaseWithGraph(LeaseStatus.Draft);
        var sut = CreateService(new FakeEsignProvider { Configured = false });

        var result = await sut.SendForSignatureAsync(PortfolioId, lease.Id, new SendForSignatureRequest(), changedByUserId: 7, ipAddress: null);

        result.Outcome.Should().Be(SendForSignatureOutcome.NotConfigured);

        var reloaded = await _db.Leases.AsNoTracking().FirstAsync(l => l.Id == lease.Id);
        reloaded.Status.Should().Be(LeaseStatus.Draft, "a disabled provider must never change lease state");
        reloaded.EsignStatus.Should().Be(EsignStatus.None);
        reloaded.EsignEnvelopeId.Should().BeNull();
    }

    [Fact]
    public async Task SendForSignature_ProviderEnabled_SetsSent_AndPendingSignature()
    {
        var lease = SeedLeaseWithGraph(LeaseStatus.Draft);
        var provider = new FakeEsignProvider { Configured = true, EnvelopeId = "sig_abc123" };
        var sut = CreateService(provider);

        var result = await sut.SendForSignatureAsync(PortfolioId, lease.Id, new SendForSignatureRequest(), changedByUserId: 7, ipAddress: "127.0.0.1");

        result.Outcome.Should().Be(SendForSignatureOutcome.Sent);
        result.Status!.EsignStatus.Should().Be(EsignStatus.Sent);
        result.Status.LeaseStatus.Should().Be(LeaseStatus.PendingSignature);
        result.Status.EnvelopeId.Should().Be("sig_abc123");
        provider.SendCalls.Should().Be(1);

        var reloaded = await _db.Leases.AsNoTracking().FirstAsync(l => l.Id == lease.Id);
        reloaded.Status.Should().Be(LeaseStatus.PendingSignature);
        reloaded.EsignStatus.Should().Be(EsignStatus.Sent);
        reloaded.EsignEnvelopeId.Should().Be("sig_abc123");
    }

    [Fact]
    public async Task SendForSignature_ActiveUnsignedLease_SendsAndKeepsLeaseActive()
    {
        var lease = SeedLeaseWithGraph(LeaseStatus.Active);
        var provider = new FakeEsignProvider { Configured = true, EnvelopeId = "sig_active_send" };
        var sut = CreateService(provider);

        var result = await sut.SendForSignatureAsync(PortfolioId, lease.Id, new SendForSignatureRequest(), changedByUserId: 7, ipAddress: null);

        result.Outcome.Should().Be(SendForSignatureOutcome.Sent);
        result.Status!.EsignStatus.Should().Be(EsignStatus.Sent);
        result.Status.LeaseStatus.Should().Be(LeaseStatus.Active);
        result.Status.EnvelopeId.Should().Be("sig_active_send");
        provider.SendCalls.Should().Be(1);

        var reloaded = await _db.Leases.AsNoTracking().FirstAsync(l => l.Id == lease.Id);
        reloaded.Status.Should().Be(LeaseStatus.Active);
        reloaded.EsignStatus.Should().Be(EsignStatus.Sent);
        reloaded.EsignEnvelopeId.Should().Be("sig_active_send");
    }

    [Fact]
    public async Task SendForSignature_UsesLeaseTenantEmailAndIgnoresOverride()
    {
        var lease = SeedLeaseWithGraph(LeaseStatus.Draft, tenantEmail: "tenant-on-lease@example.com");
        var provider = new FakeEsignProvider { Configured = true, EnvelopeId = "sig_tenant_only" };
        var sut = CreateService(provider);

        var result = await sut.SendForSignatureAsync(
            PortfolioId,
            lease.Id,
            new SendForSignatureRequest
            {
                SignerName = "Owner Admin",
                SignerEmail = "owner-admin@example.com",
            },
            changedByUserId: 7,
            ipAddress: null);

        result.Outcome.Should().Be(SendForSignatureOutcome.Sent);
        provider.LastSigners.Should().HaveCount(1);
        provider.LastSigners[0].Name.Should().Be("Marcus Williams");
        provider.LastSigners[0].Email.Should().Be("tenant-on-lease@example.com");
    }

    [Fact]
    public async Task SendForSignature_UsesLeaseTenantMemberships_WhenPresent()
    {
        var lease = SeedLeaseWithGraph(LeaseStatus.Draft, tenantEmail: "primary@example.com");
        var coTenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Dana",
            LastName = "Lopez",
            Email = "co-tenant@example.com",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Tenants.Add(coTenant);
        await _db.SaveChangesAsync();
        _db.LeaseTenants.AddRange(
            new LeaseTenant
            {
                PortfolioId = PortfolioId,
                LeaseId = lease.Id,
                TenantId = lease.TenantId,
                IsPrimary = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            },
            new LeaseTenant
            {
                PortfolioId = PortfolioId,
                LeaseId = lease.Id,
                TenantId = coTenant.Id,
                IsPrimary = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        await _db.SaveChangesAsync();
        var provider = new FakeEsignProvider { Configured = true, EnvelopeId = "sig_memberships" };
        var sut = CreateService(provider);

        var result = await sut.SendForSignatureAsync(PortfolioId, lease.Id, new SendForSignatureRequest(), changedByUserId: 7, ipAddress: null);

        result.Outcome.Should().Be(SendForSignatureOutcome.Sent);
        provider.LastSigners.Select(s => s.Email).Should().Equal("primary@example.com", "co-tenant@example.com");
    }

    [Fact]
    public async Task SendForSignature_ActiveDefaultOverlayTemplate_SendsRenderedTemplateDocument()
    {
        var lease = SeedLeaseWithGraph(LeaseStatus.Draft);
        var template = await SeedActiveOverlayTemplateAsync(lease.PropertyId);
        var provider = new FakeEsignProvider { Configured = true, EnvelopeId = "sig_template_1" };
        var sut = CreateService(provider);

        var result = await sut.SendForSignatureAsync(PortfolioId, lease.Id, new SendForSignatureRequest(), changedByUserId: 7, ipAddress: null);

        result.Outcome.Should().Be(SendForSignatureOutcome.Sent);
        provider.LastDocumentBytes.Should().NotBeNull();

        var sentText = RentalCommand.Api.Scanning.PdfTextExtractor.TryExtractText(provider.LastDocumentBytes!);
        sentText.Should().Contain("Custom Landlord Lease");
        sentText.Should().Contain("Marcus");
        sentText.Should().Contain("Williams");
        sentText.Should().Contain("$1,450.00");
        sentText.Should().NotContain("Residential Lease Agreement");

        var reloaded = await _db.Leases.AsNoTracking().FirstAsync(l => l.Id == lease.Id);
        reloaded.DocumentTemplateId.Should().Be(template.Id);
        reloaded.DocumentTemplateVersion.Should().Be(template.Version);
    }

    [Fact]
    public async Task SendForSignature_NoticeGivenLease_ReturnsAlreadyFinalized_AndDoesNotChangeLease()
    {
        var lease = SeedLeaseWithGraph(LeaseStatus.NoticeGiven);
        lease.MoveOutDate = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);
        await _db.SaveChangesAsync();
        var provider = new FakeEsignProvider { Configured = true, EnvelopeId = "sig_notice_1" };
        var sut = CreateService(provider);

        var result = await sut.SendForSignatureAsync(PortfolioId, lease.Id, new SendForSignatureRequest(), changedByUserId: 7, ipAddress: "127.0.0.1");

        result.Outcome.Should().Be(SendForSignatureOutcome.AlreadyFinalized);
        provider.SendCalls.Should().Be(0);

        var reloaded = await _db.Leases.AsNoTracking().FirstAsync(l => l.Id == lease.Id);
        reloaded.Status.Should().Be(LeaseStatus.NoticeGiven);
        reloaded.EsignStatus.Should().Be(EsignStatus.None);
        reloaded.EsignEnvelopeId.Should().BeNull();
        reloaded.MoveOutDate.Should().Be(lease.MoveOutDate);
    }

    [Fact]
    public async Task SendForSignature_TenantHasNoEmail_ReturnsMissingSigner()
    {
        var lease = SeedLeaseWithGraph(LeaseStatus.Draft, tenantEmail: null);
        var sut = CreateService(new FakeEsignProvider { Configured = true });

        var result = await sut.SendForSignatureAsync(PortfolioId, lease.Id, new SendForSignatureRequest(), changedByUserId: 1, ipAddress: null);

        result.Outcome.Should().Be(SendForSignatureOutcome.MissingSigner);
    }

    [Fact]
    public async Task SendForSignature_LandlordHasNoEmail_StillSendsToLeaseTenant()
    {
        var owner = await _db.OwnerEntities.FirstAsync(o => o.PortfolioId == PortfolioId && o.IsPrimary);
        owner.Email = null;
        await _db.SaveChangesAsync();

        var lease = SeedLeaseWithGraph(LeaseStatus.Draft);
        var provider = new FakeEsignProvider { Configured = true, EnvelopeId = "sig_no_landlord" };
        var sut = CreateService(provider);

        var result = await sut.SendForSignatureAsync(PortfolioId, lease.Id, new SendForSignatureRequest(), changedByUserId: 1, ipAddress: null);

        result.Outcome.Should().Be(SendForSignatureOutcome.Sent);
        provider.SendCalls.Should().Be(1);
        provider.LastSigners.Should().ContainSingle();
        provider.LastSigners[0].Email.Should().Be("tenant@example.com");
    }

    [Fact]
    public async Task HandleSignedEvent_FlipsPendingSignatureLeaseToActive_AndStoresSignedDocument()
    {
        var lease = SeedLeaseWithGraph(LeaseStatus.PendingSignature);
        lease.EsignEnvelopeId = "sig_signed_1";
        lease.EsignStatus = EsignStatus.Sent;
        await _db.SaveChangesAsync();

        var provider = new FakeEsignProvider { Configured = true, SignedPdf = SamplePdf() };
        var sut = CreateService(provider);

        var advanced = await sut.HandleSignedEventAsync("sig_signed_1");

        advanced.Should().BeTrue();

        var reloaded = await _db.Leases.AsNoTracking().FirstAsync(l => l.Id == lease.Id);
        reloaded.EsignStatus.Should().Be(EsignStatus.Signed);
        reloaded.Status.Should().Be(LeaseStatus.Active, "a signed PendingSignature lease becomes Active");
        reloaded.SignedDocumentStoredFileId.Should().NotBeNull();

        var stored = await _db.StoredFiles.AsNoTracking().FirstAsync(f => f.Id == reloaded.SignedDocumentStoredFileId!.Value);
        stored.EntityType.Should().Be("Lease");
        stored.EntityId.Should().Be(lease.Id);
        stored.ContentType.Should().Be("application/pdf");

        // The signed document is now streamable.
        var doc = await sut.GetSignedDocumentAsync(PortfolioId, lease.Id);
        doc.Should().NotBeNull();
        doc!.Value.ContentType.Should().Be("application/pdf");
    }

    [Fact]
    public async Task HandleSignedEvent_UnknownEnvelope_IsNoOp()
    {
        var sut = CreateService(new FakeEsignProvider { Configured = true });

        var advanced = await sut.HandleSignedEventAsync("sig_does_not_exist");

        advanced.Should().BeFalse();
    }

    [Fact]
    public async Task HandleSignedEvent_OnNonPendingLease_SetsSignedButDoesNotChangeLeaseStatus()
    {
        // A lease that is already Active (e.g. signed out-of-band) should record the signature but its
        // overall status is only flipped FROM PendingSignature.
        var lease = SeedLeaseWithGraph(LeaseStatus.Active);
        lease.EsignEnvelopeId = "sig_active_1";
        lease.EsignStatus = EsignStatus.Sent;
        await _db.SaveChangesAsync();

        var sut = CreateService(new FakeEsignProvider { Configured = true, SignedPdf = SamplePdf() });

        await sut.HandleSignedEventAsync("sig_active_1");

        var reloaded = await _db.Leases.AsNoTracking().FirstAsync(l => l.Id == lease.Id);
        reloaded.EsignStatus.Should().Be(EsignStatus.Signed);
        reloaded.Status.Should().Be(LeaseStatus.Active);
    }

    [Fact]
    public async Task GetSignatureStatus_ReturnsExpectedShape()
    {
        var lease = SeedLeaseWithGraph(LeaseStatus.PendingSignature);
        lease.EsignEnvelopeId = "sig_status_1";
        lease.EsignStatus = EsignStatus.Sent;
        await _db.SaveChangesAsync();

        // Disabled provider so the status read returns the stored snapshot without a remote refresh.
        var sut = CreateService(new FakeEsignProvider { Configured = false });

        var status = await sut.GetSignatureStatusAsync(PortfolioId, lease.Id);

        status.Should().NotBeNull();
        status!.LeaseId.Should().Be(lease.Id);
        status.EsignStatus.Should().Be(EsignStatus.Sent);
        status.LeaseStatus.Should().Be(LeaseStatus.PendingSignature);
        status.EnvelopeId.Should().Be("sig_status_1");
        status.HasSignedDocument.Should().BeFalse();
    }

    [Fact]
    public async Task GetSignatureStatus_LeaseNotInPortfolio_ReturnsNull()
    {
        var sut = CreateService(new FakeEsignProvider { Configured = false });

        var status = await sut.GetSignatureStatusAsync(PortfolioId, leaseId: 99999);

        status.Should().BeNull();
    }

    [Fact]
    public async Task GetSignatureQueue_ReturnsRecentLeaseSigningEmailsWithRecipientAndSendTime()
    {
        var lease = SeedLeaseWithGraph(LeaseStatus.PendingSignature, tenantEmail: "tenant-on-lease@example.com");
        var otherLease = SeedLeaseWithGraph(LeaseStatus.PendingSignature, tenantEmail: "other-tenant@example.com");
        var now = new DateTime(2026, 6, 28, 14, 0, 0, DateTimeKind.Utc);
        var originalFile = new StoredFile
        {
            PortfolioId = PortfolioId,
            FileName = "lease.pdf",
            FilePath = "leases/lease.pdf",
            ContentType = "application/pdf",
            FileSize = 100,
            EntityType = "Lease",
            EntityId = lease.Id,
            UploadedAt = now,
        };
        _db.StoredFiles.Add(originalFile);
        await _db.SaveChangesAsync();
        var currentRequest = new SignatureRequest
        {
            PortfolioId = PortfolioId,
            LeaseId = lease.Id,
            PublicId = "current-envelope",
            DocumentName = "lease-current.pdf",
            Subject = "Lease L-2026-7",
            OriginalStoredFileId = originalFile.Id,
            CreatedAtUtc = now,
        };
        var oldRequest = new SignatureRequest
        {
            PortfolioId = PortfolioId,
            LeaseId = lease.Id,
            PublicId = "old-envelope",
            DocumentName = "lease-old.pdf",
            Subject = "Lease L-2026-7",
            OriginalStoredFileId = originalFile.Id,
            CreatedAtUtc = now.AddMinutes(-30),
        };
        var otherLeaseRequest = new SignatureRequest
        {
            PortfolioId = PortfolioId,
            LeaseId = otherLease.Id,
            PublicId = "other-envelope",
            DocumentName = "lease-other.pdf",
            Subject = "Lease L-2026-7",
            OriginalStoredFileId = originalFile.Id,
            CreatedAtUtc = now,
        };
        _db.SignatureRequests.AddRange(currentRequest, oldRequest, otherLeaseRequest);
        lease.EsignEnvelopeId = currentRequest.PublicId;
        await _db.SaveChangesAsync();

        _db.OutboxMessages.AddRange(
            new OutboxMessage
            {
                PortfolioId = PortfolioId,
                MessageType = "email",
                Payload = JsonSerializer.Serialize(new
                {
                    source = OutboxPayloadSources.LeaseEsignSigningLink,
                    signatureRequestId = currentRequest.Id,
                    leaseId = lease.Id,
                    to = "tenant-on-lease@example.com",
                    subject = "Lease L-2026-7",
                }),
                CreatedAt = now.AddMinutes(-10),
                SentAt = now.AddMinutes(-9),
            },
            new OutboxMessage
            {
                PortfolioId = PortfolioId,
                MessageType = "email",
                Payload = JsonSerializer.Serialize(new
                {
                    source = OutboxPayloadSources.LeaseEsignSigningLink,
                    signatureRequestId = currentRequest.Id,
                    leaseId = lease.Id,
                    to = "tenant-on-lease@example.com",
                    subject = "Lease L-2026-7",
                }),
                CreatedAt = now.AddMinutes(-4),
                FailedAt = now.AddMinutes(-2),
                RetryCount = 5,
                Error = "SMTP rejected the message.",
            },
            new OutboxMessage
            {
                PortfolioId = PortfolioId,
                MessageType = "email",
                Payload = JsonSerializer.Serialize(new
                {
                    source = OutboxPayloadSources.LeaseEsignSigningLink,
                    signatureRequestId = currentRequest.Id,
                    leaseId = lease.Id,
                    to = "tenant-on-lease@example.com",
                    subject = "Lease L-2026-7",
                }),
                CreatedAt = now.AddMinutes(-6),
                SentAt = now.AddMinutes(-5),
                Error = "Email delivery is not configured for this environment.",
            },
            new OutboxMessage
            {
                PortfolioId = PortfolioId,
                MessageType = "email",
                Payload = JsonSerializer.Serialize(new
                {
                    source = OutboxPayloadSources.LeaseEsignSigningLink,
                    signatureRequestId = otherLeaseRequest.Id,
                    leaseId = otherLease.Id,
                    to = "other-tenant@example.com",
                    subject = "Lease L-2026-7",
                }),
                CreatedAt = now.AddMinutes(-1),
                SentAt = now,
            },
            new OutboxMessage
            {
                PortfolioId = PortfolioId,
                MessageType = "email",
                Payload = JsonSerializer.Serialize(new
                {
                    source = OutboxPayloadSources.LeaseEsignSigningLink,
                    signatureRequestId = currentRequest.Id,
                    leaseId = int.Parse($"{lease.Id}0"),
                    to = "wrong-tenant@example.com",
                    subject = "Lease collision",
                }),
                CreatedAt = now.AddMinutes(-3),
                SentAt = now.AddMinutes(-2),
            },
            new OutboxMessage
            {
                PortfolioId = PortfolioId,
                MessageType = "email",
                Payload = JsonSerializer.Serialize(new
                {
                    source = OutboxPayloadSources.LeaseEsignSigningLink,
                    signatureRequestId = oldRequest.Id,
                    leaseId = lease.Id,
                    to = "old-recipient@example.com",
                    subject = "Old signing request",
                }),
                CreatedAt = now.AddMinutes(-1),
                SentAt = now,
            },
            new OutboxMessage
            {
                PortfolioId = PortfolioId,
                MessageType = "email",
                Payload = JsonSerializer.Serialize(new
                {
                    to = "owner@example.com",
                    subject = "Unrelated email",
                }),
                CreatedAt = now,
                SentAt = now,
            });
        await _db.SaveChangesAsync();

        var sut = CreateService(new FakeEsignProvider { Configured = false });

        var queue = await sut.GetSignatureQueueAsync(PortfolioId, lease.Id);

        queue.Should().NotBeNull();
        queue!.LeaseId.Should().Be(lease.Id);
        queue.Items.Should().HaveCount(3);
        queue.Items.Select(i => i.RecipientEmail).Should().Equal(
            "tenant-on-lease@example.com",
            "tenant-on-lease@example.com",
            "tenant-on-lease@example.com");
        queue.Items.Select(i => i.Status).Should().Equal("Failed", "DeliveryDisabled", "Sent");
        queue.Items.Select(i => i.StatusAt).Should().Equal(now.AddMinutes(-2), now.AddMinutes(-5), now.AddMinutes(-9));
        queue.Items[0].Error.Should().Be("SMTP rejected the message.");
        queue.Items[1].Error.Should().Be("Email delivery is not configured for this environment.");
    }

    private LeaseEsignService CreateService(IEsignProvider provider)
    {
        var leaseService = new LeaseService(
            _db,
            new NoopDataUpdateService(),
            _storage,
            new LeaseAgreementPdfGenerator(),
            new AuditTrailService(_db, new RentalCommand.Data.Auditing.AuditScope()),
            NullLogger<LeaseService>.Instance,
            new LeaseAgreementRenderer(
                _db,
                _storage,
                new LeaseAgreementPdfGenerator(),
                NullLogger<LeaseAgreementRenderer>.Instance));

        return new LeaseEsignService(
            _db,
            leaseService,
            provider,
            _storage,
            new NoopDataUpdateService(),
            new AuditTrailService(_db, new RentalCommand.Data.Auditing.AuditScope()),
            NullLogger<LeaseEsignService>.Instance);
    }

    private static byte[] SamplePdf()
        // A minimal but valid-enough PDF byte sequence for storage/streaming round-trips.
        => System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n%signed-test\n");

    private Lease SeedLeaseWithGraph(LeaseStatus status, string? tenantEmail = "tenant@example.com")
    {
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Maple Court",
            AddressLine1 = "10 Maple Ct",
            City = "Columbus",
            State = "OH",
            PostalCode = "43215",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Properties.Add(property);
        _db.SaveChanges();

        var unit = new Unit
        {
            PropertyId = property.Id,
            UnitNumber = "2B",
            Bedrooms = 2,
            Bathrooms = 1,
            MarketRent = 1450m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Units.Add(unit);

        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Marcus",
            LastName = "Williams",
            Email = tenantEmail,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Tenants.Add(tenant);
        _db.SaveChanges();

        var lease = new Lease
        {
            PortfolioId = PortfolioId,
            PropertyId = property.Id,
            UnitId = unit.Id,
            TenantId = tenant.Id,
            LeaseNumber = "L-2026-7",
            Status = status,
            StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent = 1450m,
            SecurityDeposit = 1450m,
            LateFeeAmount = 75m,
            RentDueDay = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Leases.Add(lease);
        _db.SaveChanges();
        return lease;
    }

    private async Task<DocumentTemplate> SeedActiveOverlayTemplateAsync(int propertyId)
    {
        var pdfBytes = LeaseTemplateFixturePdf();
        var storageKey = await _storage.UploadAsync(
            new MemoryStream(pdfBytes),
            "custom-landlord-lease.pdf",
            "application/pdf");

        var stored = new StoredFile
        {
            PortfolioId = PortfolioId,
            FileName = "custom-landlord-lease.pdf",
            FilePath = storageKey,
            ContentType = "application/pdf",
            FileSize = pdfBytes.Length,
            EntityType = "DocumentTemplate",
            UploadedAt = DateTime.UtcNow,
        };

        var template = new DocumentTemplate
        {
            PortfolioId = PortfolioId,
            Kind = DocumentTemplateKind.Lease,
            Status = DocumentTemplateStatus.Active,
            RenderMode = DocumentTemplateRenderMode.Overlay,
            Name = "Custom landlord lease",
            OriginalStoredFile = stored,
            DefaultForPortfolio = true,
            PropertyId = propertyId,
            Version = 7,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };

        template.Fields.Add(new DocumentTemplateField
        {
            FieldKey = "tenant.fullName",
            Label = "Tenant full name",
            Kind = DocumentTemplateFieldKind.Text,
            PageNumber = 1,
            XPct = 0.18,
            YPct = 0.34,
            WidthPct = 0.62,
            HeightPct = 0.04,
            SortOrder = 1,
        });
        template.Fields.Add(new DocumentTemplateField
        {
            FieldKey = "lease.monthlyRent",
            Label = "Monthly rent",
            Kind = DocumentTemplateFieldKind.Currency,
            PageNumber = 1,
            XPct = 0.18,
            YPct = 0.42,
            WidthPct = 0.24,
            HeightPct = 0.04,
            SortOrder = 2,
        });

        _db.DocumentTemplates.Add(template);
        await _db.SaveChangesAsync();

        stored.EntityId = template.Id;
        await _db.SaveChangesAsync();
        return template;
    }

    private static byte[] LeaseTemplateFixturePdf()
    {
        var document = QuestPDF.Fluent.Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(QuestPDF.Helpers.PageSizes.A4);
                page.Margin(40);
                page.DefaultTextStyle(t => t.FontSize(12));
                page.Content().Column(col =>
                {
                    col.Item().Text("Custom Landlord Lease").FontSize(18).Bold();
                    col.Item().PaddingTop(60).Text("Tenant:");
                    col.Item().PaddingTop(30).Text("Monthly rent:");
                });
            });
        });

        return document.GeneratePdf();
    }

    /// <summary>Configurable fake provider — gating, send, status, and signed-file download are all controllable.</summary>
    private sealed class FakeEsignProvider : IEsignProvider
    {
        public bool Configured { get; init; }
        public string EnvelopeId { get; init; } = "sig_fake";
        public byte[]? SignedPdf { get; init; }
        public byte[]? LastDocumentBytes { get; private set; }
        public IReadOnlyList<EsignSigner> LastSigners { get; private set; } = Array.Empty<EsignSigner>();
        public int SendCalls { get; private set; }

        public bool IsConfigured => Configured;

        public Task<EsignResult> SendForSignatureAsync(EsignRequest request, CancellationToken ct = default)
        {
            SendCalls++;
            LastDocumentBytes = request.DocumentBytes;
            LastSigners = request.Signers
                .Select(s => new EsignSigner { Name = s.Name, Email = s.Email })
                .ToArray();
            return Task.FromResult(Configured
                ? EsignResult.Sent(EnvelopeId, "Sent")
                : EsignResult.NotConfigured());
        }

        public Task<EsignResult> GetStatusAsync(string envelopeId, CancellationToken ct = default)
            => Task.FromResult(Configured
                ? new EsignResult { EnvelopeId = envelopeId, Status = "Sent" }
                : EsignResult.NotConfigured());

        public Task<byte[]?> DownloadSignedDocumentAsync(string envelopeId, CancellationToken ct = default)
            => Task.FromResult(Configured ? SignedPdf : null);
    }

    private sealed class NoopDataUpdateService : IDataUpdateService
    {
        public Task BroadcastEntityUpdateAsync(int portfolioId, string entityType, int entityId, object data, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task BroadcastEntityDeleteAsync(int portfolioId, string entityType, int entityId, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class InMemoryFileStorage : IFileStorage
    {
        private readonly Dictionary<string, byte[]> _files = new();

        public async Task<string> UploadAsync(Stream content, string fileName, string contentType, CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            var key = $"{Guid.NewGuid():N}_{fileName}";
            _files[key] = ms.ToArray();
            return key;
        }

        public Task<Stream> DownloadAsync(string path, CancellationToken ct = default)
        {
            if (!_files.TryGetValue(path, out var bytes))
                throw new FileNotFoundException(path);
            return Task.FromResult<Stream>(new MemoryStream(bytes));
        }

        public Task DeleteAsync(string path, CancellationToken ct = default)
        {
            _files.Remove(path);
            return Task.CompletedTask;
        }
    }

    /// <summary>SQLite-compatible context: strips Postgres-only DDL the same way other suites do.</summary>
    private sealed class EsignTestDbContext : RentalCommandDbContext
    {
        public EsignTestDbContext(DbContextOptions<RentalCommandDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<ScanDraft>().Property(e => e.ExtractedFields).HasColumnType("TEXT");
            modelBuilder.Entity<AuditLog>().Property(e => e.OldValues).HasColumnType("TEXT");
            modelBuilder.Entity<AuditLog>().Property(e => e.NewValues).HasColumnType("TEXT");
            modelBuilder.Entity<OutboxMessage>().Property(e => e.Payload).HasColumnType("TEXT");
            modelBuilder.Entity<QueuedJob>().Property(e => e.Payload).HasColumnType("TEXT");
            modelBuilder.Entity<Lease>().ToTable("Leases");
        }
    }
}
