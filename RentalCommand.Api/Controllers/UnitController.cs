using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Scanning;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Configuration;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// CRUD for units. Units are scoped through their owning property's portfolio (the caller's
/// <c>portfolioId</c> claim). List supports <c>?propertyId&amp;skip&amp;take&amp;search&amp;sort</c>.
/// Removal is a soft-delete.
/// </summary>
[ApiController]
[Route("api/v1/units")]
[Produces("application/json")]
public class UnitController : ManagementControllerBase
{
    private readonly IUnitService _service;
    private readonly IUnitDashboardService _dashboard;
    private readonly IListingWorkspaceService _listings;
    private readonly IWorkspaceAuthorizationEvaluator _authorization;
    private readonly TimeProvider _timeProvider;
    private readonly UploadSettings _uploadSettings;

    public UnitController(
        IUnitService service,
        IUnitDashboardService dashboard,
        IListingWorkspaceService listings,
        IWorkspaceAuthorizationEvaluator authorization,
        TimeProvider timeProvider,
        IOptions<UploadSettings> uploadSettings)
    {
        _service = service;
        _dashboard = dashboard;
        _listings = listings;
        _authorization = authorization;
        _timeProvider = timeProvider;
        _uploadSettings = uploadSettings.Value;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<UnitResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<UnitResponse>>> List(
        [FromQuery] UnitListQuery query, [FromQuery] int? propertyId, CancellationToken ct)
    {
        var items = await _service.ListAsync(GetPortfolioId(), propertyId, query, ct);
        return Ok(items);
    }

    /// <summary>
    /// Units with cheap health badges for the <c>/units</c> page (spec section 10): status, open-WO count,
    /// days-until-lease-end, a unit-document count, and a simplified stage label. One projection query —
    /// the list never calls the per-unit dashboard per row.
    /// </summary>
    [HttpGet("list-with-health")]
    [ProducesResponseType(typeof(IReadOnlyList<UnitHealthResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<UnitHealthResponse>>> ListWithHealth(
        [FromQuery] ListQuery query, [FromQuery] int? propertyId, CancellationToken ct)
    {
        var items = await _service.ListWithHealthAsync(GetPortfolioId(), propertyId, query, ct);
        return Ok(items);
    }

    [HttpGet("list-with-health/page")]
    [ProducesResponseType(typeof(UnitHealthListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<UnitHealthListResponse>> ListWithHealthPage(
        [FromQuery] UnitHealthListQuery query, CancellationToken ct)
    {
        var page = await _service.ListWithHealthPageAsync(GetPortfolioId(), query, ct);
        return Ok(page);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(UnitResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UnitResponse>> Get(int id, CancellationToken ct)
    {
        var item = await _service.GetAsync(GetPortfolioId(), id, ct);
        return item == null ? NotFound(new { error = "Unit not found" }) : Ok(item);
    }

    /// <summary>
    /// At-a-glance Unit Command Center aggregate (header chips, current lease/tenant, derived lifecycle
    /// stage + next-best-action, capped overview, recent timeline). Portfolio-scoped; 404 when the unit
    /// is not in the caller's portfolio. Per-tab heavy data loads separately via the filtered endpoints.
    /// </summary>
    [HttpGet("{id:int}/dashboard")]
    [ProducesResponseType(typeof(UnitDashboardResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UnitDashboardResponse>> Dashboard(int id, CancellationToken ct)
    {
        var dashboard = await _dashboard.GetDashboardAsync(GetPortfolioId(), id, ct);
        return dashboard == null ? NotFound(new { error = "Unit not found" }) : Ok(dashboard);
    }

    [HttpGet("{id:int}/listing-workspace")]
    [ProducesResponseType(typeof(ListingWorkspaceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ListingWorkspaceResponse?>> GetListingWorkspace(int id, CancellationToken ct)
    {
        if (!await CanManageListingAsync(id, ct)) return Forbid();
        if (!await _listings.UnitExistsInPortfolioAsync(GetPortfolioId(), id, ct))
            return NotFound(new { error = "Unit not found" });

        var listing = await _listings.GetAsync(GetPortfolioId(), id, ct);
        return Ok(listing);
    }

    [HttpPost("{id:int}/listing-workspace/generate")]
    [ProducesResponseType(typeof(ListingWorkspaceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ListingWorkspaceResponse>> GenerateListingWorkspace(int id, CancellationToken ct)
    {
        if (!await CanManageListingAsync(id, ct)) return Forbid();
        var listing = await _listings.GenerateAsync(GetPortfolioId(), id, GetUserId(), ct);
        return listing == null ? NotFound(new { error = "Unit not found" }) : Ok(listing);
    }

    [HttpPut("{id:int}/listing-workspace")]
    [ProducesResponseType(typeof(ListingWorkspaceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ListingWorkspaceResponse>> SaveListingWorkspace(
        int id, [FromBody] SaveListingWorkspaceRequest request, CancellationToken ct)
    {
        if (!await CanManageListingAsync(id, ct)) return Forbid();
        var listing = await _listings.SaveAsync(GetPortfolioId(), id, request, GetUserId(), ct);
        return listing == null ? NotFound(new { error = "Unit not found" }) : Ok(listing);
    }

    [HttpPost("{id:int}/listing-workspace/photos/{photoId:int}/content")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(ListingWorkspaceResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ListingWorkspaceResponse>> AttachListingPhoto(
        int id, int photoId, IFormFile file, [FromForm] string clientOperationId, CancellationToken ct)
    {
        if (!await CanManageListingAsync(id, ct)) return Forbid();
        if (file is null || file.Length == 0) return BadRequest(new { error = "A non-empty photo is required." });
        if (string.IsNullOrWhiteSpace(clientOperationId) || clientOperationId.Trim().Length > 160)
            return BadRequest(new { error = "clientOperationId is required and cannot exceed 160 characters." });

        var fileName = DiskFileStorage.SanitizeFileName(file.FileName);
        var contentType = file.ContentType?.Trim().ToLowerInvariant() ?? string.Empty;
        byte[] bytes;
        await using (var stream = file.OpenReadStream())
        await using (var buffer = new MemoryStream())
        {
            await stream.CopyToAsync(buffer, ct);
            bytes = buffer.ToArray();
        }
        var header = bytes.Length == 0 ? null : bytes[..Math.Min(16, bytes.Length)];
        var (valid, error) = FileUploadValidator.ValidateScanUpload(
            fileName, contentType, bytes.LongLength, _uploadSettings, header);
        if (!valid || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = valid ? "Listing photos must be image files." : error });

        var workspace = await _listings.AttachPhotoAsync(GetPortfolioId(), id, photoId, GetUserId(),
            clientOperationId, fileName, contentType, bytes, ct);
        return workspace is null ? NotFound(new { error = "Listing photo was not found" }) : Ok(workspace);
    }

    [HttpPatch("{id:int}/listing-workspace/photos/{photoId:int}")]
    [ProducesResponseType(typeof(ListingWorkspaceResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ListingWorkspaceResponse>> UpdateListingPhoto(
        int id, int photoId, [FromBody] UpdateListingPhotoRequest request, CancellationToken ct)
    {
        if (!await CanManageListingAsync(id, ct)) return Forbid();
        var workspace = await _listings.UpdatePhotoAsync(GetPortfolioId(), id, photoId, request, GetUserId(), ct);
        return workspace is null ? NotFound(new { error = "Listing photo was not found" }) : Ok(workspace);
    }

    [HttpDelete("{id:int}/listing-workspace/photos/{photoId:int}/content")]
    [ProducesResponseType(typeof(ListingWorkspaceResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ListingWorkspaceResponse>> RemoveListingPhoto(
        int id, int photoId, CancellationToken ct)
    {
        if (!await CanManageListingAsync(id, ct)) return Forbid();
        var workspace = await _listings.RemovePhotoAsync(GetPortfolioId(), id, photoId, GetUserId(), ct);
        return workspace is null ? NotFound(new { error = "Listing photo was not found" }) : Ok(workspace);
    }

    [HttpPut("{id:int}/listing-workspace/photos/order")]
    [ProducesResponseType(typeof(ListingWorkspaceResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ListingWorkspaceResponse>> ReorderListingPhotos(
        int id, [FromBody] ReorderListingPhotosRequest request, CancellationToken ct)
    {
        if (!await CanManageListingAsync(id, ct)) return Forbid();
        var workspace = await _listings.ReorderPhotosAsync(GetPortfolioId(), id, request, GetUserId(), ct);
        return workspace is null ? NotFound(new { error = "Listing workspace was not found" }) : Ok(workspace);
    }

    [HttpGet("{id:int}/listing-workspace/photos/{photoId:int}/content")]
    public async Task<IActionResult> GetListingPhoto(int id, int photoId, CancellationToken ct)
    {
        if (!await CanManageListingAsync(id, ct)) return Forbid();
        var file = await _listings.OpenPhotoAsync(GetPortfolioId(), id, photoId, ct);
        if (file is null) return NotFound(new { error = "Listing photo was not found" });
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Cache-Control"] = "private, max-age=3600";
        return File(file.Content, file.ContentType, enableRangeProcessing: true);
    }

    [HttpPost("{id:int}/listing-workspace/publications/{publicationId:int}/connected/prepare")]
    [ProducesResponseType(typeof(ListingWorkspaceResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ListingWorkspaceResponse>> PrepareConnectedListing(
        int id, int publicationId, CancellationToken ct)
    {
        if (!await CanManageListingAsync(id, ct)) return Forbid();
        var workspace = await _listings.PrepareConnectedAsync(
            GetPortfolioId(), id, publicationId, GetUserId(), ct);
        return workspace is null ? NotFound(new { error = "Connected listing publication not found" }) : Ok(workspace);
    }

    [HttpPost("{id:int}/listing-workspace/publications/{publicationId:int}/connected/publish")]
    [ProducesResponseType(typeof(ListingWorkspaceResponse), StatusCodes.Status200OK)]
    public Task<ActionResult<ListingWorkspaceResponse>> PublishConnectedListing(
        int id, int publicationId, [FromBody] ConnectedListingCommandRequest request, CancellationToken ct)
        => RunConnectedCommandAsync(id, publicationId, request,
            _listings.PublishConnectedAsync, ct);

    [HttpPost("{id:int}/listing-workspace/publications/{publicationId:int}/connected/update")]
    [ProducesResponseType(typeof(ListingWorkspaceResponse), StatusCodes.Status200OK)]
    public Task<ActionResult<ListingWorkspaceResponse>> UpdateConnectedListing(
        int id, int publicationId, [FromBody] ConnectedListingCommandRequest request, CancellationToken ct)
        => RunConnectedCommandAsync(id, publicationId, request,
            _listings.UpdateConnectedAsync, ct);

    [HttpPost("{id:int}/listing-workspace/publications/{publicationId:int}/connected/unpublish")]
    [ProducesResponseType(typeof(ListingWorkspaceResponse), StatusCodes.Status200OK)]
    public Task<ActionResult<ListingWorkspaceResponse>> UnpublishConnectedListing(
        int id, int publicationId, [FromBody] ConnectedListingCommandRequest request, CancellationToken ct)
        => RunConnectedCommandAsync(id, publicationId, request,
            _listings.UnpublishConnectedAsync, ct);

    private async Task<ActionResult<ListingWorkspaceResponse>> RunConnectedCommandAsync(
        int unitId,
        int publicationId,
        ConnectedListingCommandRequest request,
        Func<int, int, int, string, int, CancellationToken, Task<ListingWorkspaceResponse?>> command,
        CancellationToken ct)
    {
        if (!await CanManageListingAsync(unitId, ct)) return Forbid();
        var workspace = await command(GetPortfolioId(), unitId, publicationId,
            request.ClientOperationId, GetUserId(), ct);
        return workspace is null ? NotFound(new { error = "Connected listing publication not found" }) : Ok(workspace);
    }

    [HttpPost("{id:int}/listing-workspace/publications/{publicationId:int}/signals")]
    [ProducesResponseType(typeof(ExternalListingSignalResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ExternalListingSignalResponse>> IngestListingSignal(
        int id, int publicationId, [FromBody] IngestExternalListingSignalRequest request, CancellationToken ct)
    {
        if (!await CanManageListingAsync(id, ct)) return Forbid();
        var signal = await _listings.IngestSignalAsync(GetPortfolioId(), id, publicationId, request, ct);
        return signal is null ? NotFound(new { error = "Listing publication not found" }) : Ok(signal);
    }

    [HttpPost("{id:int}/listing-workspace/signals/{signalId:int}/confirm")]
    [ProducesResponseType(typeof(ListingWorkspaceResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ListingWorkspaceResponse>> ConfirmListingSignal(
        int id, int signalId, [FromQuery] bool accept = true, CancellationToken ct = default)
    {
        if (!await CanManageListingAsync(id, ct)) return Forbid();
        var workspace = await _listings.ConfirmSignalAsync(GetPortfolioId(), id, signalId, accept, GetUserId(), ct);
        return workspace is null ? NotFound(new { error = "Listing signal not found" }) : Ok(workspace);
    }

    private Task<bool> CanManageListingAsync(int unitId, CancellationToken ct)
    {
        var active = GetActiveAccessContext();
        return _authorization.HasCapabilityAsync(
            active,
            CapabilityKeys.LeasingListingsManage,
            new UnitCapabilityAuthorizationTarget(active.PortfolioId, unitId),
            _timeProvider.GetUtcNow().UtcDateTime,
            ct);
    }

    /// <summary>
    /// The unit's full history (deep timeline tab): a bounded <c>AuditLog</c> union over the unit and its
    /// children, newest first, paged via <c>?skip&amp;take</c>. Out-of-scope units return an empty list.
    /// </summary>
    [HttpGet("{id:int}/timeline")]
    [ProducesResponseType(typeof(IReadOnlyList<AuditEntryResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AuditEntryResponse>>> Timeline(
        int id, [FromQuery] ListQuery query, CancellationToken ct)
    {
        var items = await _dashboard.GetTimelineAsync(GetPortfolioId(), id, query.NormalizedSkip, query.NormalizedTake, ct);
        return Ok(items);
    }

    [HttpPost]
    [ProducesResponseType(typeof(UnitResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UnitResponse>> Create([FromBody] CreateUnitRequest request, CancellationToken ct)
    {
        var created = await _service.CreateAsync(GetPortfolioId(), request, ct);
        return created == null
            ? NotFound(new { error = "Property not found in this portfolio" })
            : CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(UnitResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UnitResponse>> Update(int id, [FromBody] UpdateUnitRequest request, CancellationToken ct)
    {
        var updated = await _service.UpdateAsync(GetPortfolioId(), id, request, ct);
        return updated == null ? NotFound(new { error = "Unit not found" }) : Ok(updated);
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var deleted = await _service.DeleteAsync(GetPortfolioId(), id, ct);
        return deleted ? NoContent() : NotFound(new { error = "Unit not found" });
    }
}
