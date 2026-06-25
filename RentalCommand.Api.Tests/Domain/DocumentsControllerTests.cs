using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.TestCommon;
using SkiaSharp;

namespace RentalCommand.Api.Tests.Domain;

public sealed class DocumentsControllerTests : IDisposable
{
    private const int PortfolioId = 1;

    private static readonly byte[] TestPng = BuildTestPng();

    private readonly SqliteTestContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task GetFile_WithThumbForImage_ReturnsJpegThumbnail()
    {
        var documents = new Mock<IDocumentService>();
        documents
            .Setup(d => d.FindAsync(PortfolioId, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredFile
            {
                Id = 7,
                PortfolioId = PortfolioId,
                EntityType = "Unit",
                EntityId = 3,
                FileName = "inspection.png",
                FilePath = "stored/inspection.png",
                ContentType = "image/png",
                FileSize = TestPng.Length,
                UploadedAt = DateTime.UtcNow,
            });

        var storage = new Mock<IFileStorage>();
        storage
            .Setup(s => s.DownloadAsync("stored/inspection.png", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(TestPng));

        var controller = CreateController(documents.Object, storage.Object);

        var result = await controller.GetFile(7, thumb: true, CancellationToken.None);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be("image/jpeg");
        file.FileContents.Should().NotBeEquivalentTo(TestPng);
        controller.Response.Headers.ContentDisposition.ToString().Should().Be("inline; filename=\"inspection-thumb.jpg\"");
    }

    private static byte[] BuildTestPng()
    {
        using var bitmap = new SKBitmap(4, 4);
        bitmap.Erase(SKColors.Teal);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private DocumentsController CreateController(IDocumentService documents, IFileStorage storage)
    {
        var controller = new DocumentsController(
            documents,
            storage,
            _ctx.Db,
            Options.Create(new UploadSettings
            {
                MaxFileSizeBytes = 52_428_800,
                AllowedMimeTypes = ["image/jpeg", "image/png", "application/pdf"],
            }),
            NullLogger<DocumentsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim("portfolioId", PortfolioId.ToString()),
                        new Claim(ClaimTypes.NameIdentifier, "7"),
                    ], "test")),
                },
            },
        };

        return controller;
    }
}
