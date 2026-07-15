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

namespace RentalCommand.Api.Services.Domain;

public sealed record AtomicPublicApplicationSubmissionCommand(
    string Token,
    string RequestJson,
    string? IpAddress,
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record AtomicPublicApplicationSubmissionResult(
    bool Found,
    int ApplicationId,
    string Status,
    string Message) : IAtomicResultData;

/// <summary>Anonymous token-scoped intake with a durable receipt, audit, and outbox.</summary>
public sealed class AtomicPublicApplicationSubmissionHandler
    : IAtomicCommandHandler<AtomicPublicApplicationSubmissionCommand, AtomicPublicApplicationSubmissionResult>,
      IAtomicReplayAuthorizer<AtomicPublicApplicationSubmissionCommand>
{
    public async Task<AtomicPublicApplicationSubmissionResult> HandleAsync(
        AtomicPublicApplicationSubmissionCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        Validate(command);
        var portfolioId = await ResolvePortfolioIdAsync(command.Token, attempt.Persistence, ct);
        if (portfolioId is null) return Missing();
        await attempt.Locking.AcquireAsync(AtomicLockResource.Portfolio, portfolioId.Value, ct);
        portfolioId = await ResolvePortfolioIdAsync(command.Token, attempt.Persistence, ct);
        if (portfolioId is null) return Missing();

        var request = JsonSerializer.Deserialize<SubmitApplicationRequest>(command.RequestJson)
            ?? throw new ArgumentException("Public application payload is invalid.");
        if (!request.ConsentGiven)
            throw new DomainValidationException(
                "You must consent to a background/credit check to submit an application.");

        var references = await ResolveReferencesAsync(
            portfolioId.Value, request.PropertyId, request.UnitId, attempt.Persistence, ct);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var entity = new RentalApplication
        {
            PortfolioId = portfolioId.Value,
            PropertyId = references.PropertyId,
            UnitId = references.UnitId,
            FirstName = request.FirstName.Trim(),
            LastName = request.LastName.Trim(),
            Email = Normalize(request.Email),
            Phone = Normalize(request.Phone),
            DateOfBirth = Utc(request.DateOfBirth),
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
            DesiredMoveInDate = Utc(request.DesiredMoveInDate),
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
        attempt.Persistence.Add(entity);
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
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        Validate(command);
        if (await ResolvePortfolioIdAsync(command.Token, persistence, ct) is null)
            throw new UnauthorizedAccessException("This application link is invalid or no longer active.");
    }

    private static Task<int?> ResolvePortfolioIdAsync(
        string token,
        IAtomicPersistenceSession persistence,
        CancellationToken ct) =>
        persistence.Query<Portfolio>().AsNoTracking()
            .Where(portfolio => portfolio.PublicApplicationToken == token
                && portfolio.Status == PortfolioStatus.Active && portfolio.DeletedAt == null)
            .Select(portfolio => (int?)portfolio.Id)
            .SingleOrDefaultAsync(ct);

    private static async Task<(int? PropertyId, int? UnitId)> ResolveReferencesAsync(
        int portfolioId,
        int? requestedPropertyId,
        int? requestedUnitId,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        var resolved = await persistence.Query<Portfolio>().AsNoTracking()
            .Where(portfolio => portfolio.Id == portfolioId && portfolio.DeletedAt == null)
            .Select(portfolio => new
            {
                PropertyId = requestedPropertyId > 0
                    ? persistence.Query<Property>()
                        .Where(property => property.Id == requestedPropertyId
                            && property.PortfolioId == portfolio.Id && property.DeletedAt == null)
                        .Select(property => (int?)property.Id).SingleOrDefault()
                    : null,
                UnitId = requestedUnitId > 0
                    ? persistence.Query<Unit>()
                        .Where(unit => unit.Id == requestedUnitId
                            && unit.PortfolioId == portfolio.Id && unit.DeletedAt == null)
                        .Select(unit => (int?)unit.Id).SingleOrDefault()
                    : null,
                UnitPropertyId = requestedUnitId > 0
                    ? persistence.Query<Unit>()
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

    private static void Validate(AtomicPublicApplicationSubmissionCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Token) || command.Token.Length > 64
            || string.IsNullOrWhiteSpace(command.RequestJson)
            || string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey)
            || command.DeliveryIdempotencyKey.Length > 128)
            throw new ArgumentException("Application token, request, and idempotency key are required.");
    }

    private static AtomicPublicApplicationSubmissionResult Missing() =>
        new(false, 0, string.Empty, string.Empty);
    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
    private static DateTime? Utc(DateTime? value) => value is null ? null
        : value.Value.Kind == DateTimeKind.Utc ? value : value.Value.ToUniversalTime();

    private static PostgresException? FindPostgresException(Exception exception)
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
