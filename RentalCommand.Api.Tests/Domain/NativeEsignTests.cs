using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using QuestPDF.Fluent;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Services.Esign;
using RentalCommand.Core.Constants;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// End-to-end coverage of the NATIVE e-sign flow: <see cref="NativeEsignProvider"/> creating the request +
/// per-signer tokens + outbox emails, and <see cref="NativeSigningService"/> driving the public
/// view/sign/decline endpoints — including multi-signer completion that executes the PDF, stores it with a
/// SHA-256, and updates the lease. SQLite in-memory (same pattern as LeaseEsignServiceTests).
/// </summary>
public sealed class NativeEsignTests : IDisposable
{
    private const int PortfolioId = 1;

    private readonly SqliteConnection _conn;
    private readonly RentalCommandDbContext _db;
    private readonly InMemoryFileStorage _storage = new();
    private readonly IConfiguration _config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["App:WebBaseUrl"] = "https://app.test" })
        .Build();

    public NativeEsignTests()
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>().UseSqlite(_conn).Options;
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
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public void Provider_IsConfigured_IsAlwaysTrue()
    {
        var provider = CreateProvider();
        provider.IsConfigured.Should().BeTrue("native e-sign is always available — no third-party key required");
    }

    [Fact]
    public async Task Send_CreatesRequest_Signers_Tokens_AndEnqueuesEmails()
    {
        var lease = SeedLeaseWithGraph(LeaseStatus.Draft);
        var provider = CreateProvider();

        var result = await SendAsync(provider, lease);

        result.IsConfigured.Should().BeTrue();
        result.EnvelopeId.Should().NotBeNullOrWhiteSpace();
        result.Status.Should().Be("Sent");

        var request = await _db.SignatureRequests.Include(r => r.Signers).Include(r => r.AuditEvents)
            .FirstAsync(r => r.PublicId == result.EnvelopeId);
        request.PortfolioId.Should().Be(PortfolioId);
        request.LeaseId.Should().Be(lease.Id);
        request.Status.Should().Be(SignatureRequestStatus.Sent);
        request.OriginalStoredFileId.Should().BeGreaterThan(0);

        request.Signers.Should().HaveCount(1);
        var signer = request.Signers.Single();
        signer.Token.Should().NotBeNullOrWhiteSpace();
        signer.ExpiresAtUtc.Should().BeAfter(DateTime.UtcNow);
        signer.Status.Should().Be(SignatureSignerStatus.Pending);

        request.AuditEvents.Should().ContainSingle(e => e.Type == SignatureAuditEventType.Sent);

        // A signing-link email was enqueued via the outbox, with the {webBase}/sign/{token} link.
        var outbox = await _db.OutboxMessages.SingleAsync();
        outbox.MessageType.Should().Be("email");
        outbox.Payload.Should().Contain($"https://app.test/sign/{signer.Token}");
        using var payload = JsonDocument.Parse(outbox.Payload);
        payload.RootElement.GetProperty("source").GetString().Should().Be(OutboxPayloadSources.LeaseEsignSigningLink);
        payload.RootElement.GetProperty("to").GetString().Should().Be("tenant@example.com");
        payload.RootElement.GetProperty("leaseId").GetInt32().Should().Be(lease.Id);
        payload.RootElement.GetProperty("signatureRequestId").GetInt32().Should().Be(request.Id);
    }

    [Fact]
    public async Task GetPackage_MarksViewed_AndWritesAuditEvent()
    {
        var (token, _) = await SendAndGetTokenAsync();
        var signing = CreateSigningService();

        var pkg = await signing.GetPackageAsync(token, "203.0.113.5", "Mozilla/Test", default);

        pkg.Outcome.Should().Be(SignTokenOutcome.Ok);
        pkg.Value!.SignerName.Should().Be("Marcus Williams");
        pkg.Value.ConsentDisclosure.Should().Contain("E-SIGN");
        pkg.Value.DocumentUrl.Should().Be($"/api/v1/sign/{token}/document");

        var signer = await _db.SignatureSigners.AsNoTracking().FirstAsync(s => s.Token == token);
        signer.Status.Should().Be(SignatureSignerStatus.Viewed);
        signer.ViewedAtUtc.Should().NotBeNull();
        signer.IpAddress.Should().Be("203.0.113.5");

        var viewed = await _db.SignatureAuditEvents.AsNoTracking()
            .Where(e => e.Type == SignatureAuditEventType.Viewed).ToListAsync();
        viewed.Should().ContainSingle();
        viewed[0].IpAddress.Should().Be("203.0.113.5");
        viewed[0].UserAgent.Should().Be("Mozilla/Test");
    }

    [Fact]
    public async Task Sign_SingleSigner_CapturesSignature_CompletesRequest_AndUpdatesLease()
    {
        var lease = SeedLeaseWithGraph(LeaseStatus.PendingSignature);
        var (token, envelopeId) = await SendAndGetTokenAsync(lease);
        var signing = CreateSigningService();

        var res = await signing.SignAsync(token, new SubmitSignatureRequest
        {
            Consent = true,
            SignatureType = "Typed",
            TypedName = "Marcus Williams",
        }, "198.51.100.9", "UA/sign", default);

        res.Outcome.Should().Be(SignTokenOutcome.Ok);
        res.Value!.RequestCompleted.Should().BeTrue();
        res.Value.RequestStatus.Should().Be(nameof(SignatureRequestStatus.Completed));

        // Signer captured: signature + consent + IP + timestamp.
        var signer = await _db.SignatureSigners.AsNoTracking().FirstAsync(s => s.Token == token);
        signer.Status.Should().Be(SignatureSignerStatus.Signed);
        signer.ConsentGiven.Should().BeTrue();
        signer.SignatureType.Should().Be(SignatureSignatureType.Typed);
        signer.TypedName.Should().Be("Marcus Williams");
        signer.IpAddress.Should().Be("198.51.100.9");
        signer.SignedAtUtc.Should().NotBeNull();

        // Request completed with an executed PDF + a SHA-256 hash.
        var request = await _db.SignatureRequests.AsNoTracking().FirstAsync(r => r.PublicId == envelopeId);
        request.Status.Should().Be(SignatureRequestStatus.Completed);
        request.SignedStoredFileId.Should().NotBeNull();
        request.ContentSha256.Should().NotBeNullOrWhiteSpace();
        request.ContentSha256!.Length.Should().Be(64);

        // Lease updated: signed + active + signed document attached.
        var reloaded = await _db.Leases.AsNoTracking().FirstAsync(l => l.Id == lease.Id);
        reloaded.EsignStatus.Should().Be(EsignStatus.Signed);
        reloaded.Status.Should().Be(LeaseStatus.Active);
        reloaded.SignedDocumentStoredFileId.Should().NotBeNull();

        // The Completed audit event records the hash.
        (await _db.SignatureAuditEvents.AsNoTracking()
            .AnyAsync(e => e.Type == SignatureAuditEventType.Completed)).Should().BeTrue();
    }

    [Fact]
    public async Task Sign_MultiSigner_OnlyCompletesAfterLastSigner()
    {
        var lease = SeedLeaseWithGraph(LeaseStatus.PendingSignature);
        var provider = CreateProvider();
        var sendResult = await SendAsync(provider, lease, extraSigner: ("Dana Lee", "dana@example.com"));
        var envelopeId = sendResult.EnvelopeId!;

        var signers = await _db.SignatureSigners.AsNoTracking()
            .Where(s => s.SignatureRequest!.PublicId == envelopeId).OrderBy(s => s.Id).ToListAsync();
        signers.Should().HaveCount(2);

        var signing = CreateSigningService();

        // First signer signs → PartiallySigned, lease NOT yet active.
        var first = await signing.SignAsync(signers[0].Token, new SubmitSignatureRequest
        {
            Consent = true,
            SignatureType = "Typed",
            TypedName = "Marcus Williams",
        }, "10.0.0.1", "UA1", default);

        first.Value!.RequestCompleted.Should().BeFalse();
        first.Value.RequestStatus.Should().Be(nameof(SignatureRequestStatus.PartiallySigned));

        var midLease = await _db.Leases.AsNoTracking().FirstAsync(l => l.Id == lease.Id);
        midLease.EsignStatus.Should().NotBe(EsignStatus.Signed);
        midLease.Status.Should().Be(LeaseStatus.PendingSignature);

        // Second (last) signer signs → Completed + lease active.
        var second = await signing.SignAsync(signers[1].Token, new SubmitSignatureRequest
        {
            Consent = true,
            SignatureType = "Typed",
            TypedName = "Dana Lee",
        }, "10.0.0.2", "UA2", default);

        second.Value!.RequestCompleted.Should().BeTrue();

        var finalLease = await _db.Leases.AsNoTracking().FirstAsync(l => l.Id == lease.Id);
        finalLease.EsignStatus.Should().Be(EsignStatus.Signed);
        finalLease.Status.Should().Be(LeaseStatus.Active);
        finalLease.SignedDocumentStoredFileId.Should().NotBeNull();
    }

    [Fact]
    public async Task Sign_TemplateBackedRequest_UsesOriginalTemplatePdf_AndStampsBothSignatures()
    {
        var lease = SeedLeaseWithGraph(LeaseStatus.PendingSignature);
        _db.DocumentTemplates.Add(new DocumentTemplate
        {
            Id = 42,
            PortfolioId = PortfolioId,
            Kind = DocumentTemplateKind.Lease,
            Status = DocumentTemplateStatus.Active,
            RenderMode = DocumentTemplateRenderMode.Overlay,
            Name = "Custom landlord lease",
            DefaultForPortfolio = true,
            Version = 7,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var provider = CreateProvider();
        var sendResult = await provider.SendForSignatureAsync(new EsignRequest
        {
            DocumentName = $"lease-{lease.Id}-agreement.pdf",
            Subject = $"Lease {lease.LeaseNumber}",
            DocumentBytes = LeaseTemplateFixturePdf(),
            Signers = new[]
            {
                new EsignSigner { Name = "Marcus Williams", Email = "tenant@example.com" },
                new EsignSigner { Name = "Owner Admin", Email = "owner-admin@example.com" },
            },
            DocumentTemplateId = 42,
            DocumentTemplateVersion = 7,
            TemplateFieldSnapshotJson = TemplateSignatureFieldSnapshotJson(),
        });

        var signers = await _db.SignatureSigners.AsNoTracking()
            .Where(s => s.SignatureRequest!.PublicId == sendResult.EnvelopeId)
            .OrderBy(s => s.Id)
            .ToListAsync();

        var signing = CreateSigningService();

        var tenant = await signing.SignAsync(signers[0].Token, new SubmitSignatureRequest
        {
            Consent = true,
            SignatureType = "Typed",
            TypedName = "Marcus Williams",
        }, "10.0.0.1", "UA-tenant", default);
        tenant.Value!.RequestCompleted.Should().BeFalse();

        var landlord = await signing.SignAsync(signers[1].Token, new SubmitSignatureRequest
        {
            Consent = true,
            SignatureType = "Typed",
            TypedName = "Owner Admin",
        }, "10.0.0.2", "UA-landlord", default);
        landlord.Value!.RequestCompleted.Should().BeTrue();

        var signedBytes = await provider.DownloadSignedDocumentAsync(sendResult.EnvelopeId!);
        signedBytes.Should().NotBeNull();

        var text = RentalCommand.Api.Scanning.PdfTextExtractor.TryExtractText(signedBytes!);
        text.Should().Contain("Custom Landlord Lease");
        text.Should().Contain("Marcus Williams");
        text.Should().Contain("Owner Admin");
        text.Should().Contain("Certificate of Completion");
        text.Should().NotContain("This Residential Lease Agreement");

        using var executedDoc = UglyToad.PdfPig.PdfDocument.Open(signedBytes!);
        var firstPageText = executedDoc.GetPage(1).Text;
        firstPageText.Replace(" ", string.Empty).Should().Contain("MarcusWilliams");
        firstPageText.Replace(" ", string.Empty).Should().Contain("OwnerAdmin");
    }

    [Fact]
    public async Task Sign_WithoutConsent_IsRejected_AndDoesNotChangeSigner()
    {
        var (token, _) = await SendAndGetTokenAsync();
        var signing = CreateSigningService();

        var res = await signing.SignAsync(token, new SubmitSignatureRequest
        {
            Consent = false,
            SignatureType = "Typed",
            TypedName = "Marcus Williams",
        }, "1.2.3.4", "UA", default);

        res.Outcome.Should().Be(SignTokenOutcome.Invalid);

        var signer = await _db.SignatureSigners.AsNoTracking().FirstAsync(s => s.Token == token);
        signer.Status.Should().NotBe(SignatureSignerStatus.Signed);
    }

    [Fact]
    public async Task Sign_TwiceWithSameToken_IsRejectedAsExpired_SingleUse()
    {
        var lease = SeedLeaseWithGraph(LeaseStatus.PendingSignature);
        var (token, _) = await SendAndGetTokenAsync(lease);
        var signing = CreateSigningService();

        var first = await signing.SignAsync(token, new SubmitSignatureRequest
        {
            Consent = true, SignatureType = "Typed", TypedName = "Marcus Williams",
        }, "1.1.1.1", "UA", default);
        first.Outcome.Should().Be(SignTokenOutcome.Ok);

        // Re-using the token after signing is rejected (410 Gone).
        var second = await signing.SignAsync(token, new SubmitSignatureRequest
        {
            Consent = true, SignatureType = "Typed", TypedName = "Marcus Williams",
        }, "1.1.1.1", "UA", default);
        second.Outcome.Should().Be(SignTokenOutcome.Expired);
    }

    [Fact]
    public async Task Decline_MarksSignerAndRequestDeclined_AndReflectsOnLease()
    {
        var lease = SeedLeaseWithGraph(LeaseStatus.PendingSignature);
        // Put the lease into the Sent e-sign state so the decline path can flip EsignStatus.
        lease.EsignStatus = EsignStatus.Sent;
        await _db.SaveChangesAsync();

        var (token, envelopeId) = await SendAndGetTokenAsync(lease);
        // Point the lease's envelope id at this request so HandleDeclinedEventAsync resolves it.
        var leaseRow = await _db.Leases.FirstAsync(l => l.Id == lease.Id);
        leaseRow.EsignEnvelopeId = envelopeId;
        leaseRow.EsignStatus = EsignStatus.Sent;
        await _db.SaveChangesAsync();

        var signing = CreateSigningService();
        var res = await signing.DeclineAsync(token, new DeclineSignatureRequest { Reason = "Need changes" }, "9.9.9.9", "UA", default);

        res.Outcome.Should().Be(SignTokenOutcome.Ok);
        res.Value!.SignerStatus.Should().Be(nameof(SignatureSignerStatus.Declined));
        res.Value.RequestStatus.Should().Be(nameof(SignatureRequestStatus.Declined));

        var signer = await _db.SignatureSigners.AsNoTracking().FirstAsync(s => s.Token == token);
        signer.Status.Should().Be(SignatureSignerStatus.Declined);

        (await _db.SignatureAuditEvents.AsNoTracking()
            .AnyAsync(e => e.Type == SignatureAuditEventType.Declined)).Should().BeTrue();

        var reloaded = await _db.Leases.AsNoTracking().FirstAsync(l => l.Id == lease.Id);
        reloaded.EsignStatus.Should().Be(EsignStatus.Declined);
    }

    [Fact]
    public async Task GetPackage_UnknownToken_ReturnsNotFound()
    {
        var signing = CreateSigningService();
        var res = await signing.GetPackageAsync("not-a-real-token", null, null, default);
        res.Outcome.Should().Be(SignTokenOutcome.NotFound);
    }

    [Fact]
    public async Task GetPackage_ExpiredPendingToken_IsRejected()
    {
        var (token, _) = await SendAndGetTokenAsync();
        var row = await _db.SignatureSigners.FirstAsync(s => s.Token == token);
        row.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await _db.SaveChangesAsync();

        var signing = CreateSigningService();
        var res = await signing.GetPackageAsync(token, "1.1.1.1", "UA", default);

        res.Outcome.Should().Be(SignTokenOutcome.Expired);
    }

    [Fact]
    public async Task GetDocument_ExpiredPendingToken_IsRejected()
    {
        var (token, _) = await SendAndGetTokenAsync();
        var row = await _db.SignatureSigners.FirstAsync(s => s.Token == token);
        row.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await _db.SaveChangesAsync();

        var signing = CreateSigningService();
        var res = await signing.GetDocumentAsync(token, default);

        res.Outcome.Should().Be(SignTokenOutcome.Expired);
    }

    [Fact]
    public async Task Sign_ExpiredToken_IsRejected()
    {
        var (token, _) = await SendAndGetTokenAsync();
        // Force the token to be expired.
        var row = await _db.SignatureSigners.FirstAsync(s => s.Token == token);
        row.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await _db.SaveChangesAsync();

        var signing = CreateSigningService();
        var res = await signing.SignAsync(token, new SubmitSignatureRequest
        {
            Consent = true, SignatureType = "Typed", TypedName = "Marcus Williams",
        }, "1.1.1.1", "UA", default);

        res.Outcome.Should().Be(SignTokenOutcome.Expired);
    }

    // -------------------------------------------------------------------------
    // Harness
    // -------------------------------------------------------------------------

    private NativeEsignProvider CreateProvider()
        => new(_db, _storage, _config, NullLogger<NativeEsignProvider>.Instance);

    private LeaseEsignService CreateLeaseEsignService(IEsignProvider provider)
    {
        var leaseService = new LeaseService(
            _db, new NoopDataUpdateService(), _storage, new LeaseAgreementPdfGenerator(),
            new AuditTrailService(_db, new RentalCommand.Data.Auditing.AuditScope()), NullLogger<LeaseService>.Instance);

        return new LeaseEsignService(
            _db, leaseService, provider, _storage, new NoopDataUpdateService(),
            new AuditTrailService(_db, new RentalCommand.Data.Auditing.AuditScope()),
            _config,
            NullLogger<LeaseEsignService>.Instance);
    }

    private NativeSigningService CreateSigningService()
    {
        var provider = CreateProvider();
        var leaseEsign = CreateLeaseEsignService(provider);
        return new NativeSigningService(
            _db, _storage, new ExecutedLeasePdfGenerator(), leaseEsign, NullLogger<NativeSigningService>.Instance);
    }

    private async Task<EsignResult> SendAsync(NativeEsignProvider provider, Lease lease, (string Name, string Email)? extraSigner = null)
    {
        var pdf = new LeaseAgreementPdfGenerator().Generate(new LeaseAgreementData
        {
            Lease = lease,
            LandlordName = "Acme Property Management LLC",
            TenantName = "Marcus Williams",
            PropertyName = "Maple Court",
            PropertyAddress = "10 Maple Ct, Columbus, OH 43215",
            UnitNumber = "2B",
            State = "OH",
        });

        var signers = new List<EsignSigner> { new() { Name = "Marcus Williams", Email = "tenant@example.com" } };
        if (extraSigner is { } extra)
        {
            signers.Add(new EsignSigner { Name = extra.Name, Email = extra.Email });
        }

        return await provider.SendForSignatureAsync(new EsignRequest
        {
            DocumentName = $"lease-{lease.Id}-agreement.pdf",
            Subject = $"Lease {lease.LeaseNumber}",
            DocumentBytes = pdf,
            Signers = signers,
        });
    }

    private async Task<(string Token, string EnvelopeId)> SendAndGetTokenAsync(Lease? lease = null)
    {
        lease ??= SeedLeaseWithGraph(LeaseStatus.Draft);
        var provider = CreateProvider();
        var result = await SendAsync(provider, lease);
        var signer = await _db.SignatureSigners.AsNoTracking()
            .FirstAsync(s => s.SignatureRequest!.PublicId == result.EnvelopeId);
        return (signer.Token, result.EnvelopeId!);
    }

    private static string TemplateSignatureFieldSnapshotJson()
        => JsonSerializer.Serialize(new[]
        {
            new
            {
                Id = 1,
                FieldKey = "lease.signature.tenant",
                Label = "Tenant signature",
                Kind = nameof(DocumentTemplateFieldKind.Signature),
                SignerRole = nameof(DocumentTemplateSignerRole.Tenant),
                PageNumber = 1,
                XPct = 0.16,
                YPct = 0.48,
                WidthPct = 0.30,
                HeightPct = 0.06,
                Required = true,
                Locked = false,
                SortOrder = 1,
            },
            new
            {
                Id = 2,
                FieldKey = "lease.signature.landlord",
                Label = "Landlord signature",
                Kind = nameof(DocumentTemplateFieldKind.Signature),
                SignerRole = nameof(DocumentTemplateSignerRole.Landlord),
                PageNumber = 1,
                XPct = 0.16,
                YPct = 0.62,
                WidthPct = 0.30,
                HeightPct = 0.06,
                Required = true,
                Locked = false,
                SortOrder = 2,
            },
            new
            {
                Id = 3,
                FieldKey = "lease.dateSigned.tenant",
                Label = "Tenant date signed",
                Kind = nameof(DocumentTemplateFieldKind.DateSigned),
                SignerRole = nameof(DocumentTemplateSignerRole.Tenant),
                PageNumber = 1,
                XPct = 0.58,
                YPct = 0.48,
                WidthPct = 0.18,
                HeightPct = 0.03,
                Required = true,
                Locked = false,
                SortOrder = 3,
            },
            new
            {
                Id = 4,
                FieldKey = "lease.dateSigned.landlord",
                Label = "Landlord date signed",
                Kind = nameof(DocumentTemplateFieldKind.DateSigned),
                SignerRole = nameof(DocumentTemplateSignerRole.Landlord),
                PageNumber = 1,
                XPct = 0.58,
                YPct = 0.62,
                WidthPct = 0.18,
                HeightPct = 0.03,
                Required = true,
                Locked = false,
                SortOrder = 4,
            },
        });

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
                    col.Item().PaddingTop(45).Text("Tenant signer");
                    col.Item().PaddingTop(70).Text("Landlord signer");
                    col.Item().PaddingTop(35).Text("This template text must survive execution.");
                });
            });
        });

        return document.GeneratePdf();
    }

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

    /// <summary>SQLite-compatible context: strips Postgres-only column types the way other suites do.</summary>
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
