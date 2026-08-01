using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Imaging;
using RentalCommand.Api.Scanning;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Configuration;
using RentalCommand.Core.Documents;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;
using RentalCommand.Data.Documents;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// General-purpose document attachment hub: upload, list, download, and soft-delete
/// <see cref="Core.Entities.StoredFile"/> rows for any entity type within the caller's portfolio.
/// All routes are scoped through the validated canonical access context.
///
/// <para>
/// This controller is tenant-reachable on purpose (tenants attach a photo to their own maintenance
/// request from the portal), so it stays on the plain <see cref="AuthenticatedPortfolioControllerBase"/>
/// rather than the staff-only <see cref="ManagementControllerBase"/>. Portfolio scoping alone is NOT
/// sufficient for a Tenant principal — every other tenant in the same portfolio shares that scope — so a
/// tenant caller is additionally constrained to documents on a <c>WorkOrder</c> they own (their only
/// legitimate document surface). Staff callers keep full portfolio access, even when an example/demo user
/// also has tenant relationships. See <see cref="TenantMayAccessEntityAsync"/>.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/documents")]
[Produces("application/json")]
public sealed class DocumentsController : AuthenticatedPortfolioControllerBase
{
    private const string DocumentTemplateEntityType = "DocumentTemplate";
    private static readonly string[] DocumentTemplateCapabilityKeys =
    [
        CapabilityKeys.RentalsManage,
        CapabilityKeys.LeasingAgreementsPrepare,
    ];

    // Content types safe to render inline (no active content that could run scripts).
    private static readonly HashSet<string> InlineSafeContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "image/jpeg", "image/png", "image/gif", "image/webp", "image/heic"
    };

    // Additional MIME types allowed for general documents (beyond the scan-only allowlist).
    private static readonly HashSet<string> DocumentMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Plain text / CSV
        "text/plain",
        "text/csv",
        // Word processing
        "application/msword",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        // Spreadsheets
        "application/vnd.ms-excel",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        // Presentations
        "application/vnd.ms-powerpoint",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation",
    };

    // Allowed extensions for the document hub (superset of scan extensions).
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".jpg", ".jpeg", ".png", ".heic",
        ".txt", ".csv",
        ".doc", ".docx",
        ".xls", ".xlsx",
        ".ppt", ".pptx"
    };

    private readonly IDocumentService _documents;
    private readonly IFileStorage _storage;
    private readonly RentalCommandDbContext _db;
    private readonly UploadSettings _uploadSettings;
    private readonly ILogger<DocumentsController> _logger;

    public DocumentsController(
        IDocumentService documents,
        IFileStorage storage,
        RentalCommandDbContext db,
        IOptions<UploadSettings> uploadSettings,
        ILogger<DocumentsController> logger)
    {
        _documents = documents;
        _storage = storage;
        _db = db;
        _uploadSettings = uploadSettings.Value;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // POST /api/v1/documents  — upload a document and attach to an entity
    // -------------------------------------------------------------------------

    [HttpPost]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(DocumentDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<DocumentDto>> Upload(
        IFormFile file,
        [FromForm] string entityType,
        [FromForm] long entityId,
        [FromForm] string? category,
        [FromForm] string clientOperationId,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "A non-empty file is required." });

        var contentType = file.ContentType?.Trim() ?? string.Empty;
        var fileName = Path.GetFileName(file.FileName ?? string.Empty);

        var (isValid, validationError) = ValidateDocumentUpload(fileName, contentType, file.Length);
        if (!isValid)
            return BadRequest(new { error = validationError });

        if (string.IsNullOrWhiteSpace(entityType))
            return BadRequest(new { error = "entityType is required." });
        if (string.IsNullOrWhiteSpace(clientOperationId) || clientOperationId.Trim().Length > 160)
            return BadRequest(new { error = "clientOperationId is required and cannot exceed 160 characters." });

        var portfolioId = GetPortfolioId();
        var normalizedEntityType = entityType.Trim();
        if (!TryParseTarget(normalizedEntityType, out var target))
            return BadRequest(new { error = $"entityType '{normalizedEntityType}' is not a supported document target." });
        var isStaff = HasWorkspaceMembership();
        WorkspaceReadScope? staffScope = null;
        if (isStaff)
        {
            if (!TryReadWorkspaceScope(out var scope) ||
                !await StaffMayAccessTargetAsync(scope, target, entityId, write: true, ct))
                return NotFound(new { error = "The referenced record was not found in your portfolio." });
            staffScope = scope;
        }
        var tenantId = isStaff ? null : await ResolveTenantIdAsync(portfolioId, ct);
        if (!isStaff && (!tenantId.HasValue || target != StoredDocumentTarget.WorkOrder))
            return NotFound(new { error = "The referenced record was not found in your portfolio." });
        if (!isStaff && !await TenantMayAccessEntityAsync(target.ToString(), entityId, portfolioId, ct))
            return NotFound(new { error = "The referenced record was not found in your portfolio." });

        string contentSha256;
        await using (var hashStream = file.OpenReadStream())
        {
            contentSha256 = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, ct)).ToLowerInvariant();
        }

        var sanitizedFileName = DiskFileStorage.SanitizeFileName(fileName);
        var requestFingerprint = Fingerprint(new
        {
            portfolioId,
            actorUserId = GetUserId(),
            target = target.ToString(),
            entityId,
            contentSha256,
            fileName = sanitizedFileName,
            contentType,
            sizeBytes = file.Length,
            category = category?.Trim(),
        });

        PendingFileUploadAdmission admission;
        try
        {
            admission = await _documents.PrepareUploadAsync(
                portfolioId,
                GetUserId(),
                clientOperationId,
                requestFingerprint,
                sanitizedFileName,
                contentType,
                file.Length,
                ct);
        }
        catch (UploadOperationConflictException ex)
        {
            return Conflict(new { error = ex.Message });
        }

        var finalized = await _documents.GetFinalizedUploadAsync(portfolioId, admission, ct);
        if (finalized is not null)
        {
            finalized.Category = category;
            return CreatedAtAction(nameof(GetFile), new { id = finalized.Id }, finalized);
        }

        try
        {
            await using var uploadStream = file.OpenReadStream();
            await _storage.UploadAtAsync(
                uploadStream,
                admission.StoragePath,
                sanitizedFileName,
                contentType,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Document storage upload failed for portfolio {PortfolioId}", portfolioId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "File storage failed." });
        }

        var dto = await _documents.CreateAsync(
            admission.Id,
            portfolioId,
            target,
            entityId,
            GetUserId(),
            tenantId,
            isStaff,
            staffScope,
            clientOperationId,
            requestFingerprint,
            contentSha256,
            sanitizedFileName,
            contentType,
            file.Length,
            admission.StoragePath,
            ct);
        if (dto is null)
            return NotFound(new { error = "The referenced record was not found in your portfolio." });

        dto.Category = category;
        return CreatedAtAction(nameof(GetFile), new { id = dto.Id }, dto);
    }

    // -------------------------------------------------------------------------
    // GET /api/v1/documents?entityType=&entityId=  — list documents for an entity
    // -------------------------------------------------------------------------

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<DocumentDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<DocumentDto>>> List(
        [FromQuery] string entityType,
        [FromQuery] long entityId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entityType))
            return BadRequest(new { error = "entityType query parameter is required." });

        var portfolioId = GetPortfolioId();
        var normalizedEntityType = entityType.Trim();
        if (!TryParseTarget(normalizedEntityType, out var target))
            return BadRequest(new { error = $"entityType '{normalizedEntityType}' is not a supported document target." });
        normalizedEntityType = target.ToString();

        // Tenant guard: a tenant may only list documents for a WorkOrder they own. Returning an empty
        // list (rather than 403) keeps the response shape identical for any non-owned/foreign entity.
        if (HasWorkspaceMembership())
        {
            if (!TryReadWorkspaceScope(out var scope) ||
                !await StaffMayAccessTargetAsync(scope, target, entityId, write: false, ct))
                return Ok(Array.Empty<DocumentDto>());
        }
        else if (!await TenantMayAccessEntityAsync(normalizedEntityType, entityId, portfolioId, ct))
        {
            return Ok(Array.Empty<DocumentDto>());
        }

        var docs = await _documents.ListAsync(portfolioId, normalizedEntityType, entityId, ct);
        return Ok(docs);
    }

    // -------------------------------------------------------------------------
    // GET /api/v1/documents/{id}/file  — stream the stored blob
    // -------------------------------------------------------------------------

    [HttpGet("{id:int}/file", Name = nameof(GetFile))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetFile(int id, [FromQuery] bool thumb = false, CancellationToken ct = default)
    {
        var portfolioId = GetPortfolioId();
        var row = await _documents.FindAsync(portfolioId, id, ct);
        if (row is null)
            return NotFound(new { error = "Document not found." });

        // Tenant guard: a tenant may only download a document attached to a WorkOrder they own. Without
        // this, any tenant could stream every document in the portfolio by id (other tenants' lease PDFs,
        // ID scans, owner financials). 404 (not 403) so a foreign id is indistinguishable from a missing one.
        if (HasWorkspaceMembership())
        {
            if (!TryReadWorkspaceScope(out var scope) ||
                !await StaffMayAccessStoredFileTargetAsync(scope, row, write: false, ct))
                return NotFound(new { error = "Document not found." });
        }
        else if (!await TenantMayAccessEntityAsync(row.EntityType, row.EntityId, portfolioId, ct))
        {
            return NotFound(new { error = "Document not found." });
        }

        Stream stream;
        try
        {
            stream = await _storage.DownloadAsync(row.FilePath, ct);
        }
        catch
        {
            return NotFound(new { error = "File not found on storage." });
        }

        // Serve defensively: never let the browser execute active content on our origin.
        // Images and PDFs are rendered inline; everything else is forced to download.
        Response.Headers["X-Content-Type-Options"] = "nosniff";

        var safeName = Uri.EscapeDataString(row.FileName);

        if (thumb && row.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            byte[] srcBytes;
            using (var ms = new MemoryStream())
            {
                await stream.CopyToAsync(ms, ct);
                srcBytes = ms.ToArray();
            }

            var thumbBytes = ThumbnailResizer.ResizeToJpeg(srcBytes);
            if (thumbBytes is not null)
            {
                var baseName = Path.GetFileNameWithoutExtension(row.FileName);
                if (string.IsNullOrWhiteSpace(baseName))
                    baseName = $"document-{id}";
                var thumbName = Uri.EscapeDataString($"{baseName}-thumb.jpg");

                Response.Headers["Cache-Control"] = "private, max-age=86400";
                Response.Headers["Content-Disposition"] = $"inline; filename=\"{thumbName}\"";
                return File(thumbBytes, "image/jpeg");
            }

            stream = new MemoryStream(srcBytes);
        }

        if (InlineSafeContentTypes.Contains(row.ContentType))
        {
            Response.Headers["Content-Disposition"] = $"inline; filename=\"{safeName}\"";
            return File(stream, row.ContentType);
        }

        Response.Headers["Content-Disposition"] = $"attachment; filename=\"{safeName}\"";
        return File(stream, "application/octet-stream");
    }

    // -------------------------------------------------------------------------
    // DELETE /api/v1/documents/{id}  — soft-delete + durable post-commit blob cleanup
    // -------------------------------------------------------------------------

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(
        int id,
        [FromQuery] string clientOperationId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientOperationId) || clientOperationId.Trim().Length > 160)
            return BadRequest(new { error = "clientOperationId is required and cannot exceed 160 characters." });

        var portfolioId = GetPortfolioId();
        var isStaff = HasWorkspaceMembership();
        WorkspaceReadScope? staffScope = null;
        if (isStaff)
        {
            if (!TryReadWorkspaceScope(out var scope))
                return NotFound(new { error = "Document not found." });
            staffScope = scope;
        }
        var tenantId = isStaff ? null : await ResolveTenantIdAsync(portfolioId, ct);
        if (!isStaff)
            return NotFound(new { error = "Document not found." });
        var deleted = await _documents.DeleteAsync(
            portfolioId,
            id,
            GetUserId(),
            tenantId,
            isStaff,
            staffScope,
            clientOperationId,
            ct);
        return deleted ? NoContent() : NotFound(new { error = "Document not found." });
    }

    // -------------------------------------------------------------------------
    // Tenant access guard (within-portfolio)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Whether the CALLER may act on a document attached to <paramref name="entityType"/>/
    /// <paramref name="entityId"/>. Staff callers always may — portfolio scope is enforced elsewhere —
    /// even if the same identity also has tenant relationships. A tenant-only caller may ONLY when the
    /// entity is a <c>WorkOrder</c> that belongs to that tenant (their single legitimate document surface —
    /// a maintenance-request photo). Every other entity type, or a work order owned by a different tenant,
    /// is denied. Fail-closed: an unknown/missing entity reference returns <c>false</c> for a tenant.
    /// </summary>
    private async Task<bool> TenantMayAccessEntityAsync(
        string? entityType, long? entityId, int portfolioId, CancellationToken ct)
    {
        if (HasWorkspaceMembership())
        {
            // Staff/owner/manager/agent: not tenant-constrained (portfolio scope already applied).
            return true;
        }

        if (entityId is null || string.IsNullOrWhiteSpace(entityType))
            return false;

        // A tenant's only document surface is a photo on their OWN maintenance request.
        if (!string.Equals(entityType.Trim(), "WorkOrder", StringComparison.OrdinalIgnoreCase))
            return false;

        var accessContextId = GetAccessContextId();
        return await _db.EffectiveTenantAccess.AnyAsync(access =>
            access.AccessContextId == accessContextId && access.PortfolioId == portfolioId &&
            _db.WorkOrders.Any(workOrder =>
                workOrder.Id == entityId.Value && workOrder.PortfolioId == portfolioId &&
                workOrder.TenantId == access.TenantId &&
                workOrder.LeaseManagementId == access.LeaseManagementId), ct);
    }

    private Task<int?> ResolveTenantIdAsync(int portfolioId, CancellationToken ct)
    {
        var accessContextId = GetAccessContextId();
        return _db.EffectiveTenantAccess
            .Where(access => access.AccessContextId == accessContextId &&
                access.PortfolioId == portfolioId)
            .OrderBy(access => access.LeaseManagementPartyId)
            .Select(access => (int?)access.TenantId)
            .FirstOrDefaultAsync(ct);
    }

    private bool TryReadWorkspaceScope(out WorkspaceReadScope scope)
    {
        scope = default;
        if (!TryGetActiveAccessContext(out var active))
        {
            return false;
        }

        scope = new WorkspaceReadScope(
            active.PortfolioId,
            active.UserId,
            active.SessionId,
            active.AccessContextId,
            active.AccessRevision);
        return true;
    }

    private Task<bool> StaffMayAccessTargetAsync(
        WorkspaceReadScope scope,
        StoredDocumentTarget target,
        long entityId,
        bool write,
        CancellationToken ct)
    {
        var utcNow = DateTime.UtcNow;
        if (target == StoredDocumentTarget.WorkOrder)
        {
            var workOrderCapabilities = write
                ? new[] { CapabilityKeys.WorkManage, CapabilityKeys.AssignedWorkUpdate }
                : new[] { CapabilityKeys.WorkRead, CapabilityKeys.AssignedWorkRead };
            return _db.WorkOrders.AsNoTracking()
                .WhereAuthorized(_db, scope, workOrderCapabilities, utcNow)
                .AnyAsync(workOrder => workOrder.Id == entityId, ct);
        }

        IReadOnlyCollection<string>? capabilities = target switch
        {
            StoredDocumentTarget.Property or StoredDocumentTarget.Unit or StoredDocumentTarget.Tenant =>
                write ? [CapabilityKeys.RentalsManage] : [CapabilityKeys.RentalsRead],
            StoredDocumentTarget.LeaseAgreement or StoredDocumentTarget.LegalDocumentArtifact =>
                write
                    ? [CapabilityKeys.RentalsManage, CapabilityKeys.LeasingAgreementsPrepare]
                    : [CapabilityKeys.RentalsRead, CapabilityKeys.LeasingTermsRead],
            StoredDocumentTarget.TenantAccount or StoredDocumentTarget.TenantLedgerEntry =>
                write
                    ? [CapabilityKeys.MoneyChargesManage, CapabilityKeys.MoneyPaymentsManage]
                    : [CapabilityKeys.MoneyBalancesRead],
            StoredDocumentTarget.Expense =>
                write ? [CapabilityKeys.MoneyExpensesManage] : [CapabilityKeys.MoneyBalancesRead],
            StoredDocumentTarget.WorkOrder or StoredDocumentTarget.Appointment or StoredDocumentTarget.Inspection =>
                write ? [CapabilityKeys.WorkManage] : [CapabilityKeys.WorkRead],
            StoredDocumentTarget.Vendor =>
                write ? [CapabilityKeys.WorkManage] : [CapabilityKeys.WorkRead],
            StoredDocumentTarget.SecurityDepositAccount =>
                write
                    ? [CapabilityKeys.MoneyDepositsManage]
                    : [CapabilityKeys.MoneyBalancesRead, CapabilityKeys.LeasingDepositsRead],
            StoredDocumentTarget.OwnerEntity =>
                write ? [CapabilityKeys.RentalsManage] : [CapabilityKeys.MoneyOwnerReportsRead],
            _ => null,
        };
        if (capabilities is null) return Task.FromResult(false);

        var properties = _db.Properties.AsNoTracking()
            .WhereAuthorized(_db, scope, capabilities, utcNow);
        var leaseManagements = _db.LeaseManagements.AsNoTracking()
            .WhereAuthorized(_db, scope, capabilities, utcNow);
        return target switch
        {
            StoredDocumentTarget.Property => properties.AnyAsync(property => property.Id == entityId, ct),
            StoredDocumentTarget.Unit => _db.Units.AsNoTracking().AnyAsync(unit =>
                unit.Id == entityId && properties.Any(property => property.Id == unit.PropertyId), ct),
            StoredDocumentTarget.Tenant => _db.LeaseManagementParties.AsNoTracking().AnyAsync(party =>
                party.TenantId == entityId &&
                properties.Any(property => property.Id == party.LeaseManagement!.PropertyId), ct),
            StoredDocumentTarget.LeaseAgreement => _db.LeaseAgreements.AsNoTracking().AnyAsync(agreement =>
                agreement.Id == entityId && agreement.PortfolioId == scope.PortfolioId &&
                leaseManagements.Any(management => management.Id == agreement.LeaseManagementId), ct),
            StoredDocumentTarget.LegalDocumentArtifact => _db.LegalDocumentArtifacts.AsNoTracking()
                .AnyAsync(artifact =>
                    artifact.Id == entityId && artifact.PortfolioId == scope.PortfolioId &&
                    (_db.LeaseAgreements.AsNoTracking().Any(agreement =>
                         agreement.PortfolioId == artifact.PortfolioId &&
                         (agreement.IssuedArtifactId == artifact.Id ||
                          agreement.ExecutedArtifactId == artifact.Id) &&
                         leaseManagements.Any(management => management.Id == agreement.LeaseManagementId)) ||
                     _db.LeaseAddenda.AsNoTracking().Any(addendum =>
                         addendum.PortfolioId == artifact.PortfolioId &&
                         (addendum.IssuedArtifactId == artifact.Id ||
                          addendum.ExecutedArtifactId == artifact.Id) &&
                         leaseManagements.Any(management => management.Id == addendum.LeaseManagementId))), ct),
            StoredDocumentTarget.TenantAccount => _db.TenantAccounts.AsNoTracking().AnyAsync(account =>
                account.Id == entityId && account.PortfolioId == scope.PortfolioId &&
                leaseManagements.Any(management => management.Id == account.LeaseManagementId), ct),
            StoredDocumentTarget.TenantLedgerEntry => _db.TenantLedgerEntries.AsNoTracking().AnyAsync(entry =>
                entry.Id == entityId && entry.PortfolioId == scope.PortfolioId &&
                _db.TenantAccounts.AsNoTracking().Any(account =>
                    account.Id == entry.TenantAccountId && account.PortfolioId == entry.PortfolioId &&
                    leaseManagements.Any(management => management.Id == account.LeaseManagementId)), ct),
            StoredDocumentTarget.Expense => _db.Expenses.AsNoTracking().AnyAsync(expense =>
                expense.Id == entityId && properties.Any(property => property.Id == expense.PropertyId), ct),
            StoredDocumentTarget.WorkOrder => _db.WorkOrders.AsNoTracking().AnyAsync(workOrder =>
                workOrder.Id == entityId && properties.Any(property => property.Id == workOrder.PropertyId), ct),
            StoredDocumentTarget.Appointment => _db.Appointments.AsNoTracking().AnyAsync(appointment =>
                appointment.Id == entityId && properties.Any(property => property.Id == appointment.PropertyId), ct),
            StoredDocumentTarget.Inspection => _db.Inspections.AsNoTracking().AnyAsync(inspection =>
                inspection.Id == entityId && properties.Any(property => property.Id == inspection.PropertyId), ct),
            StoredDocumentTarget.Vendor => _db.Vendors.AsNoTracking().AnyAsync(vendor =>
                vendor.Id == entityId && vendor.PortfolioId == scope.PortfolioId &&
                (vendor.WorkOrders.Any(workOrder =>
                     workOrder.PortfolioId == vendor.PortfolioId &&
                     properties.Any(property => property.Id == workOrder.PropertyId)) ||
                 vendor.Expenses.Any(expense =>
                     expense.PortfolioId == vendor.PortfolioId && expense.PropertyId != null &&
                     properties.Any(property => property.Id == expense.PropertyId))), ct),
            StoredDocumentTarget.SecurityDepositAccount => _db.SecurityDepositAccounts.AsNoTracking()
                .AnyAsync(deposit =>
                    deposit.Id == entityId && deposit.PortfolioId == scope.PortfolioId &&
                    _db.TenantAccounts.AsNoTracking().Any(account =>
                        account.Id == deposit.TenantAccountId && account.PortfolioId == deposit.PortfolioId &&
                        leaseManagements.Any(management => management.Id == account.LeaseManagementId)), ct),
            StoredDocumentTarget.OwnerEntity => _db.OwnerEntities.AsNoTracking().AnyAsync(owner =>
                owner.Id == entityId && owner.PortfolioId == scope.PortfolioId &&
                _db.PropertyOwnerships.Any(ownership =>
                    ownership.PortfolioId == scope.PortfolioId
                    && ownership.OwnerEntityId == owner.Id
                    && ownership.EffectiveFromUtc <= DateTime.UtcNow
                    && (ownership.EffectiveToUtc == null || ownership.EffectiveToUtc > DateTime.UtcNow)
                    && properties.Any(property => property.Id == ownership.PropertyId)), ct),
            _ => Task.FromResult(false),
        };
    }

    private Task<bool> StaffMayAccessStoredFileTargetAsync(
        WorkspaceReadScope scope,
        Core.Entities.StoredFile row,
        bool write,
        CancellationToken ct)
    {
        if (string.Equals(row.EntityType?.Trim(), DocumentTemplateEntityType, StringComparison.OrdinalIgnoreCase))
        {
            return !write && row.EntityId is >= int.MinValue and <= int.MaxValue
                ? StaffMayAccessDocumentTemplateAsync(scope, (int)row.EntityId.Value, row.Id, ct)
                : Task.FromResult(false);
        }

        return TryParseTarget(row.EntityType ?? string.Empty, out var target)
            ? StaffMayAccessTargetAsync(scope, target, row.EntityId ?? 0, write, ct)
            : Task.FromResult(false);
    }

    private Task<bool> StaffMayAccessDocumentTemplateAsync(
        WorkspaceReadScope scope,
        int documentTemplateId,
        int storedFileId,
        CancellationToken ct)
    {
        var utcNow = DateTime.UtcNow;
        var assignments = _db.MembershipRoleAssignments.AsNoTracking()
            .Where(assignment =>
                assignment.PortfolioId == scope.PortfolioId &&
                assignment.Status == MembershipRoleAssignmentStatus.Active &&
                assignment.EffectiveFromUtc <= utcNow &&
                (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > utcNow) &&
                assignment.SuspendedAtUtc == null &&
                assignment.RevokedAtUtc == null &&
                assignment.WorkspaceMembership != null &&
                assignment.WorkspaceMembership.AccessContextId == scope.AccessContextId &&
                assignment.WorkspaceMembership.PortfolioId == scope.PortfolioId &&
                assignment.WorkspaceMembership.Status == WorkspaceMembershipStatus.Active &&
                assignment.WorkspaceMembership.EffectiveFromUtc <= utcNow &&
                (assignment.WorkspaceMembership.EffectiveToUtc == null ||
                 assignment.WorkspaceMembership.EffectiveToUtc > utcNow) &&
                assignment.WorkspaceMembership.SuspendedAtUtc == null &&
                assignment.WorkspaceMembership.RevokedAtUtc == null &&
                assignment.WorkspaceMembership.AccessContext != null &&
                assignment.WorkspaceMembership.AccessContext.UserId == scope.UserId &&
                assignment.WorkspaceMembership.AccessContext.PortfolioId == scope.PortfolioId &&
                assignment.WorkspaceMembership.AccessContext.AccessRevision == scope.AccessRevision &&
                assignment.WorkspaceMembership.AccessContext.Status == WorkspaceAccessContextStatus.Active &&
                assignment.WorkspaceMembership.AccessContext.SuspendedAtUtc == null &&
                assignment.WorkspaceMembership.AccessContext.RevokedAtUtc == null &&
                assignment.RoleProfile != null &&
                assignment.RoleProfile.Capabilities.Any(profileCapability =>
                    profileCapability.CapabilityDefinition != null &&
                    DocumentTemplateCapabilityKeys.Contains(profileCapability.CapabilityDefinition.Key) &&
                    profileCapability.CapabilityDefinition.AuthorizationTargetKind ==
                    CapabilityAuthorizationTargetKind.Property));

        return _db.DocumentTemplates.AsNoTracking().AnyAsync(template =>
            template.Id == documentTemplateId &&
            template.PortfolioId == scope.PortfolioId &&
            (template.OriginalStoredFileId == storedFileId || template.CompiledStoredFileId == storedFileId) &&
            (template.PropertyId.HasValue
                ? assignments.Any(assignment =>
                    assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                    assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties &&
                    assignment.SelectedProperties.Any(selected =>
                        selected.PortfolioId == scope.PortfolioId &&
                        selected.PropertyId == template.PropertyId.Value))
                : assignments.Any(assignment =>
                    assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                    assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties &&
                    assignment.SelectedProperties.Any(selected =>
                        selected.PortfolioId == scope.PortfolioId))), ct);
    }


    private static string Fingerprint<T>(T request)
    {
        var json = JsonSerializer.Serialize(request);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    private static bool TryParseTarget(string value, out StoredDocumentTarget target)
    {
        target = default;
        return !int.TryParse(value, out _)
            && Enum.TryParse(value, ignoreCase: true, out target)
            && Enum.IsDefined(target);
    }

    // -------------------------------------------------------------------------
    // Validation
    // -------------------------------------------------------------------------

    private (bool IsValid, string? Error) ValidateDocumentUpload(string? fileName, string contentType, long sizeBytes)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return (false, "File name is required.");

        if (fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
            return (false, "File name must not contain path separators or '..'.");

        var maxBytes = _uploadSettings.MaxFileSizeBytes > 0 ? _uploadSettings.MaxFileSizeBytes : 52_428_800;
        if (sizeBytes > maxBytes)
            return (false, $"File size {sizeBytes:N0} bytes exceeds the {maxBytes:N0}-byte limit.");

        // Accept anything already in the scan allowlist …
        var mimeInScanList = _uploadSettings.AllowedMimeTypes is { Length: > 0 }
            && _uploadSettings.AllowedMimeTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase);

        // … or in the document-specific extension list.
        var docMimeAllowed = DocumentMimeTypes.Contains(contentType);

        var ext = Path.GetExtension(fileName);
        var extAllowed = !string.IsNullOrEmpty(ext) && DocumentExtensions.Contains(ext);

        if (!mimeInScanList && !docMimeAllowed && !extAllowed)
            return (false, $"Content type '{contentType}' is not permitted for document uploads.");

        return (true, null);
    }
}
