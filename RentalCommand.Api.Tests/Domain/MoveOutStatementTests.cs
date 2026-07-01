using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Covers the photo-backed security-deposit move-out statement PDF: returns a non-empty %PDF with the
/// deposit and deductions reflected, embeds an attached photo, and 404s (null) for an out-of-scope id.
/// </summary>
public class MoveOutStatementTests : IDisposable
{
    private const int PortfolioId = 1;

    // Minimal valid 1×1 transparent PNG, used so QuestPDF's image decode succeeds.
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private readonly SqliteTestContext _ctx = new();
    private readonly InMemoryFileStorage _storage = new();

    public MoveOutStatementTests()
    {
        // QuestPDF refuses to render without a license; tests don't run the API's Program.cs.
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
    }

    public void Dispose() => _ctx.Dispose();

    private SecurityDepositService CreateSut() => new(
        _ctx.Db,
        _storage,
        new MoveOutStatementPdfGenerator(),
        Mock.Of<RentalCommand.Core.Interfaces.IAuditTrailService>(),
        Mock.Of<RentalCommand.Core.Interfaces.ICurrentActor>(),
        Mock.Of<ILogger<SecurityDepositService>>(),
        TimeProvider.System);

    [Fact]
    public async Task MoveOutStatement_ReturnsPdf_WithDepositAndDeductions()
    {
        var (deposit, _) = SeedFullChain();
        // Two itemised deductions on the deposit.
        deposit.DeductionsJson = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new DepositDeduction("Carpet cleaning", 150m, "Pet stains in the living room"),
            new DepositDeduction("Unpaid last month rent", 400m, null),
        });
        deposit.DeductionsTotal = 550m;
        await _ctx.Db.SaveChangesAsync();

        var sut = CreateSut();
        var pdf = await sut.GetMoveOutStatementAsync(PortfolioId, deposit.Id);

        pdf.Should().NotBeNull();
        pdf!.Length.Should().BeGreaterThan(0);
        // %PDF magic header.
        Encoding.ASCII.GetString(pdf, 0, 4).Should().Be("%PDF");
    }

    [Fact]
    public async Task MoveOutStatement_EmbedsAttachedPhoto()
    {
        var (deposit, _) = SeedFullChain();
        await _ctx.Db.SaveChangesAsync();

        var sut = CreateSut();
        var withoutPhoto = await sut.GetMoveOutStatementAsync(PortfolioId, deposit.Id);

        // Attach a photo to the deposit (StoredFile with EntityType=SecurityDeposit).
        var key = await _storage.UploadAsync(new MemoryStream(OnePixelPng), "move-out.png", "image/png");
        _ctx.Db.StoredFiles.Add(new StoredFile
        {
            PortfolioId = PortfolioId,
            FileName = "move-out.png",
            FilePath = key,
            ContentType = "image/png",
            FileSize = OnePixelPng.Length,
            EntityType = "SecurityDeposit",
            EntityId = deposit.Id,
            UploadedAt = DateTime.UtcNow,
        });
        await _ctx.Db.SaveChangesAsync();

        var withPhoto = await sut.GetMoveOutStatementAsync(PortfolioId, deposit.Id);

        withoutPhoto.Should().NotBeNull();
        withPhoto.Should().NotBeNull();
        Encoding.ASCII.GetString(withPhoto!, 0, 4).Should().Be("%PDF");
        // The embedded image bytes make the photo-bearing PDF larger.
        withPhoto!.Length.Should().BeGreaterThan(withoutPhoto!.Length);
    }

    [Fact]
    public async Task MoveOutStatement_ReturnsNull_ForOutOfScopeHolding()
    {
        var (deposit, _) = SeedFullChain();
        await _ctx.Db.SaveChangesAsync();

        var sut = CreateSut();

        // Different portfolio scope → null (controller maps to 404).
        var pdf = await sut.GetMoveOutStatementAsync(portfolioId: 999, deposit.Id);
        pdf.Should().BeNull();
    }

    private (SecurityDepositHolding deposit, Lease lease) SeedFullChain()
    {
        var now = DateTime.UtcNow;

        var tenant = new Tenant
        {
            PortfolioId = PortfolioId,
            FirstName = "Dana",
            LastName = "Reyes",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var property = new Property
        {
            PortfolioId = PortfolioId,
            Name = "Maple Court",
            AddressLine1 = "12 Maple Ct",
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
            UnitNumber = "2B",
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
            LeaseNumber = "L-1001",
            StartDate = now.AddYears(-1),
            EndDate = now,
            MoveOutDate = now,
            MonthlyRent = 1500m,
            SecurityDeposit = 1500m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.Leases.Add(lease);
        _ctx.Db.SaveChanges();

        var deposit = new SecurityDepositHolding
        {
            PortfolioId = PortfolioId,
            LeaseId = lease.Id,
            Amount = 1500m,
            Status = SecurityDepositStatus.Held,
            HeldAt = now.AddYears(-1),
            DeductionsJson = "[]",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ctx.Db.SecurityDepositHoldings.Add(deposit);
        _ctx.Db.SaveChanges();

        return (deposit, lease);
    }

    /// <summary>In-memory <see cref="IFileStorage"/> so statement generation works without disk.</summary>
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
}
