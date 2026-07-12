using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Leasing;

namespace RentalCommand.Api.Controllers;

[ApiController]
[Route("api/v1/lease-managements")]
[Produces("application/json")]
public sealed class LeaseManagementController : ManagementControllerBase
{
    private static readonly AtomicJsonResultCodec<PrepareMoveInResult> ResultCodec =
        new("lease-management.prepare-move-in.v1");

    private readonly IAtomicUnitOfWork _atomic;
    private readonly TimeProvider _timeProvider;

    public LeaseManagementController(IAtomicUnitOfWork atomic, TimeProvider timeProvider)
    {
        _atomic = atomic;
        _timeProvider = timeProvider;
    }

    [HttpPost("prepare-move-in")]
    [ProducesResponseType(typeof(PrepareMoveInResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> PrepareMoveIn(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] PrepareMoveInRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return BadRequest(new { error = "Idempotency-Key is required." });
        }
        var normalizedKey = idempotencyKey.Trim();
        if (normalizedKey.Length > 200)
        {
            return BadRequest(new { error = "Idempotency-Key cannot exceed 200 characters." });
        }
        if (!TryReadAccessClaims(out var sessionId, out var accessContextId, out var accessRevision))
        {
            return Forbid();
        }
        if (request.Parties is null || request.TermType is null
            || request.Parties.Any(party => party.Role is null)
            || request.TermsPayload.ValueKind != JsonValueKind.Object)
        {
            return BadRequest(new { error = "Parties and an object TermsPayload are required." });
        }

        var portfolioId = GetPortfolioId();
        var userId = GetUserId();
        var keyDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedKey)))
            .ToLowerInvariant();

        try
        {
            var command = new PrepareMoveInCommand(
                portfolioId,
                request.ApplicationId,
                request.UnitId,
                userId,
                sessionId,
                accessContextId,
                accessRevision,
                _timeProvider.GetUtcNow().UtcDateTime,
                request.PlannedPossessionAtUtc,
                request.PartyEffectiveFrom,
                request.Parties.Select(party => new PrepareMoveInParty(
                    party.TenantId,
                    party.Role!.Value,
                    party.GuarantorLegalNoticeEligible,
                    party.ChangeReason,
                    party.IsAgreementSigner,
                    party.SigningOrder,
                    party.IsRequiredSigner)).ToArray(),
                request.DocumentTemplateId,
                request.DocumentTemplateVersion,
                request.TermType.Value,
                request.TermStartOn,
                request.TermEndOn,
                request.BaseRentAmount,
                request.RentDueDay,
                request.SecurityDepositObligation,
                request.LateFeeAmount,
                request.GracePeriodDays,
                request.TermsSchemaVersion,
                request.TermsPayload.GetRawText(),
                request.CreateSecurityDepositAccount,
                $"prepare-move-in:{portfolioId}:{request.ApplicationId}:{keyDigest}");

            var outcome = await _atomic.ExecuteAsync(
                new AtomicCommandIdentity(
                    "lease-management.prepare-move-in",
                    $"{portfolioId}:{request.ApplicationId}:{keyDigest}"),
                command,
                ResultCodec,
                ct);

            return outcome.Value.Outcome switch
            {
                PrepareMoveInOutcome.Prepared => StatusCode(
                    StatusCodes.Status201Created,
                    PrepareMoveInResponse.FromResult(
                        outcome.Value,
                        outcome.Disposition == AtomicCommandDisposition.Replayed)),
                PrepareMoveInOutcome.AlreadyPrepared => Conflict(new { error = outcome.Value.Error }),
                PrepareMoveInOutcome.ApplicationNotApproved =>
                    UnprocessableEntity(new { error = outcome.Value.Error }),
                PrepareMoveInOutcome.InvalidTemplate =>
                    UnprocessableEntity(new { error = outcome.Value.Error }),
                PrepareMoveInOutcome.InvalidParties =>
                    UnprocessableEntity(new { error = outcome.Value.Error }),
                PrepareMoveInOutcome.InvalidAgreementTerms =>
                    UnprocessableEntity(new { error = outcome.Value.Error }),
                _ => StatusCode(StatusCodes.Status500InternalServerError),
            };
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
        catch (JsonException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    private bool TryReadAccessClaims(
        out Guid sessionId,
        out int accessContextId,
        out long accessRevision)
    {
        sessionId = default;
        accessContextId = default;
        accessRevision = default;
        return Guid.TryParse(User.FindFirstValue("sid"), out sessionId)
            && int.TryParse(User.FindFirstValue("ctx"), out accessContextId)
            && long.TryParse(User.FindFirstValue("ar"), out accessRevision);
    }
}
