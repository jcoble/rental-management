using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Api.Services;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

public sealed record AtomicPublicApplicationSubmissionCommand(
    string Token,
    string RequestJson,
    string? IpAddress,
    [property: AtomicFingerprintIgnore]
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record AtomicPublicApplicationSubmissionResult(
    bool Found,
    int ApplicationId,
    string Status,
    string Message);

/// <summary>Anonymous token-scoped intake with a durable receipt, audit, and outbox.</summary>
public sealed class AtomicPublicApplicationSubmissionHandler
    : IAtomicCommandHandler<AtomicPublicApplicationSubmissionCommand, AtomicPublicApplicationSubmissionResult>
{
    private readonly RentalCommandDbContext _db;

    public AtomicPublicApplicationSubmissionHandler(RentalCommandDbContext db) => _db = db;

    public async Task<AtomicPublicApplicationSubmissionResult> HandleAsync(
        AtomicPublicApplicationSubmissionCommand command,
        IAtomicCommandContext attempt,
        CancellationToken ct)
    {
        Validate(command);
        var portfolioId = await ResolvePortfolioIdAsync(command.Token, _db, ct);
        if (portfolioId is null) return Missing();
        await attempt.AcquireLockAsync("Portfolio", portfolioId.Value, ct);
        portfolioId = await ResolvePortfolioIdAsync(command.Token, _db, ct);
        if (portfolioId is null) return Missing();

        var request = JsonSerializer.Deserialize<SubmitApplicationRequest>(command.RequestJson)
            ?? throw new ArgumentException("Public application payload is invalid.");
        if (!request.ConsentGiven)
            throw new DomainValidationException(
                "You must consent to a background/credit check to submit an application.");

        var references = await ResolveReferencesAsync(
            portfolioId.Value, request.PropertyId, request.UnitId, _db, ct);
        var normalizedEmail = Normalize(request.Email)?.ToLowerInvariant();
        if (normalizedEmail is not null && await _db.Set<RentalApplication>()
                .AsNoTracking()
                .AnyAsync(application =>
                    application.PortfolioId == portfolioId.Value
                    && application.DeletedAt == null
                    && application.Email != null
                    && application.Email.Trim().ToLower() == normalizedEmail
                    && (application.Status == ApplicationStatus.Submitted
                        || application.Status == ApplicationStatus.UnderReview
                        || application.Status == ApplicationStatus.Approved), ct))
        {
            throw new DomainValidationException(
                $"An open application for {normalizedEmail} already exists. Review it before creating another.",
                409);
        }
        var now = await AtomicCommandDbClock.ReadDatabaseClockUtcAsync(_db, ct);
        var entity = new RentalApplication
        {
            PortfolioId = portfolioId.Value,
            PropertyId = references.PropertyId,
            UnitId = references.UnitId,
            FirstName = request.FirstName.Trim(),
            LastName = request.LastName.Trim(),
            Email = Normalize(request.Email),
            Phone = Normalize(request.Phone),
            DateOfBirth = UtcDate(request.DateOfBirth),
            CurrentAddressLine1 = Normalize(request.CurrentAddressLine1),
            CurrentAddressLine2 = Normalize(request.CurrentAddressLine2),
            CurrentCity = Normalize(request.CurrentCity),
            CurrentState = Normalize(request.CurrentState),
            CurrentPostalCode = Normalize(request.CurrentPostalCode),
            CurrentAddress = AddressComposer.Compose(
                request.CurrentAddressLine1, request.CurrentAddressLine2,
                request.CurrentCity, request.CurrentState, request.CurrentPostalCode)
                ?? Normalize(request.CurrentAddress),
            Employer = Normalize(request.Employer),
            MonthlyIncome = request.MonthlyIncome,
            DesiredMoveInDate = UtcDate(request.DesiredMoveInDate),
            Notes = Normalize(request.Notes),
            IdExtractedFields = request.IdExtractedFields,
            ConsentGiven = true,
            ConsentAtUtc = now,
            ConsentIpAddress = command.IpAddress,
            Status = ApplicationStatus.Submitted,
            SubmittedAtUtc = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Add(entity);
        attempt.BindSemanticAudit(entity, new AtomicSemanticAudit(
            portfolioId.Value,
            "RentalApplication",
            0,
            AuditLogOperation.Created,
            ActorLabel: "Public applicant",
            ChangeReason: "Public rental application submitted with screening consent",
            IpAddress: command.IpAddress));
        try
        {
            await attempt.FlushBusinessAsync(ct);
        }
        catch (DbUpdateException ex) when (
            FindPostgresException(ex) is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: "IX_RentalApplications_PortfolioId_Email_Open_CI",
            })
        {
            throw new DomainValidationException(
                "An open application for this email address already exists. Review it before creating another.",
                409);
        }
        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = portfolioId.Value,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
            {
                entityType = "RentalApplication",
                entityId = entity.Id,
                operation = "update",
                data = new { },
            }),
            IdempotencyKey = $"{command.DeliveryIdempotencyKey}:application",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
        return new AtomicPublicApplicationSubmissionResult(
            true, entity.Id, entity.Status.ToString(),
            "Thank you. Your application has been received.");
    }

    public async Task AuthorizeReplayAsync(
        AtomicPublicApplicationSubmissionCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        if (await ResolvePortfolioIdAsync(command.Token, _db, ct) is null)
            throw new UnauthorizedAccessException("This application link is invalid or no longer active.");
    }

    private Task<int?> ResolvePortfolioIdAsync(
        string token,
        RentalCommandDbContext db,
        CancellationToken ct) =>
        db.Set<Portfolio>().AsNoTracking()
            .Where(portfolio => portfolio.PublicApplicationToken == token
                && portfolio.Status == PortfolioStatus.Active && portfolio.DeletedAt == null)
            .Select(portfolio => (int?)portfolio.Id)
            .SingleOrDefaultAsync(ct);

    private async Task<(int? PropertyId, int? UnitId)> ResolveReferencesAsync(
        int portfolioId,
        int? requestedPropertyId,
        int? requestedUnitId,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        var resolved = await db.Set<Portfolio>().AsNoTracking()
            .Where(portfolio => portfolio.Id == portfolioId && portfolio.DeletedAt == null)
            .Select(portfolio => new
            {
                PropertyId = requestedPropertyId > 0
                    ? db.Set<Property>()
                        .Where(property => property.Id == requestedPropertyId
                            && property.PortfolioId == portfolio.Id && property.DeletedAt == null)
                        .Select(property => (int?)property.Id).SingleOrDefault()
                    : null,
                UnitId = requestedUnitId > 0
                    ? db.Set<Unit>()
                        .Where(unit => unit.Id == requestedUnitId
                            && unit.PortfolioId == portfolio.Id && unit.DeletedAt == null)
                        .Select(unit => (int?)unit.Id).SingleOrDefault()
                    : null,
                UnitPropertyId = requestedUnitId > 0
                    ? db.Set<Unit>()
                        .Where(unit => unit.Id == requestedUnitId
                            && unit.PortfolioId == portfolio.Id && unit.DeletedAt == null)
                        .Select(unit => (int?)unit.PropertyId).SingleOrDefault()
                    : null,
            }).SingleAsync(ct);
        if (requestedPropertyId is > 0 && resolved.PropertyId is null)
            throw new DomainValidationException("Selected property was not found.");
        if (requestedUnitId is > 0 && resolved.UnitId is null)
            throw new DomainValidationException("Selected unit was not found.");
        if (resolved.PropertyId is > 0 && resolved.UnitPropertyId != null
            && resolved.PropertyId != resolved.UnitPropertyId)
            throw new DomainValidationException("Selected unit does not belong to the selected property.");
        return (resolved.UnitPropertyId ?? resolved.PropertyId, resolved.UnitId);
    }

    private void Validate(AtomicPublicApplicationSubmissionCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Token) || command.Token.Length > 64
            || string.IsNullOrWhiteSpace(command.RequestJson)
            || string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey)
            || command.DeliveryIdempotencyKey.Length > 128)
            throw new ArgumentException("Application token, request, and idempotency key are required.");
    }

    private AtomicPublicApplicationSubmissionResult Missing() =>
        new(false, 0, string.Empty, string.Empty);
    private string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
    private DateTime? UtcDate(DateTime? value) => value is null
        ? null
        : DateTime.SpecifyKind(value.Value.Date, DateTimeKind.Utc);

    private PostgresException? FindPostgresException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is PostgresException postgres) return postgres;
            if (current.InnerException is null) return null;
        }
        return null;
    }
}

public static class AtomicPublicApplicationSubmission
{
    public static readonly AtomicJsonResultCodec<AtomicPublicApplicationSubmissionResult> Codec =
        new("application.public-submit.v1");

    public static AtomicCommandIdentity Identity(AtomicPublicApplicationSubmissionCommand command)
    {
        var tokenDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.Token)))
            .ToLowerInvariant();
        return new AtomicCommandIdentity(
            "application.public-submit",
            $"{tokenDigest}:{command.DeliveryIdempotencyKey}");
    }
}
