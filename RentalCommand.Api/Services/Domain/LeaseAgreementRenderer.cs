using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PdfSharp.Drawing;
using PdfSharp.Drawing.Layout;
using PdfSharp.Fonts;
using PdfSharp.Pdf.IO;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Data;

namespace RentalCommand.Api.Services.Domain;

public sealed record LeaseAgreementRenderResult(
    byte[] PdfBytes,
    int? DocumentTemplateId,
    int? DocumentTemplateVersion,
    string? TemplateFieldSnapshotJson);

public interface ILeaseAgreementRenderer
{
    Task<LeaseAgreementRenderResult> RenderAsync(int portfolioId, LeaseAgreementData data, CancellationToken ct = default);
}

/// <summary>
/// Renders a lease agreement from either the built-in QuestPDF agreement or an active landlord-owned
/// overlay template. Overlay coordinates use the same normalized top-left contract as the web designer.
/// </summary>
public sealed class LeaseAgreementRenderer : ILeaseAgreementRenderer
{
    private const string OverlayFontFamily = "Lato";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly object FontResolverLock = new();
    private static readonly LatoFontResolver FontResolver = new();

    private readonly RentalCommandDbContext _db;
    private readonly IFileStorage _storage;
    private readonly ILeaseAgreementPdfGenerator _fallbackPdf;
    private readonly ILogger<LeaseAgreementRenderer> _logger;

    public LeaseAgreementRenderer(
        RentalCommandDbContext db,
        IFileStorage storage,
        ILeaseAgreementPdfGenerator fallbackPdf,
        ILogger<LeaseAgreementRenderer> logger)
    {
        _db = db;
        _storage = storage;
        _fallbackPdf = fallbackPdf;
        _logger = logger;
    }

    public async Task<LeaseAgreementRenderResult> RenderAsync(
        int portfolioId,
        LeaseAgreementData data,
        CancellationToken ct = default)
    {
        var template = await ResolveActiveOverlayTemplateAsync(portfolioId, data.Lease.PropertyId, ct);
        if (template?.OriginalStoredFile is null)
        {
            return new LeaseAgreementRenderResult(_fallbackPdf.Generate(data), null, null, null);
        }

        byte[] originalBytes;
        try
        {
            await using var original = await _storage.DownloadAsync(template.OriginalStoredFile.FilePath, ct);
            using var ms = new MemoryStream();
            await original.CopyToAsync(ms, ct);
            originalBytes = ms.ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Lease template {TemplateId} source PDF is unavailable; falling back to built-in agreement.",
                template.Id);
            return new LeaseAgreementRenderResult(_fallbackPdf.Generate(data), null, null, null);
        }

        var renderedBytes = RenderOverlayPreview(originalBytes, template.Fields, data);
        var valueMap = BuildValueMap(data);
        return new LeaseAgreementRenderResult(
            renderedBytes,
            template.Id,
            template.Version,
            SnapshotFields(template.Fields, valueMap));
    }

    private async Task<DocumentTemplate?> ResolveActiveOverlayTemplateAsync(
        int portfolioId,
        int propertyId,
        CancellationToken ct)
    {
        return await _db.DocumentTemplates
            .AsNoTracking()
            .Include(t => t.OriginalStoredFile)
            .Include(t => t.Fields)
            .Where(t => t.PortfolioId == portfolioId
                && t.Kind == DocumentTemplateKind.Lease
                && t.Status == DocumentTemplateStatus.Active
                && t.RenderMode == DocumentTemplateRenderMode.Overlay
                && t.OriginalStoredFileId != null
                && t.DefaultForPortfolio
                && (t.PropertyId == propertyId || t.PropertyId == null))
            .OrderByDescending(t => t.PropertyId == propertyId ? 1 : 0)
            .ThenByDescending(t => t.UpdatedAtUtc)
            .ThenByDescending(t => t.Id)
            .FirstOrDefaultAsync(ct);
    }

    internal static byte[] RenderOverlayPreview(
        byte[] originalBytes,
        IReadOnlyList<DocumentTemplateField> fields,
        LeaseAgreementData data)
    {
        var valueMap = BuildValueMap(data);
        var renderableFields = fields
            .Where(f => IsWhiteoutField(f) || (IsAutoFillField(f) && ResolveValue(f, valueMap) is { Length: > 0 }))
            .OrderBy(f => f.SortOrder)
            .ThenBy(f => f.Id)
            .ToList();

        return StampValues(originalBytes, renderableFields, valueMap);
    }

    private static bool IsAutoFillField(DocumentTemplateField field) =>
        field.SignerRole == DocumentTemplateSignerRole.None
            && !IsWhiteoutField(field)
            && field.Kind is not DocumentTemplateFieldKind.Signature
                and not DocumentTemplateFieldKind.Initial
                and not DocumentTemplateFieldKind.DateSigned;

    internal static bool IsWhiteoutField(DocumentTemplateField field) =>
        field.Kind == DocumentTemplateFieldKind.Whiteout;

    private static byte[] StampValues(
        byte[] originalBytes,
        IReadOnlyList<DocumentTemplateField> fields,
        IReadOnlyDictionary<string, string> valueMap)
    {
        EnsureFontResolver();

        using var input = new MemoryStream(originalBytes);
        using var pdf = PdfReader.Open(input, PdfDocumentOpenMode.Modify);

        foreach (var pageGroup in fields.GroupBy(f => f.PageNumber))
        {
            var pageIndex = pageGroup.Key - 1;
            if (pageIndex < 0 || pageIndex >= pdf.Pages.Count)
            {
                continue;
            }

            var page = pdf.Pages[pageIndex];
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            var formatter = new XTextFormatter(gfx);
            var orderedFields = pageGroup
                .OrderBy(f => f.SortOrder)
                .ThenBy(f => f.Id)
                .ToList();

            foreach (var field in orderedFields.Where(IsWhiteoutField))
            {
                DrawWhiteout(gfx, ToPageRect(field, page.Width.Point, page.Height.Point));
            }

            foreach (var field in orderedFields.Where(f => !IsWhiteoutField(f)))
            {
                var value = ResolveValue(field, valueMap);
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var rect = ToPageRect(field, page.Width.Point, page.Height.Point);
                var font = new XFont(OverlayFontFamily, FontSizeFor(rect), XFontStyleEx.Regular);
                formatter.DrawString(value, font, XBrushes.Black, rect, XStringFormats.TopLeft);
            }
        }

        using var output = new MemoryStream();
        pdf.Save(output, false);
        return output.ToArray();
    }

    internal static void DrawWhiteout(XGraphics gfx, XRect rect)
    {
        gfx.DrawRectangle(XBrushes.White, rect);
    }

    private static XRect ToPageRect(DocumentTemplateField field, double pageWidth, double pageHeight)
    {
        var x = Clamp01(field.XPct) * pageWidth;
        var y = Clamp01(field.YPct) * pageHeight;
        var width = Clamp01(field.WidthPct) * pageWidth;
        var height = Clamp01(field.HeightPct) * pageHeight;
        return new XRect(x, y, width, height);
    }

    private static double Clamp01(double value) => Math.Min(1, Math.Max(0, value));

    private static double FontSizeFor(XRect rect) =>
        Math.Clamp(rect.Height * 0.62, 7, 12);

    private static string? ResolveValue(DocumentTemplateField field, IReadOnlyDictionary<string, string> valueMap)
    {
        if (valueMap.TryGetValue(field.FieldKey, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return string.IsNullOrWhiteSpace(field.DefaultText) ? null : field.DefaultText.Trim();
    }

    private static IReadOnlyDictionary<string, string> BuildValueMap(LeaseAgreementData data)
    {
        var lease = data.Lease;
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["landlord.name"] = data.LandlordName,
            ["tenant.fullName"] = data.TenantName,
            ["tenant.email"] = lease.Tenant?.Email ?? string.Empty,
            ["property.name"] = data.PropertyName,
            ["property.address"] = data.PropertyAddress,
            ["unit.number"] = data.UnitNumber ?? string.Empty,
            ["lease.startDate"] = Date(lease.StartDate),
            ["lease.endDate"] = Date(lease.EndDate),
            ["lease.monthlyRent"] = Money(lease.MonthlyRent),
            ["lease.securityDeposit"] = Money(lease.SecurityDeposit),
            ["lease.lateFeeAmount"] = Money(lease.LateFeeAmount),
            ["lease.rentDueDay"] = lease.RentDueDay.ToString(CultureInfo.InvariantCulture),
        };
    }

    private static string Date(DateTime value) =>
        value.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);

    private static string Money(decimal value) =>
        value.ToString("$#,##0.00", CultureInfo.InvariantCulture);

    private static string SnapshotFields(
        IReadOnlyList<DocumentTemplateField> fields,
        IReadOnlyDictionary<string, string> valueMap)
    {
        var snapshot = fields
            .OrderBy(f => f.SortOrder)
            .ThenBy(f => f.Id)
            .Select(f => new
            {
                f.Id,
                f.FieldKey,
                f.Label,
                Kind = f.Kind.ToString(),
                SignerRole = f.SignerRole.ToString(),
                f.PageNumber,
                f.XPct,
                f.YPct,
                f.WidthPct,
                f.HeightPct,
                f.Required,
                f.Locked,
                f.SortOrder,
                Value = IsWhiteoutField(f) ? null : ResolveValue(f, valueMap),
            })
            .ToList();

        return JsonSerializer.Serialize(snapshot, JsonOptions);
    }

    private static void EnsureFontResolver()
    {
        lock (FontResolverLock)
        {
            GlobalFontSettings.FontResolver ??= FontResolver;
        }
    }

    private sealed class LatoFontResolver : IFontResolver
    {
        private const string Regular = "Lato#Regular";
        private const string Bold = "Lato#Bold";
        private const string Italic = "Lato#Italic";
        private const string BoldItalic = "Lato#BoldItalic";

        public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
        {
            var face = (bold, italic) switch
            {
                (true, true) => BoldItalic,
                (true, false) => Bold,
                (false, true) => Italic,
                _ => Regular,
            };

            return new FontResolverInfo(face);
        }

        public byte[] GetFont(string faceName)
        {
            var fileName = faceName switch
            {
                Bold => "Lato-Bold.ttf",
                Italic => "Lato-Italic.ttf",
                BoldItalic => "Lato-BoldItalic.ttf",
                _ => "Lato-Regular.ttf",
            };

            return File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "LatoFont", fileName));
        }
    }
}
