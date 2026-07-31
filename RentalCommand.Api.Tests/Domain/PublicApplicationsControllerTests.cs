using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using RentalCommand.Api.Controllers;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

/// <summary>
/// Controller-level tests for the public, no-login application endpoints. Verifies a good token
/// submits into the token's portfolio (201) and a bad token 404s, using the real service over an
/// in-memory DB with a no-op LLM provider (no key → empty extraction).
/// </summary>
public class PublicApplicationsControllerTests : IDisposable
{
    private const int PortfolioId = 1;
    private const string Token = "share-token-xyz";

    private readonly SqliteConnection _conn;
    private readonly RentalCommandDbContext _db;
    private readonly PublicApplicationsController _controller;

    public PublicApplicationsControllerTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        var options = new DbContextOptionsBuilder<RentalCommandDbContext>()
            .UseSqlite(_conn)
            .Options;

        _db = new ApplicationTestDbContext(options);
        _db.Database.EnsureCreated();

        _db.Portfolios.Add(new Portfolio
        {
            Id = PortfolioId,
            Name = "Frank's Rentals",
            ManagementCompanyName = "Frank Property Management",
            TimeZone = "UTC",
            PublicApplicationToken = Token,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        var service = new ApplicationService(
            _db,
            Mock.Of<IFileStorage>(),
            Mock.Of<IDataUpdateService>(),
            Mock.Of<IAuditTrailService>(),
            TimeProvider.System,
            new PublicSubmissionAtomicUnitOfWork(_db));
        var uploadSettings = Options.Create(new UploadSettings
        {
            MaxFileSizeBytes = 10_000_000,
            AllowedMimeTypes = ["image/jpeg", "image/png", "application/pdf"],
        });

        _controller = new PublicApplicationsController(
            service, new NoKeyLlmProvider(), uploadSettings,
            NullLogger<PublicApplicationsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };
        _controller.HttpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("198.51.100.4");
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task Submit_GoodToken_Creates201AndPersistsInPortfolio()
    {
        var request = new SubmitApplicationRequest
        {
            FirstName = "Dana",
            LastName = "Lopez",
            ConsentGiven = true,
        };

        var result = await _controller.Submit(Token, request, Guid.NewGuid().ToString("N"), CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status201Created);
        var body = obj.Value.Should().BeOfType<SubmitApplicationResult>().Subject;
        body.Status.Should().Be("Submitted");

        var saved = await _db.RentalApplications.SingleAsync();
        saved.PortfolioId.Should().Be(PortfolioId);
        saved.ConsentIpAddress.Should().Be("198.51.100.4");
    }

    [Fact]
    public async Task Submit_BadToken_Returns404()
    {
        var request = new SubmitApplicationRequest
        {
            FirstName = "X",
            LastName = "Y",
            ConsentGiven = true,
        };

        var result = await _controller.Submit("wrong-token", request, Guid.NewGuid().ToString("N"), CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
        (await _db.RentalApplications.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Submit_WithoutConsent_Returns400()
    {
        var request = new SubmitApplicationRequest
        {
            FirstName = "No",
            LastName = "Consent",
            ConsentGiven = false,
        };

        var result = await _controller.Submit(Token, request, Guid.NewGuid().ToString("N"), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        (await _db.RentalApplications.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task GetForm_BadToken_Returns404()
    {
        var result = await _controller.GetForm("nope", CancellationToken.None);
        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    /// <summary>Stands in for an unconfigured LLM provider: returns all-empty, zero-confidence fields.</summary>
    private sealed class NoKeyLlmProvider : ILlmProvider
    {
        public Task<string> ChatAsync(string prompt, CancellationToken ct = default)
            => Task.FromResult(string.Empty);

        public Task<ExtractedFields> ExtractAsync(
            byte[] documentBytes, string contentType, string instructions,
            IReadOnlyList<ExtractionFieldSpec> fields, string? groundingContext = null,
            CancellationToken ct = default)
            => Task.FromResult(new ExtractedFields
            {
                ModelId = "noop",
                Fields = fields.ToDictionary(f => f.Name, _ => new FieldExtraction { Value = "", Confidence = 0m }),
            });

        public Task<LlmToolResult> ChatWithToolsAsync(
            string systemPrompt, IReadOnlyList<LlmChatMessage> messages,
            IReadOnlyList<LlmToolSpec> tools, CancellationToken ct = default)
            => Task.FromResult(new LlmToolResult("noop", null, [], 0, 0, "noop"));
    }

    private sealed class PublicSubmissionAtomicUnitOfWork(RentalCommandDbContext db) : IAtomicUnitOfWork
    {
        public async Task<AtomicCommandOutcome<TResult>> ExecuteAsync<TCommand, TResult>(
            AtomicCommandIdentity identity,
            TCommand command,
            AtomicJsonResultCodec<TResult> resultCodec,
            CancellationToken ct = default)
            where TCommand : notnull, IAtomicCommandData
            where TResult : notnull
        {
            if (command is not AtomicPublicApplicationSubmissionCommand submit
                || typeof(TResult) != typeof(AtomicPublicApplicationSubmissionResult))
                throw new InvalidOperationException("Unexpected atomic command in public application controller test.");

            var portfolioId = await db.Portfolios
                .Where(portfolio => portfolio.PublicApplicationToken == submit.Token)
                .Select(portfolio => (int?)portfolio.Id)
                .SingleOrDefaultAsync(ct);
            if (portfolioId is null)
            {
                return (AtomicCommandOutcome<TResult>)(object)
                    new AtomicCommandOutcome<AtomicPublicApplicationSubmissionResult>(
                        new(false, 0, string.Empty, string.Empty),
                        AtomicCommandDisposition.Executed,
                        Guid.NewGuid());
            }

            var request = System.Text.Json.JsonSerializer
                .Deserialize<SubmitApplicationRequest>(submit.RequestJson)!;
            var now = DateTime.UtcNow;
            var application = new RentalApplication
            {
                PortfolioId = portfolioId.Value,
                FirstName = request.FirstName,
                LastName = request.LastName,
                ConsentGiven = request.ConsentGiven,
                ConsentAtUtc = now,
                ConsentIpAddress = submit.IpAddress,
                Status = RentalCommand.Core.Enums.ApplicationStatus.Submitted,
                SubmittedAtUtc = now,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.RentalApplications.Add(application);
            await db.SaveChangesAsync(ct);
            return (AtomicCommandOutcome<TResult>)(object)
                new AtomicCommandOutcome<AtomicPublicApplicationSubmissionResult>(
                    new(true, application.Id, "Submitted", "Received"),
                    AtomicCommandDisposition.Executed,
                    Guid.NewGuid());
        }
    }
}
