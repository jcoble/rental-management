using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using PdfSharp.Drawing;
using PdfSharp.Drawing.Layout;
using PdfSharp.Fonts;
using PdfSharp.Pdf.IO;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Leasing;

namespace RentalCommand.Api.Services.Domain;

public sealed record LeaseAgreementRenderResult(
    byte[] PdfBytes,
    int DocumentSourceVersionId,
    string? TemplateFieldSnapshotJson);

public interface ILeaseAgreementRenderer
{
    Task<LeaseAgreementRenderResult> RenderAsync(
        int portfolioId, int actorUserId, LeaseAgreementRenderData data, CancellationToken ct = default);

    Task<LeaseAgreementRenderResult> RenderExactAsync(
        int portfolioId,
        int documentSourceVersionId,
        LeaseAgreementRenderData data,
        Func<byte[]> builtInPdfFactory,
        CancellationToken ct = default);
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

    private readonly IFileStorage _storage;
    private readonly ILeaseAgreementPdfGenerator _fallbackPdf;
    private readonly ILegalDocumentSourceVersionResolver _sourceVersions;
    private readonly ILogger<LeaseAgreementRenderer> _logger;

    public LeaseAgreementRenderer(
        IFileStorage storage,
        ILeaseAgreementPdfGenerator fallbackPdf,
        ILegalDocumentSourceVersionResolver sourceVersions,
        ILogger<LeaseAgreementRenderer> logger)
    {
        _storage = storage;
        _fallbackPdf = fallbackPdf;
        _sourceVersions = sourceVersions;
        _logger = logger;
    }

    public async Task<LeaseAgreementRenderResult> RenderAsync(
        int portfolioId,
        int actorUserId,
        LeaseAgreementRenderData data,
        CancellationToken ct = default)
    {
        var resolvedSource = await _sourceVersions.ResolveActiveOverlayAsync(
            portfolioId,
            data.PropertyId,
            actorUserId,
            DateTime.UtcNow,
            ct);
        if (resolvedSource?.OriginalStoragePath is null)
        {
            var builtInSourceId = await ResolveBuiltInSourceAsync(portfolioId, actorUserId, ct);
            return new LeaseAgreementRenderResult(_fallbackPdf.Generate(data), builtInSourceId, null);
        }

        var templateSnapshot = JsonSerializer.Deserialize<AuthoredTemplateSnapshot>(
                resolvedSource.SnapshotPayload,
                JsonOptions)
            ?? throw new InvalidOperationException(
                $"Legal-document source {resolvedSource.DocumentSourceVersionId} has no template snapshot.");
        var allFields = templateSnapshot.Fields.Select(field => ToTemplateField(portfolioId, field)).ToArray();

        byte[] originalBytes;
        try
        {
            await using var original = await _storage.DownloadAsync(resolvedSource.OriginalStoragePath, ct);
            using var ms = new MemoryStream();
            await original.CopyToAsync(ms, ct);
            originalBytes = ms.ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Lease template source {DocumentSourceVersionId} PDF is unavailable; falling back to built-in agreement.",
                resolvedSource.DocumentSourceVersionId);
            var builtInSourceId = await ResolveBuiltInSourceAsync(portfolioId, actorUserId, ct);
            return new LeaseAgreementRenderResult(_fallbackPdf.Generate(data), builtInSourceId, null);
        }

        var valueMap = BuildValueMap(data);
        var renderableFields = allFields
            .Where(field => field.Kind == DocumentTemplateFieldKind.Whiteout
                || (field.SignerRole == DocumentTemplateSignerRole.None
                    && field.Kind != DocumentTemplateFieldKind.Signature
                    && field.Kind != DocumentTemplateFieldKind.Initial
                    && field.Kind != DocumentTemplateFieldKind.DateSigned
                    && (!string.IsNullOrWhiteSpace(ResolveValue(field, valueMap)))))
            .ToArray();
        var renderedBytes = RenderOverlayPreview(originalBytes, renderableFields, data);
        var fieldSnapshot = SnapshotFields(allFields, valueMap);
        return new LeaseAgreementRenderResult(
            renderedBytes,
            resolvedSource.DocumentSourceVersionId,
            fieldSnapshot);
    }

    public async Task<LeaseAgreementRenderResult> RenderExactAsync(
        int portfolioId,
        int documentSourceVersionId,
        LeaseAgreementRenderData data,
        Func<byte[]> builtInPdfFactory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(builtInPdfFactory);
        var source = await _sourceVersions.ResolveExactAsync(portfolioId, documentSourceVersionId, ct)
            ?? throw new InvalidOperationException(
                $"Legal-document source {documentSourceVersionId} is unavailable in this portfolio.");

        if (source.SourceKind == LegalDocumentSourceKind.BuiltInRenderer)
        {
            return new LeaseAgreementRenderResult(
                builtInPdfFactory(), source.DocumentSourceVersionId, null);
        }

        if (string.IsNullOrWhiteSpace(source.SourceStoragePath))
        {
            throw new InvalidOperationException(
                "The lease PDF is missing.");
        }

        byte[] sourceBytes;
        await using (var original = await _storage.DownloadAsync(source.SourceStoragePath, ct))
        {
            using var buffer = new MemoryStream();
            await original.CopyToAsync(buffer, ct);
            sourceBytes = buffer.ToArray();
        }

        EnsureImmutableSourceBytes(source, sourceBytes);

        if (source.SourceKind == LegalDocumentSourceKind.ImportedExternalDocument)
        {
            return new LeaseAgreementRenderResult(sourceBytes, source.DocumentSourceVersionId, null);
        }

        var templateSnapshot = JsonSerializer.Deserialize<AuthoredTemplateSnapshot>(
                source.SnapshotPayload,
                JsonOptions)
            ?? throw new InvalidOperationException(
                $"Legal-document source {documentSourceVersionId} has no template snapshot.");
        if (!string.Equals(
                templateSnapshot.RenderMode,
                nameof(DocumentTemplateRenderMode.Overlay),
                StringComparison.OrdinalIgnoreCase))
        {
            return new LeaseAgreementRenderResult(sourceBytes, source.DocumentSourceVersionId, null);
        }
        var allFields = templateSnapshot.Fields.Select(field => ToTemplateField(portfolioId, field)).ToArray();
        var valueMap = BuildValueMap(data);
        var renderableFields = allFields
            .Where(field => field.Kind == DocumentTemplateFieldKind.Whiteout
                || (field.SignerRole == DocumentTemplateSignerRole.None
                    && field.Kind != DocumentTemplateFieldKind.Signature
                    && field.Kind != DocumentTemplateFieldKind.Initial
                    && field.Kind != DocumentTemplateFieldKind.DateSigned
                    && !string.IsNullOrWhiteSpace(ResolveValue(field, valueMap))))
            .ToArray();
        return new LeaseAgreementRenderResult(
            RenderOverlayPreview(sourceBytes, renderableFields, data),
            source.DocumentSourceVersionId,
            SnapshotFields(allFields, valueMap));
    }

    private static void EnsureImmutableSourceBytes(
        ResolvedExactLegalDocumentSourceVersion source,
        byte[] sourceBytes)
    {
        var expectedHash = source.SourceContentSha256 ?? source.SourceArtifactContentSha256;
        if (source.SourceKind == LegalDocumentSourceKind.ImportedExternalDocument)
        {
            if (source.SourceStoredFileId is null || source.SourceLegalDocumentArtifactId is null
                || !LegalDocumentIssuanceBinding.IsSha256(source.SourceContentSha256)
                || !LegalDocumentIssuanceBinding.IsSha256(source.SourceArtifactContentSha256))
            {
                throw new InvalidOperationException(
                    "The imported lease file is incomplete.");
            }

            if (!LegalDocumentIssuanceBinding.Matches(
                    source.SourceContentSha256!, source.SourceArtifactContentSha256!))
            {
                throw new InvalidOperationException(
                    $"Imported legal-document source {source.DocumentSourceVersionId} conflicts with artifact {source.SourceLegalDocumentArtifactId}.");
            }
        }

        if (expectedHash is null)
        {
            return;
        }

        if (!LegalDocumentIssuanceBinding.IsSha256(expectedHash))
        {
            throw new InvalidOperationException(
                "The imported lease file could not be validated.");
        }

        var actualHash = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant();
        if (!LegalDocumentIssuanceBinding.Matches(actualHash, expectedHash))
        {
            throw new InvalidOperationException(
                "The imported lease file failed verification.");
        }
    }

    internal static byte[] RenderOverlayPreview(
        byte[] originalBytes,
        IReadOnlyList<DocumentTemplateField> fields,
        LeaseAgreementRenderData data)
    {
        return StampValues(originalBytes, fields, BuildValueMap(data));
    }

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

    internal static IReadOnlyDictionary<string, string> BuildValueMap(LeaseAgreementRenderData data)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["landlord.name"] = data.LandlordName,
            ["tenant.fullName"] = data.TenantName,
            ["tenant.email"] = data.TenantEmail,
            ["property.name"] = data.PropertyName,
            ["property.address"] = data.PropertyAddress,
            ["unit.number"] = data.UnitNumber ?? string.Empty,
            ["lease.startDate"] = Date(data.TermStartOn),
            ["lease.endDate"] = data.TermEndOn is { } termEnd ? Date(termEnd) : string.Empty,
            ["lease.monthlyRent"] = Money(data.BaseRentAmount),
            ["lease.securityDeposit"] = Money(data.SecurityDepositObligation),
            ["lease.lateFeeAmount"] = Money(data.LateFeeAmount),
            ["lease.rentDueDay"] = data.RentDueDay.ToString(CultureInfo.InvariantCulture),
        };
    }

    private static string Date(DateOnly value) =>
        value.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);

    private static string Money(decimal value) =>
        value.ToString("$#,##0.00", CultureInfo.InvariantCulture);

    private static string SnapshotFields(
        IReadOnlyList<DocumentTemplateField> fields,
        IReadOnlyDictionary<string, string> valueMap)
    {
        var snapshot = fields.Select(f => new
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

    private Task<int> ResolveBuiltInSourceAsync(
        int portfolioId, int actorUserId, CancellationToken ct)
    {
        return _sourceVersions.ResolveBuiltInAsync(
            portfolioId,
            BuiltInLeaseAgreementSource.BusinessKey,
            BuiltInLeaseAgreementSource.RendererKey,
            BuiltInLeaseAgreementSource.RendererVersion,
            BuiltInLeaseAgreementSource.SnapshotPayload,
            actorUserId,
            DateTime.UtcNow,
            ct);
    }

    private static DocumentTemplateField ToTemplateField(
        int portfolioId,
        AuthoredTemplateFieldSnapshot field) =>
        new()
        {
            Id = field.Id,
            PortfolioId = portfolioId,
            FieldKey = field.FieldKey,
            Label = field.Label,
            Kind = Enum.Parse<DocumentTemplateFieldKind>(field.Kind),
            SignerRole = Enum.Parse<DocumentTemplateSignerRole>(field.SignerRole),
            PageNumber = field.PageNumber,
            XPct = field.XPct,
            YPct = field.YPct,
            WidthPct = field.WidthPct,
            HeightPct = field.HeightPct,
            Required = field.Required,
            Locked = field.Locked,
            SortOrder = field.SortOrder,
            DefaultText = field.DefaultText,
        };

    private sealed record AuthoredTemplateSnapshot(
        int DocumentTemplateId,
        int DocumentTemplateVersion,
        string RenderMode,
        int? OriginalStoredFileId,
        int? CompiledStoredFileId,
        string? DraftHtml,
        IReadOnlyList<AuthoredTemplateFieldSnapshot> Fields);

    private sealed record AuthoredTemplateFieldSnapshot(
        int Id,
        string FieldKey,
        string Label,
        string Kind,
        string SignerRole,
        int PageNumber,
        double XPct,
        double YPct,
        double WidthPct,
        double HeightPct,
        bool Required,
        bool Locked,
        int SortOrder,
        string? DefaultText);

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
