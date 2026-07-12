using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalCommand.Api.Imaging;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;
using RentalCommand.Api.Auth;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Api.Controllers;

/// <summary>
/// Base for controllers that operate within one selected workspace. The compact JWT coordinates
/// are validated against the current session/context/revision by middleware; controllers consume
/// that database-backed result rather than trusting a portfolio or relationship claim.
/// </summary>
[Authorize]
public abstract class AuthenticatedPortfolioControllerBase : ControllerBase
{
    /// <summary>
    /// Portfolio id from the validated active access context. Throws when the context is missing so
    /// a request with no workspace scope can never run a tenant-scoped query with
    /// <c>portfolioId == 0</c> (which would read across tenants). Use <see cref="TryGetPortfolioId"/>
    /// for code paths that legitimately tolerate an unscoped caller.
    /// </summary>
    /// <exception cref="MissingAuthContextException">The canonical access context is absent or invalid.</exception>
    protected int GetPortfolioId()
    {
        if (!TryGetPortfolioId(out var id))
        {
            throw new MissingAuthContextException("Missing portfolio context");
        }

        return id;
    }

    /// <summary>
    /// Attempts to read the validated workspace without throwing. Returns <c>false</c> (and
    /// <paramref name="portfolioId"/> = 0) when the caller has no portfolio scope.
    /// </summary>
    protected bool TryGetPortfolioId(out int portfolioId)
    {
        if (HttpContext.Items.TryGetValue(CanonicalAccessContextHttpItem.Key, out var value) &&
            value is ActiveAccessContext accessContext)
        {
            portfolioId = accessContext.PortfolioId;
            return true;
        }

        portfolioId = 0;
        return false;
    }

    /// <summary>Int user id parsed from the <c>sub</c>/NameIdentifier claim.</summary>
    protected int GetUserId()
    {
        if (!HttpContext.Items.TryGetValue(CanonicalAccessContextHttpItem.Key, out var value) ||
            value is not ActiveAccessContext accessContext)
        {
            throw new MissingAuthContextException("Invalid user context");
        }

        return accessContext.UserId;
    }

    /// <summary>
    /// True when the validated context has a Team membership. This is only a relationship-shape
    /// signal; endpoint admission still belongs to canonical capability authorization.
    /// </summary>
    protected bool HasWorkspaceMembership() =>
        HttpContext.Items.TryGetValue(CanonicalAccessContextHttpItem.Key, out var value) &&
        value is ActiveAccessContext { WorkspaceMembershipId: not null };

    /// <summary>
    /// Tenant id from the <c>tenantId</c> claim, or <c>null</c> when the caller is not a tenant
    /// (landlord/staff/owner). Used to constrain otherwise portfolio-wide management reads to a
    /// tenant's own records when a tenant calls them.
    /// </summary>
    protected int? GetTenantIdOrNull()
    {
        return null;
    }

    // Content types we trust to render inline; anything else downloads as octet-stream so an uploaded
    // html/svg can't execute on the app origin. Shared by every scanned-document endpoint.
    private static readonly HashSet<string> InlineSafeContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf", "image/jpeg", "image/png", "image/gif", "image/webp", "image/heic"
    };

    /// <summary>
    /// Serves the original scanned document a record was created from: the latest <c>StoredFile</c>
    /// re-keyed to (<paramref name="entityType"/>, <paramref name="entityId"/>) by
    /// the atomic scan-confirm finalizer. When <paramref name="thumb"/> is set and the file is an image,
    /// returns a resized JPEG preview. Inline for known-safe types, attachment otherwise. The lookup is
    /// portfolio-scoped (from the JWT claim), so it can't reach another tenant's file (IDOR-safe).
    /// </summary>
    protected async Task<IActionResult> ServeEntityScanAsync(
        RentalCommandDbContext db, IFileStorage files, string entityType, int entityId, bool thumb, CancellationToken ct)
    {
        var storedFile = await db.FindLatestEntityFileAsync(GetPortfolioId(), entityType, entityId, ct);
        if (storedFile is null)
        {
            return NotFound(new { error = "Document not found" });
        }

        Stream fileStream;
        try
        {
            fileStream = await files.DownloadAsync(storedFile.FilePath, ct);
        }
        catch
        {
            return NotFound(new { error = "File not found on storage" });
        }

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        var slug = entityType.ToLowerInvariant();

        // thumb=true on an image → return a small JPEG preview rather than the full original.
        if (thumb && storedFile.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            byte[] srcBytes;
            using (var ms = new MemoryStream())
            {
                await fileStream.CopyToAsync(ms, ct);
                srcBytes = ms.ToArray();
            }

            var thumbBytes = ThumbnailResizer.ResizeToJpeg(srcBytes);
            if (thumbBytes is not null)
            {
                Response.Headers["Cache-Control"] = "private, max-age=86400";
                Response.Headers["Content-Disposition"] = $"inline; filename=\"{slug}-{entityId}-thumb.jpg\"";
                return File(thumbBytes, "image/jpeg");
            }

            // Resize failed — fall through and serve the original bytes.
            fileStream = new MemoryStream(srcBytes);
        }

        // Serve defensively: inline for known-safe types, octet-stream otherwise.
        if (InlineSafeContentTypes.Contains(storedFile.ContentType))
        {
            Response.Headers["Content-Disposition"] = $"inline; filename=\"{slug}-{entityId}\"";
            return File(fileStream, storedFile.ContentType);
        }

        Response.Headers["Content-Disposition"] = $"attachment; filename=\"{slug}-{entityId}\"";
        return File(fileStream, "application/octet-stream");
    }
}
