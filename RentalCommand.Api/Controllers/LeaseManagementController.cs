using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Services.Domain;
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
    private static readonly AtomicJsonResultCodec<LeasePartyMutationResult> AddPartyResultCodec =
        new("lease-management.party.add.v1");
    private static readonly AtomicJsonResultCodec<LeasePartyMutationResult> EndPartyResultCodec =
        new("lease-management.party.end.v1");
    private static readonly AtomicJsonResultCodec<LeasePartyMutationResult> ChangePartyRoleResultCodec =
        new("lease-management.party.change-role.v1");
    private static readonly AtomicJsonResultCodec<LeasePartyMutationResult> GrantAccessResultCodec =
        new("lease-management.party.access.grant.v1");
    private static readonly AtomicJsonResultCodec<LeasePartyMutationResult> RevokeAccessResultCodec =
        new("lease-management.party.access.revoke.v1");
    private static readonly AtomicJsonResultCodec<GivePossessionResult> GivePossessionCodec =
        new("lease-management.give-possession.v1");
    private static readonly AtomicJsonResultCodec<ReturnPossessionResult> ReturnPossessionCodec =
        new("lease-management.return-possession.v1");
    private static readonly AtomicJsonResultCodec<RecordLeaseEndingDispositionResult>
        EndingDispositionCodec = new("lease-management.ending-disposition.v1");
    private static readonly AtomicJsonResultCodec<CancelPlannedRelationshipResult> CancelCodec =
        new("lease-management.cancel-planned.v1");
    private static readonly AtomicJsonResultCodec<TransferLeaseManagementResult> TransferCodec =
        new("lease-management.transfer-unit.v1");

    private readonly IAtomicUnitOfWork _atomic;
    private readonly ILeaseManagementQueryService _queryService;
    private readonly ILeaseQaService _qa;
    private readonly string _webBaseUrl;

    public LeaseManagementController(
        IAtomicUnitOfWork atomic,
        ILeaseManagementQueryService queryService,
        ILeaseQaService qa,
        IConfiguration configuration)
    {
        _atomic = atomic;
        _queryService = queryService;
        _qa = qa;
        _webBaseUrl = (configuration["App:WebBaseUrl"] ?? "https://localhost:5667").TrimEnd('/');
    }

    [HttpGet("page")]
    [ProducesResponseType(typeof(LeaseManagementListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<LeaseManagementListResponse>> ListPage(
        [FromQuery] LeaseManagementListQuery query,
        CancellationToken ct)
    {
        if (!TryReadAccessContext(out var access))
        {
            return Forbid();
        }

        return Ok(await _queryService.ListPageAsync(access, query, ct));
    }

    [HttpGet("prepare-move-in-context")]
    [ProducesResponseType(typeof(PrepareMoveInContextResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<PrepareMoveInContextResponse>> PrepareMoveInContext(
        CancellationToken ct)
    {
        if (!TryReadAccessContext(out var access))
        {
            return Forbid();
        }

        var businessDate = await _queryService.GetPortfolioBusinessDateAsync(access, ct);
        return Ok(new PrepareMoveInContextResponse(businessDate));
    }

    [HttpGet("{leaseManagementId:int}")]
    [ProducesResponseType(typeof(LeaseManagementDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LeaseManagementDetailResponse>> Get(
        int leaseManagementId,
        CancellationToken ct)
    {
        if (!TryReadAccessContext(out var access))
        {
            return Forbid();
        }

        var item = await _queryService.GetAsync(access, leaseManagementId, ct);
        return item is null ? NotFound(new { error = "Lease management relationship not found" }) : Ok(item);
    }

    /// <summary>
    /// Returns the exact current household and active relationship-access grants that require an explicit
    /// disposition before possession can be returned. Current membership, access filtering, and
    /// ordering are evaluated by PostgreSQL in two purposeful flat queries: one for parties and one
    /// for grants. The response does not rely on a client-side join or a per-party follow-up query.
    /// </summary>
    [HttpGet("{leaseManagementId:int}/return-possession-context")]
    [ProducesResponseType(typeof(ReturnPossessionContextResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ReturnPossessionContextResponse>> ReturnPossessionContext(
        int leaseManagementId,
        CancellationToken ct)
    {
        if (!TryReadAccessContext(out var access))
        {
            return Forbid();
        }

        return Ok(await _queryService.GetReturnPossessionContextAsync(access, leaseManagementId, ct));
    }

    /// <summary>
    /// Tenant-facing ledger for one LeaseManagement's continuous TenantAccount. Authorization,
    /// aggregation, ordering, and paging remain database-side in the canonical reader.
    /// </summary>
    [HttpGet("{leaseManagementId:int}/ledger")]
    [ProducesResponseType(typeof(LeaseLedgerResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LeaseLedgerResponse>> Ledger(
        int leaseManagementId,
        CancellationToken ct,
        [FromQuery] int skip = 0,
        [FromQuery] int? take = null)
    {
        if (!TryReadAccessContext(out var access))
        {
            return Forbid();
        }

        var ledger = await _queryService.GetLedgerAsync(access, leaseManagementId, skip, take, ct);
        return ledger == null ? NotFound(new { error = "Tenant account not found" }) : Ok(ledger);
    }

    [HttpPost("{leaseManagementId:int}/ask")]
    [ProducesResponseType(typeof(LeaseQuestionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LeaseQuestionResponse>> Ask(
        int leaseManagementId,
        [FromBody] LeaseQuestionRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            return BadRequest(new { error = "Question is required." });
        }
        if (!TryReadAccessContext(out var access))
        {
            return Forbid();
        }
        var answer = await _qa.AskManagementAsync(
            access, leaseManagementId, request.Question.Trim(), ct);
        return answer is null
            ? NotFound(new { error = "No governing agreement document is available." })
            : Ok(answer);
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
        if (!TryReadAccessContext(out var sessionId, out var accessContextId, out var accessRevision))
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
                request.OpeningBalanceAmount,
                request.OpeningBalanceEffectiveOn,
                request.OpeningBalanceNote,
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
                PrepareMoveInOutcome.UnitUnavailable =>
                    Conflict(new { error = outcome.Value.Error }),
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

    [HttpPost("{leaseManagementId:int}/parties")]
    [ProducesResponseType(typeof(LeasePartyMutationResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> AddEffectiveParty(
        int leaseManagementId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] AddEffectivePartyRequest request,
        CancellationToken ct)
    {
        if (!TryPrepareMutation(idempotencyKey, out var envelope, out var error))
        {
            return error!;
        }
        if (request.Role is null || request.LegalBasis is null)
        {
            return BadRequest(new { error = "Role and LegalBasis are required." });
        }
        if (!HasRequiredTextWithinLimit(request.ChangeReason, 500))
        {
            return BadRequest(new { error = "ChangeReason is required and cannot exceed 500 characters." });
        }

        var command = new AddEffectivePartyCommand(
            envelope.PortfolioId,
            leaseManagementId,
            request.TenantId,
            request.Role.Value,
            request.EffectiveFrom,
            request.GuarantorLegalNoticeEligible,
            request.ChangeReason,
            request.LegalBasis.SameRelationshipConfirmed,
            request.LegalBasis.AgreementId,
            request.LegalBasis.AddendumId,
            envelope.UserId,
            envelope.AuthSessionId,
            envelope.AccessContextId,
            envelope.AccessRevision,
            $"lease-party-add:{envelope.PortfolioId}:{leaseManagementId}:{envelope.KeyDigest}");
        return await ExecuteMutation(
            "lease-management.party.add",
            $"{leaseManagementId}",
            envelope.KeyDigest,
            command,
            AddPartyResultCodec,
            StatusCodes.Status201Created,
            ct);
    }

    [HttpPost("{leaseManagementId:int}/parties/{partyId:int}/end")]
    [ProducesResponseType(typeof(LeasePartyMutationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> EndEffectiveParty(
        int leaseManagementId,
        int partyId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] EndEffectivePartyRequest request,
        CancellationToken ct)
    {
        if (!TryPrepareMutation(idempotencyKey, out var envelope, out var error))
        {
            return error!;
        }
        if (request.AccessDisposition is null || request.LegalBasis is null)
        {
            return BadRequest(new { error = "AccessDisposition and LegalBasis are required." });
        }
        if (!HasRequiredTextWithinLimit(request.ChangeReason, 500))
        {
            return BadRequest(new { error = "ChangeReason is required and cannot exceed 500 characters." });
        }

        var command = new EndEffectivePartyCommand(
            envelope.PortfolioId,
            leaseManagementId,
            partyId,
            request.EffectiveThrough,
            request.AccessDisposition.Value,
            request.PrimarySuccessorPartyId,
            request.LegalBasis.SameRelationshipConfirmed,
            request.LegalBasis.AgreementId,
            request.LegalBasis.AddendumId,
            request.ChangeReason,
            envelope.UserId,
            envelope.AuthSessionId,
            envelope.AccessContextId,
            envelope.AccessRevision,
            $"lease-party-end:{envelope.PortfolioId}:{leaseManagementId}:{partyId}:{envelope.KeyDigest}");
        return await ExecuteMutation(
            "lease-management.party.end",
            $"{leaseManagementId}:{partyId}",
            envelope.KeyDigest,
            command,
            EndPartyResultCodec,
            StatusCodes.Status200OK,
            ct);
    }

    [HttpPost("{leaseManagementId:int}/parties/{partyId:int}/change-role")]
    [ProducesResponseType(typeof(LeasePartyMutationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ChangeEffectivePartyRole(
        int leaseManagementId,
        int partyId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] ChangeEffectivePartyRoleRequest request,
        CancellationToken ct)
    {
        if (!TryPrepareMutation(idempotencyKey, out var envelope, out var error))
        {
            return error!;
        }
        if (request.NewRole is null || request.AccessDisposition is null || request.LegalBasis is null)
        {
            return BadRequest(new { error = "NewRole, AccessDisposition, and LegalBasis are required." });
        }
        if (!HasRequiredTextWithinLimit(request.ChangeReason, 500))
        {
            return BadRequest(new { error = "ChangeReason is required and cannot exceed 500 characters." });
        }

        var command = new ChangeEffectivePartyRoleCommand(
            envelope.PortfolioId,
            leaseManagementId,
            partyId,
            request.NewRole.Value,
            request.EffectiveOn,
            request.GuarantorLegalNoticeEligible,
            request.AccessDisposition.Value,
            request.CompanionPrimaryPartyId,
            request.CompanionNewRole,
            request.CompanionGuarantorLegalNoticeEligible,
            request.LegalBasis.SameRelationshipConfirmed,
            request.LegalBasis.AgreementId,
            request.LegalBasis.AddendumId,
            request.ChangeReason,
            envelope.UserId,
            envelope.AuthSessionId,
            envelope.AccessContextId,
            envelope.AccessRevision,
            $"lease-party-role:{envelope.PortfolioId}:{leaseManagementId}:{partyId}:{envelope.KeyDigest}");
        return await ExecuteMutation(
            "lease-management.party.change-role",
            $"{leaseManagementId}:{partyId}",
            envelope.KeyDigest,
            command,
            ChangePartyRoleResultCodec,
            StatusCodes.Status200OK,
            ct);
    }

    [HttpPost("{leaseManagementId:int}/parties/{partyId:int}/access")]
    [ProducesResponseType(typeof(LeasePartyMutationResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> GrantTenantUserAccess(
        int leaseManagementId,
        int partyId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] GrantTenantUserAccessRequest request,
        CancellationToken ct)
    {
        if (!TryPrepareMutation(idempotencyKey, out var envelope, out var error))
        {
            return error!;
        }
        if (!HasRequiredTextWithinLimit(request.Reason, 500))
        {
            return BadRequest(new { error = "Reason is required and cannot exceed 500 characters." });
        }

        var command = new GrantTenantUserAccessCommand(
            envelope.PortfolioId,
            leaseManagementId,
            partyId,
            request.Reason,
            _webBaseUrl,
            envelope.UserId,
            envelope.AuthSessionId,
            envelope.AccessContextId,
            envelope.AccessRevision,
            $"tenant-access-grant:{envelope.PortfolioId}:{leaseManagementId}:{partyId}:{envelope.KeyDigest}");
        return await ExecuteMutation(
            "lease-management.party.access.grant",
            $"{leaseManagementId}:{partyId}",
            envelope.KeyDigest,
            command,
            GrantAccessResultCodec,
            StatusCodes.Status201Created,
            ct);
    }

    [HttpPost("{leaseManagementId:int}/parties/{partyId:int}/access/{tenantUserAccessId:int}/revoke")]
    [ProducesResponseType(typeof(LeasePartyMutationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> RevokeTenantUserAccess(
        int leaseManagementId,
        int partyId,
        int tenantUserAccessId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] RevokeTenantUserAccessRequest request,
        CancellationToken ct)
    {
        if (!TryPrepareMutation(idempotencyKey, out var envelope, out var error))
        {
            return error!;
        }
        if (!HasRequiredTextWithinLimit(request.Reason, 500))
        {
            return BadRequest(new { error = "Reason is required and cannot exceed 500 characters." });
        }

        var command = new RevokeTenantUserAccessCommand(
            envelope.PortfolioId,
            leaseManagementId,
            partyId,
            tenantUserAccessId,
            request.Reason,
            envelope.UserId,
            envelope.AuthSessionId,
            envelope.AccessContextId,
            envelope.AccessRevision,
            $"tenant-access-revoke:{envelope.PortfolioId}:{leaseManagementId}:{partyId}:{tenantUserAccessId}:{envelope.KeyDigest}");
        return await ExecuteMutation(
            "lease-management.party.access.revoke",
            $"{leaseManagementId}:{partyId}:{tenantUserAccessId}",
            envelope.KeyDigest,
            command,
            RevokeAccessResultCodec,
            StatusCodes.Status200OK,
            ct);
    }

    private async Task<IActionResult> ExecuteMutation<TCommand>(
        string commandType,
        string identityTarget,
        string keyDigest,
        TCommand command,
        AtomicJsonResultCodec<LeasePartyMutationResult> codec,
        int successStatus,
        CancellationToken ct)
        where TCommand : notnull, IAtomicCommandData
    {
        try
        {
            var outcome = await _atomic.ExecuteAsync(
                new AtomicCommandIdentity(
                    commandType,
                    $"{GetPortfolioId()}:{identityTarget}:{keyDigest}"),
                command,
                codec,
                ct);
            if (outcome.Value.Outcome == LeasePartyMutationOutcome.Applied)
            {
                return StatusCode(
                    successStatus,
                    LeasePartyMutationResponse.FromResult(
                        outcome.Value,
                        outcome.Disposition == AtomicCommandDisposition.Replayed));
            }

            return outcome.Value.Outcome switch
            {
                LeasePartyMutationOutcome.AlreadyActive or LeasePartyMutationOutcome.AlreadyRevoked
                    => Conflict(new { error = outcome.Value.Error }),
                LeasePartyMutationOutcome.InvalidPrimaryTransition
                    or LeasePartyMutationOutcome.AccessTransitionInvalid
                    => Conflict(new { error = outcome.Value.Error }),
                LeasePartyMutationOutcome.InvalidEffectiveDate
                    or LeasePartyMutationOutcome.InvalidParty
                    or LeasePartyMutationOutcome.LegalBasisRequired
                    => UnprocessableEntity(new { error = outcome.Value.Error }),
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
    }

    private bool TryPrepareMutation(
        string? idempotencyKey,
        out MutationEnvelope envelope,
        out IActionResult? error)
    {
        envelope = default;
        error = null;
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            error = BadRequest(new { error = "Idempotency-Key is required." });
            return false;
        }
        var normalizedKey = idempotencyKey.Trim();
        if (normalizedKey.Length > 200)
        {
            error = BadRequest(new { error = "Idempotency-Key cannot exceed 200 characters." });
            return false;
        }
        if (!TryReadAccessContext(out var sessionId, out var accessContextId, out var accessRevision))
        {
            error = Forbid();
            return false;
        }

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedKey)))
            .ToLowerInvariant();
        envelope = new MutationEnvelope(
            GetPortfolioId(), GetUserId(), sessionId, accessContextId, accessRevision, digest);
        return true;
    }

    [HttpPost("{leaseManagementId:int}/give-possession")]
    [ProducesResponseType(typeof(GivePossessionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> GivePossession(int leaseManagementId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] GivePossessionRequest request, CancellationToken ct)
    {
        if (!TryPrepareCommand(idempotencyKey, out var normalizedKey, out var sessionId,
                out var accessContextId, out var accessRevision, out var failure))
        {
            return failure!;
        }
        var portfolioId = GetPortfolioId();
        var userId = GetUserId();
        var digest = Digest(normalizedKey!);
        try
        {
            var outcome = await _atomic.ExecuteAsync(
                new AtomicCommandIdentity("lease-management.give-possession",
                    $"{portfolioId}:{leaseManagementId}:{digest}"),
                new GivePossessionCommand(portfolioId, leaseManagementId, request.UnitId, userId,
                    sessionId, accessContextId, accessRevision,
                    $"give-possession:{portfolioId}:{leaseManagementId}:{digest}"),
                GivePossessionCodec, ct);
            return outcome.Value.Outcome switch
            {
                GivePossessionOutcome.Given or GivePossessionOutcome.AlreadyGiven
                    when outcome.Value.PossessionGivenAtUtc.HasValue => Ok(new GivePossessionResponse(
                        outcome.Value.LeaseManagementId, outcome.Value.UnitId,
                        outcome.Value.PossessionGivenAtUtc.Value,
                        outcome.Disposition != AtomicCommandDisposition.Executed)),
                GivePossessionOutcome.UnitUnavailable => Conflict(new { error = outcome.Value.Error }),
                GivePossessionOutcome.RelationshipNotEligible
                    or GivePossessionOutcome.AgreementNotExecuted
                    or GivePossessionOutcome.AccountNotOpen =>
                    UnprocessableEntity(new { error = outcome.Value.Error }),
                _ => StatusCode(StatusCodes.Status500InternalServerError),
            };
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
    }

    [HttpPost("{leaseManagementId:int}/return-possession")]
    [ProducesResponseType(typeof(ReturnPossessionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ReturnPossession(int leaseManagementId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] ReturnPossessionRequest request, CancellationToken ct)
    {
        if (!TryPrepareCommand(idempotencyKey, out var normalizedKey, out var sessionId,
                out var accessContextId, out var accessRevision, out var failure))
        {
            return failure!;
        }
        if (request.Parties.Any(item => item.Disposition is null)
            || request.Accesses.Any(item => item.Disposition is null))
        {
            return BadRequest(new { error = "Every party and access disposition is required." });
        }
        if (!HasRequiredTextWithinLimit(request.TurnoverReason, 1000))
        {
            return BadRequest(new { error = "TurnoverReason is required and cannot exceed 1000 characters." });
        }
        var portfolioId = GetPortfolioId();
        var userId = GetUserId();
        var digest = Digest(normalizedKey!);
        try
        {
            var outcome = await _atomic.ExecuteAsync(
                new AtomicCommandIdentity("lease-management.return-possession",
                    $"{portfolioId}:{leaseManagementId}:{digest}"),
                new ReturnPossessionCommand(portfolioId, leaseManagementId, request.UnitId, userId,
                    sessionId, accessContextId, accessRevision,
                    request.Parties.Select(item => new ReturnPossessionParty(
                        item.LeaseManagementPartyId, item.Disposition!.Value)).ToArray(),
                    request.Accesses.Select(item => new ReturnPossessionAccess(
                        item.TenantUserAccessId, item.Disposition!.Value)).ToArray(),
                    request.TurnoverReason,
                    $"return-possession:{portfolioId}:{leaseManagementId}:{digest}"),
                ReturnPossessionCodec, ct);
            return outcome.Value.Outcome switch
            {
                ReturnPossessionOutcome.Returned
                    when outcome.Value.TurnoverPeriodId.HasValue
                         && outcome.Value.PossessionReturnedAtUtc.HasValue =>
                    Ok(new ReturnPossessionResponse(outcome.Value.LeaseManagementId,
                        outcome.Value.UnitId, outcome.Value.TurnoverPeriodId.Value,
                        outcome.Value.PossessionReturnedAtUtc.Value,
                        outcome.Disposition != AtomicCommandDisposition.Executed)),
                ReturnPossessionOutcome.AlreadyReturned => Conflict(new { error = outcome.Value.Error }),
                ReturnPossessionOutcome.TurnoverAlreadyOpen => Conflict(new { error = outcome.Value.Error }),
                ReturnPossessionOutcome.PossessionNotGiven
                    or ReturnPossessionOutcome.InvalidPartyDisposition
                    or ReturnPossessionOutcome.InvalidAccessDisposition =>
                    UnprocessableEntity(new { error = outcome.Value.Error }),
                _ => StatusCode(StatusCodes.Status500InternalServerError),
            };
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
    }

    [HttpPost("{leaseManagementId:int}/ending-disposition")]
    [ProducesResponseType(typeof(RecordLeaseEndingDispositionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> RecordEndingDisposition(
        int leaseManagementId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] RecordLeaseEndingDispositionRequest request,
        CancellationToken ct)
    {
        if (!TryPrepareCommand(idempotencyKey, out var normalizedKey, out var sessionId,
                out var accessContextId, out var accessRevision, out var failure))
        {
            return failure!;
        }
        if (request.Disposition is null
            || !HasRequiredTextWithinLimit(request.DecisionReason, 1000))
        {
            return BadRequest(new
            {
                error = "Disposition and a decision reason of at most 1000 characters are required.",
            });
        }

        var portfolioId = GetPortfolioId();
        var userId = GetUserId();
        var digest = Digest(normalizedKey!);
        try
        {
            var outcome = await _atomic.ExecuteAsync(
                new AtomicCommandIdentity(
                    "lease-management.ending-disposition",
                    $"{portfolioId}:{leaseManagementId}:{digest}"),
                new RecordLeaseEndingDispositionCommand(
                    portfolioId,
                    leaseManagementId,
                    request.UnitId,
                    request.Disposition.Value,
                    request.NoticeGivenAtUtc,
                    request.PlannedMoveOutAtUtc,
                    request.DecisionReason,
                    userId,
                    sessionId,
                    accessContextId,
                    accessRevision,
                    $"ending-disposition:{portfolioId}:{leaseManagementId}:{digest}"),
                EndingDispositionCodec,
                ct);

            return outcome.Value.Outcome switch
            {
                RecordLeaseEndingDispositionOutcome.Recorded => Ok(
                    new RecordLeaseEndingDispositionResponse(
                        outcome.Value.LeaseManagementId,
                        outcome.Value.EndingDisposition,
                        outcome.Value.EndingDispositionDecidedAtUtc,
                        outcome.Value.EndingDispositionDecidedByUserId,
                        outcome.Value.NoticeGivenAtUtc,
                        outcome.Value.PlannedMoveOutAtUtc,
                        outcome.Disposition != AtomicCommandDisposition.Executed)),
                RecordLeaseEndingDispositionOutcome.RelationshipNotEligible
                    or RecordLeaseEndingDispositionOutcome.InvalidDates =>
                    UnprocessableEntity(new { error = outcome.Value.Error }),
                _ => StatusCode(StatusCodes.Status500InternalServerError),
            };
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
    }

    [HttpPost("{leaseManagementId:int}/cancel")]
    [ProducesResponseType(typeof(CancelPlannedRelationshipResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CancelPlannedRelationship(
        int leaseManagementId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] CancelPlannedRelationshipRequest request,
        CancellationToken ct)
    {
        if (!TryPrepareCommand(idempotencyKey, out var normalizedKey, out var sessionId,
                out var accessContextId, out var accessRevision, out var failure))
        {
            return failure!;
        }
        if (request.Accesses.Any(item => item.Disposition is null))
        {
            return BadRequest(new { error = "Every active tenant access disposition is required." });
        }
        if (!HasRequiredTextWithinLimit(request.CancellationReasonCode, 40)
            || !HasRequiredTextWithinLimit(request.DraftCancellationReason, 1000)
            || request.CancellationNote?.Trim().Length > 2000)
        {
            return BadRequest(new
            {
                error = "CancellationReasonCode, DraftCancellationReason, and CancellationNote are invalid.",
            });
        }

        var portfolioId = GetPortfolioId();
        var userId = GetUserId();
        var digest = Digest(normalizedKey!);
        try
        {
            var outcome = await _atomic.ExecuteAsync(
                new AtomicCommandIdentity(
                    "lease-management.cancel-planned",
                    $"{portfolioId}:{leaseManagementId}:{digest}"),
                new CancelPlannedRelationshipCommand(
                    portfolioId,
                    leaseManagementId,
                    request.UnitId,
                    userId,
                    sessionId,
                    accessContextId,
                    accessRevision,
                    request.CancellationReasonCode,
                    request.CancellationNote,
                    request.DraftCancellationReason,
                    request.Accesses.Select(item => new CancelPlannedRelationshipAccess(
                        item.TenantUserAccessId, item.Disposition!.Value)).ToArray(),
                    $"cancel-planned:{portfolioId}:{leaseManagementId}:{digest}"),
                CancelCodec,
                ct);

            return outcome.Value.Outcome switch
            {
                CancelPlannedRelationshipOutcome.Canceled
                    when outcome.Value.CanceledAtUtc.HasValue
                        && outcome.Value.AccountClosedAtUtc.HasValue => Ok(
                        new CancelPlannedRelationshipResponse(
                            outcome.Value.LeaseManagementId,
                            outcome.Value.UnitId,
                            outcome.Value.CanceledAtUtc.Value,
                            outcome.Value.AccountClosedAtUtc.Value,
                            outcome.Value.CanceledAgreementDraftIds,
                            outcome.Value.CanceledAddendumDraftIds,
                            outcome.Value.RevokedAccessIds,
                            outcome.Value.RetainedAccessIds,
                            outcome.Disposition != AtomicCommandDisposition.Executed)),
                CancelPlannedRelationshipOutcome.AlreadyCanceled
                    or CancelPlannedRelationshipOutcome.IssuedArtifactsRequireResolution
                    or CancelPlannedRelationshipOutcome.FinancialResolutionRequired =>
                    Conflict(new { error = outcome.Value.Error }),
                CancelPlannedRelationshipOutcome.PossessionAlreadyGiven
                    or CancelPlannedRelationshipOutcome.InvalidAccessPolicy =>
                    UnprocessableEntity(new { error = outcome.Value.Error }),
                _ => StatusCode(StatusCodes.Status500InternalServerError),
            };
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
    }

    [HttpPost("{leaseManagementId:int}/transfer")]
    [ProducesResponseType(typeof(TransferLeaseManagementResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> TransferToUnit(
        int leaseManagementId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] TransferLeaseManagementRequest request,
        CancellationToken ct)
    {
        if (!TryPrepareCommand(idempotencyKey, out var normalizedKey, out var sessionId,
                out var accessContextId, out var accessRevision, out var failure))
        {
            return failure!;
        }
        if (!HasRequiredTextWithinLimit(request.TransferReason, 500)
            || request.SourceUnitId <= 0
            || request.DestinationUnitId <= 0
            || request.SourceUnitId == request.DestinationUnitId
            || request.EffectiveOn == default
            || request.DestinationDocumentTemplateId <= 0
            || (request.GiveDestinationPossessionNow
                && !HasRequiredTextWithinLimit(request.PossessionAgreementExceptionReason, 1000))
            || (!request.GiveDestinationPossessionNow
                && !string.IsNullOrWhiteSpace(request.PossessionAgreementExceptionReason)))
        {
            return BadRequest(new
            {
                error = "Destination Unit, transfer date, template, reason, and possession policy are invalid.",
            });
        }

        var portfolioId = GetPortfolioId();
        var userId = GetUserId();
        var digest = Digest(normalizedKey!);
        var transferPublicId = new Guid(Convert.FromHexString(digest[..32]));
        try
        {
            var outcome = await _atomic.ExecuteAsync(
                new AtomicCommandIdentity(
                    "lease-management.transfer-unit",
                    $"{portfolioId}:{leaseManagementId}:{digest}"),
                new TransferLeaseManagementCommand(
                    portfolioId,
                    leaseManagementId,
                    request.SourceUnitId,
                    request.DestinationUnitId,
                    userId,
                    sessionId,
                    accessContextId,
                    accessRevision,
                    transferPublicId,
                    request.EffectiveOn,
                    request.PlannedDestinationPossessionAtUtc,
                    request.GiveDestinationPossessionNow,
                    request.PossessionAgreementExceptionReason,
                    request.DestinationDocumentTemplateId,
                    request.CarryTenantBalance,
                    request.CarrySecurityDeposit,
                    request.TransferReason,
                    $"unit-transfer:{portfolioId}:{leaseManagementId}:{digest}"),
                TransferCodec,
                ct);

            return outcome.Value.Outcome switch
            {
                TransferLeaseManagementOutcome.Transferred
                    when outcome.Value.SourcePossessionReturnedAtUtc.HasValue =>
                    StatusCode(StatusCodes.Status201Created, new TransferLeaseManagementResponse(
                        outcome.Value.TransferPublicId,
                        outcome.Value.SourceLeaseManagementId,
                        outcome.Value.SourceUnitId,
                        outcome.Value.DestinationLeaseManagementId,
                        outcome.Value.DestinationUnitId,
                        outcome.Value.DestinationTenantAccountId,
                        outcome.Value.DestinationAgreementId,
                        outcome.Value.DestinationSecurityDepositAccountId,
                        outcome.Value.TurnoverPeriodId,
                        outcome.Value.SourcePossessionReturnedAtUtc.Value,
                        outcome.Value.DestinationPossessionGivenAtUtc,
                        outcome.Value.CarriedTenantBalance,
                        outcome.Value.CarriedSecurityDeposit,
                        outcome.Value.DestinationPartyIds,
                        outcome.Value.DestinationSignerIds,
                        outcome.Value.DestinationAccessIds,
                        DestinationAgreementRequiresSignature: true,
                        outcome.Disposition != AtomicCommandDisposition.Executed)),
                TransferLeaseManagementOutcome.AlreadyTransferred
                    or TransferLeaseManagementOutcome.DestinationUnavailable
                    or TransferLeaseManagementOutcome.InvalidFinancialState =>
                    Conflict(new { error = outcome.Value.Error }),
                TransferLeaseManagementOutcome.SourcePossessionNotOpen
                    or TransferLeaseManagementOutcome.GoverningAgreementRequired
                    or TransferLeaseManagementOutcome.TenantAccountNotOpen
                    or TransferLeaseManagementOutcome.InvalidTemplate
                    or TransferLeaseManagementOutcome.InvalidHousehold =>
                    UnprocessableEntity(new { error = outcome.Value.Error }),
                _ => StatusCode(StatusCodes.Status500InternalServerError),
            };
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
    }

    private bool TryReadAccessContext(
        out Guid sessionId,
        out int accessContextId,
        out long accessRevision)
    {
        sessionId = default;
        accessContextId = default;
        accessRevision = default;
        if (!TryGetActiveAccessContext(out var active)) return false;
        sessionId = active.SessionId;
        accessContextId = active.AccessContextId;
        accessRevision = active.AccessRevision;
        return true;
    }

    private readonly record struct MutationEnvelope(
        int PortfolioId,
        int UserId,
        Guid AuthSessionId,
        int AccessContextId,
        long AccessRevision,
        string KeyDigest);

    private bool TryPrepareCommand(string? idempotencyKey, out string? normalizedKey,
        out Guid sessionId, out int accessContextId, out long accessRevision,
        out IActionResult? failure)
    {
        normalizedKey = idempotencyKey?.Trim();
        sessionId = default;
        accessContextId = default;
        accessRevision = default;
        failure = null;
        if (string.IsNullOrWhiteSpace(normalizedKey) || normalizedKey.Length > 200)
        {
            failure = BadRequest(new { error = "A valid Idempotency-Key is required (maximum 200 characters)." });
            return false;
        }
        if (!TryReadAccessContext(out sessionId, out accessContextId, out accessRevision))
        {
            failure = Forbid();
            return false;
        }
        return true;
    }

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool HasRequiredTextWithinLimit(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maxLength;
}
