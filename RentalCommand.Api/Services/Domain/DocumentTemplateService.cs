using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Api.Scanning;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Documents;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Operations;
using RentalCommand.Core.Time;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;
using RentalCommand.Data.Documents;

namespace RentalCommand.Api.Services.Domain;

/// <inheritdoc cref="IDocumentTemplateService"/>
public sealed class DocumentTemplateService : IDocumentTemplateService
{
    private static readonly string[] TemplateCapabilityKeys =
    [
        CapabilityKeys.RentalsManage,
        CapabilityKeys.LeasingAgreementsPrepare,
    ];

    private static readonly AtomicJsonResultCodec<DocumentTemplateMutationResult> MutationCodec =
        new("document-template.mutation.v1");
    private readonly RentalCommandDbContext _db;
    private readonly IDocumentTemplateFieldCatalog _catalog;
    private readonly IFileStorage _files;
    private readonly IPendingFileUploadStore _pendingUploads;
    private readonly TimeProvider _timeProvider;
    private readonly IAtomicUnitOfWork _atomic;

    public DocumentTemplateService(
        RentalCommandDbContext db,
        IDocumentTemplateFieldCatalog catalog,
        IFileStorage files,
        IPendingFileUploadStore pendingUploads,
        TimeProvider timeProvider,
        IAtomicUnitOfWork atomic)
    {
        _db = db;
        _catalog = catalog;
        _files = files;
        _pendingUploads = pendingUploads;
        _timeProvider = timeProvider;
        _atomic = atomic;
    }

    public async Task<IReadOnlyList<DocumentTemplateResponse>> ListAsync(
        WorkspaceReadScope scope, DocumentTemplateKind? kind, DocumentTemplateStatus? status, int? propertyId, ListQuery query, CancellationToken ct = default)
    {
        var page = await ListPageAsync(scope, kind, status, propertyId, query, ct);
        return page.Items;
    }

    public async Task<DocumentTemplateListResponse> ListPageAsync(
        WorkspaceReadScope scope, DocumentTemplateKind? kind, DocumentTemplateStatus? status, int? propertyId, ListQuery query, CancellationToken ct = default)
    {
        var q = AuthorizedTemplates(scope).AsNoTracking();

        if (kind.HasValue)
        {
            q = q.Where(t => t.Kind == kind.Value);
        }

        if (status.HasValue)
        {
            q = q.Where(t => t.Status == status.Value);
        }

        if (propertyId.HasValue)
        {
            q = q.Where(t => t.PropertyId == null || t.PropertyId == propertyId.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(t =>
                EF.Functions.ILike(t.Name, $"%{term}%") ||
                (t.Description != null && EF.Functions.ILike(t.Description, $"%{term}%")));
        }

        q = query.SortField switch
        {
            "name" => query.SortDescending ? q.OrderByDescending(t => t.Name) : q.OrderBy(t => t.Name),
            "kind" => query.SortDescending ? q.OrderByDescending(t => t.Kind) : q.OrderBy(t => t.Kind),
            "status" => query.SortDescending ? q.OrderByDescending(t => t.Status) : q.OrderBy(t => t.Status),
            "updatedatutc" or "updatedat" => query.SortDescending
                ? q.OrderByDescending(t => t.UpdatedAtUtc)
                : q.OrderBy(t => t.UpdatedAtUtc),
            _ => query.SortDescending ? q.OrderByDescending(t => t.CreatedAtUtc) : q.OrderBy(t => t.CreatedAtUtc),
        };

        var totalCount = await q.CountAsync(ct);
        var items = await q
            .Skip(query.NormalizedSkip)
            .Take(query.NormalizedTake)
            .Select(t => new DocumentTemplateResponse
            {
                Id = t.Id,
                PortfolioId = t.PortfolioId,
                Kind = t.Kind,
                Status = t.Status,
                RenderMode = t.RenderMode,
                Name = t.Name,
                Description = t.Description,
                OriginalStoredFileId = t.OriginalStoredFileId,
                CompiledStoredFileId = t.CompiledStoredFileId,
                HasDraftHtml = t.DraftHtml != null && t.DraftHtml != string.Empty,
                DefaultForPortfolio = t.DefaultForPortfolio,
                PropertyId = t.PropertyId,
                Version = t.Version,
                FieldCount = t.Fields.Count,
                CreatedAtUtc = t.CreatedAtUtc,
                UpdatedAtUtc = t.UpdatedAtUtc,
                ArchivedAtUtc = t.ArchivedAtUtc,
            })
            .ToListAsync(ct);

        return new DocumentTemplateListResponse
        {
            Items = items,
            TotalCount = totalCount,
            Skip = query.NormalizedSkip,
            Take = query.NormalizedTake,
        };
    }

    public async Task<DocumentTemplateResponse?> GetAsync(WorkspaceReadScope scope, int id, CancellationToken ct = default)
    {
        return await AuthorizedTemplates(scope)
            .AsNoTracking()
            .Where(template => template.Id == id)
            .Select(template => new DocumentTemplateResponse
            {
                Id = template.Id,
                PortfolioId = template.PortfolioId,
                Kind = template.Kind,
                Status = template.Status,
                RenderMode = template.RenderMode,
                Name = template.Name,
                Description = template.Description,
                OriginalStoredFileId = template.OriginalStoredFileId,
                CompiledStoredFileId = template.CompiledStoredFileId,
                HasDraftHtml = template.DraftHtml != null && template.DraftHtml != string.Empty,
                DefaultForPortfolio = template.DefaultForPortfolio,
                PropertyId = template.PropertyId,
                Version = template.Version,
                FieldCount = template.Fields.Count,
                CreatedAtUtc = template.CreatedAtUtc,
                UpdatedAtUtc = template.UpdatedAtUtc,
                ArchivedAtUtc = template.ArchivedAtUtc,
                Fields = template.Fields
                    .OrderBy(field => field.SortOrder)
                    .ThenBy(field => field.Id)
                    .Select(field => new DocumentTemplateFieldResponse
                    {
                        Id = field.Id,
                        DocumentTemplateId = field.DocumentTemplateId,
                        FieldKey = field.FieldKey,
                        Label = field.Label,
                        Kind = field.Kind,
                        SignerRole = field.SignerRole,
                        PageNumber = field.PageNumber,
                        XPct = field.XPct,
                        YPct = field.YPct,
                        WidthPct = field.WidthPct,
                        HeightPct = field.HeightPct,
                        Required = field.Required,
                        Locked = field.Locked,
                        SortOrder = field.SortOrder,
                        DefaultText = field.DefaultText,
                    })
                    .ToArray(),
            })
            .SingleOrDefaultAsync(ct);
    }

    public async Task<DocumentTemplateOperationResult<DocumentTemplateResponse>> CreateAsync(
        WorkspaceReadScope scope, CreateDocumentTemplateRequest request,
        string idempotencyKey, CancellationToken ct = default)
    {
        var digest = Digest(idempotencyKey);
        var command = new CreateDocumentTemplateCommand(
            scope.PortfolioId, Actor(scope), request.Kind, request.RenderMode, request.Name,
            request.Description, request.OriginalStoredFileId, request.CompiledStoredFileId,
            request.PropertyId, request.DefaultForPortfolio, request.DraftHtml, digest);
        var outcome = await _atomic.ExecuteAsync(
            Identity("document-template.create", scope.PortfolioId, digest), command, MutationCodec, ct);
        return MapTemplateResult(outcome.Value);
    }

    public async Task<DocumentTemplateOperationResult<DocumentTemplateResponse>> UploadPdfAsync(
        WorkspaceReadScope scope,
        Stream content,
        string fileName,
        string contentType,
        long sizeBytes,
        string name,
        string? description,
        bool defaultForPortfolio,
        int? propertyId,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var portfolioId = scope.PortfolioId;
        var normalizedName = string.IsNullOrWhiteSpace(name)
            ? Path.GetFileNameWithoutExtension(fileName)
            : name.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return DocumentTemplateOperationResult<DocumentTemplateResponse>.Invalid("Template name is required.");
        }
        var safeFileName = DiskFileStorage.SanitizeFileName(fileName);
        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        if (bytes.LongLength == 0 || bytes.LongLength != sizeBytes)
            return DocumentTemplateOperationResult<DocumentTemplateResponse>.Invalid(
                "Uploaded PDF size did not match the request.");

        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var normalizedDescription = NormalizeNullable(description);
        var fingerprint = Digest(JsonSerializer.Serialize(new
        {
            portfolioId,
            scope.UserId,
            safeFileName,
            contentType,
            sizeBytes = bytes.LongLength,
            sha256,
            name = normalizedName,
            description = normalizedDescription,
            defaultForPortfolio,
            propertyId,
        }));
        const string purpose = "document-template-pdf";
        var now = _timeProvider.UtcNow();
        var admission = await _pendingUploads.PrepareAsync(
            portfolioId, scope.UserId, purpose, idempotencyKey, fingerprint,
            safeFileName, contentType, bytes.LongLength, now, ct);
        if (admission.State == PendingFileUploadState.Prepared)
        {
            await using var upload = new MemoryStream(bytes, writable: false);
            await _files.UploadAtAsync(upload, admission.StoragePath, safeFileName, contentType, ct);
        }

        var digest = Digest(idempotencyKey);
        var command = new FinalizeDocumentTemplateUploadCommand(
            portfolioId, Actor(scope), admission.Id, purpose, digest, fingerprint,
            admission.StoragePath, safeFileName, contentType, bytes.LongLength, sha256,
            normalizedName, normalizedDescription, defaultForPortfolio, propertyId, digest);
        var outcome = await _atomic.ExecuteAsync(
            Identity("document-template.upload.finalize", portfolioId, digest),
            command, MutationCodec, ct);
        return MapTemplateResult(outcome.Value);
    }

    public async Task<DocumentTemplateOperationResult<DocumentTemplateResponse>> UpdateAsync(
        WorkspaceReadScope scope, int id, UpdateDocumentTemplateRequest request,
        string idempotencyKey, CancellationToken ct = default)
    {
        var digest = Digest(idempotencyKey);
        var command = new UpdateDocumentTemplateCommand(
            scope.PortfolioId, Actor(scope), id, request.Status, request.RenderMode, request.Name,
            request.Description, request.OriginalStoredFileId, request.CompiledStoredFileId,
            request.PropertyId, request.DefaultForPortfolio, request.DraftHtml, digest);
        var outcome = await _atomic.ExecuteAsync(
            Identity("document-template.update", scope.PortfolioId, digest), command, MutationCodec, ct);
        return MapTemplateResult(outcome.Value);
    }

    public async Task<DocumentTemplateOperationResult<DocumentTemplateFieldResponse>> AddFieldAsync(
        WorkspaceReadScope scope, int templateId, CreateDocumentTemplateFieldRequest request,
        string idempotencyKey, CancellationToken ct = default)
    {
        var templateKind = await AuthorizedTemplates(scope).AsNoTracking()
            .Where(template => template.Id == templateId)
            .Select(template => (DocumentTemplateKind?)template.Kind)
            .SingleOrDefaultAsync(ct);
        if (!templateKind.HasValue)
            return DocumentTemplateOperationResult<DocumentTemplateFieldResponse>.NotFound("Document template not found");
        var catalogItem = _catalog.Find(templateKind.Value, request.FieldKey);
        var digest = Digest(idempotencyKey);
        var command = new AddDocumentTemplateFieldCommand(
            scope.PortfolioId, Actor(scope), templateId, request.FieldKey,
            NormalizeNullable(request.Label) ?? catalogItem?.Label ?? request.FieldKey.Trim(),
            request.Kind, request.SignerRole, request.PageNumber, request.XPct, request.YPct,
            request.WidthPct, request.HeightPct,
            request.Required || catalogItem?.RequiredForSignature == true,
            request.Locked, request.SortOrder, request.DefaultText, digest);
        var outcome = await _atomic.ExecuteAsync(
            Identity("document-template.field.add", scope.PortfolioId, digest), command, MutationCodec, ct);
        return MapFieldResult(outcome.Value);
    }

    public async Task<DocumentTemplateOperationResult<DocumentTemplateFieldResponse>> UpdateFieldAsync(
        WorkspaceReadScope scope, int templateId, int fieldId, UpdateDocumentTemplateFieldRequest request,
        string idempotencyKey, CancellationToken ct = default)
    {
        var digest = Digest(idempotencyKey);
        var command = new UpdateDocumentTemplateFieldCommand(
            scope.PortfolioId, Actor(scope), templateId, fieldId, request.FieldKey, request.Label,
            request.Kind, request.SignerRole, request.PageNumber, request.XPct, request.YPct,
            request.WidthPct, request.HeightPct, request.Required, request.Locked,
            request.SortOrder, request.DefaultText, digest);
        var outcome = await _atomic.ExecuteAsync(
            Identity("document-template.field.update", scope.PortfolioId, digest), command, MutationCodec, ct);
        return MapFieldResult(outcome.Value);
    }

    public async Task<DocumentTemplateOperationResult<bool>> DeleteFieldAsync(
        WorkspaceReadScope scope, int templateId, int fieldId,
        string idempotencyKey, CancellationToken ct = default)
    {
        var digest = Digest(idempotencyKey);
        var command = new DeleteDocumentTemplateFieldCommand(
            scope.PortfolioId, Actor(scope), templateId, fieldId, digest);
        var outcome = await _atomic.ExecuteAsync(
            Identity("document-template.field.delete", scope.PortfolioId, digest), command, MutationCodec, ct);
        return outcome.Value.Outcome switch
        {
            DocumentTemplateMutationOutcome.Applied => DocumentTemplateOperationResult<bool>.Success(true),
            DocumentTemplateMutationOutcome.NotFound => DocumentTemplateOperationResult<bool>.NotFound(
                outcome.Value.Error ?? "Document template field not found"),
            _ => DocumentTemplateOperationResult<bool>.Invalid(outcome.Value.Error ?? "Document template field is invalid"),
        };
    }

    public async Task<DocumentTemplateOperationResult<DocumentTemplatePreviewResult>> PreviewLeasePdfAsync(
        WorkspaceReadScope scope,
        int templateId,
        int leaseAgreementId,
        CancellationToken ct = default)
    {
        var portfolioId = scope.PortfolioId;
        var template = await AuthorizedTemplates(scope)
            .AsNoTracking()
            .Include(t => t.OriginalStoredFile)
            .FirstOrDefaultAsync(t => t.Id == templateId, ct);
        if (template is null)
        {
            return DocumentTemplateOperationResult<DocumentTemplatePreviewResult>.NotFound("Document template not found");
        }

        if (template.Kind != DocumentTemplateKind.Lease ||
            template.RenderMode != DocumentTemplateRenderMode.Overlay ||
            template.OriginalStoredFile is null)
        {
            return DocumentTemplateOperationResult<DocumentTemplatePreviewResult>.Invalid(
                "Only uploaded lease PDF templates can be previewed against a lease.");
        }

        var agreement = await BuildAgreementPreviewQuery(scope, leaseAgreementId)
            .SingleOrDefaultAsync(ct);
        if (agreement is null)
        {
            return DocumentTemplateOperationResult<DocumentTemplatePreviewResult>.NotFound(
                "Lease agreement not found");
        }

        if (template.PropertyId.HasValue && template.PropertyId.Value != agreement.PropertyId)
        {
            return DocumentTemplateOperationResult<DocumentTemplatePreviewResult>.Invalid(
                "Choose a lease from the property this template is assigned to.");
        }

        byte[] originalBytes;
        try
        {
            originalBytes = await DownloadTemplateBytesAsync(template.OriginalStoredFile.FilePath, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DocumentTemplateOperationResult<DocumentTemplatePreviewResult>.Invalid(
                "Template source PDF is unavailable.");
        }

        var data = BuildLeaseAgreementData(agreement);
        var valueKeys = LeaseAgreementRenderer.BuildValueMap(data)
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => pair.Key)
            .ToArray();
        var fields = await _db.DocumentTemplateFields.AsNoTracking()
            .Where(field => field.PortfolioId == scope.PortfolioId
                && field.DocumentTemplateId == template.Id
                && (field.Kind == DocumentTemplateFieldKind.Whiteout
                    || (field.SignerRole == DocumentTemplateSignerRole.None
                        && field.Kind != DocumentTemplateFieldKind.Signature
                        && field.Kind != DocumentTemplateFieldKind.Initial
                        && field.Kind != DocumentTemplateFieldKind.DateSigned
                        && (valueKeys.Contains(field.FieldKey)
                            || (field.DefaultText != null && field.DefaultText != "")))))
            .OrderBy(field => field.SortOrder)
            .ThenBy(field => field.Id)
            .ToListAsync(ct);
        var previewBytes = LeaseAgreementRenderer.RenderOverlayPreview(originalBytes, fields, data);
        var fileName = $"lease-template-{template.Id}-agreement-{agreement.LeaseAgreementId}-preview.pdf";

        return DocumentTemplateOperationResult<DocumentTemplatePreviewResult>.Success(
            new DocumentTemplatePreviewResult(previewBytes, fileName));
    }

    private async Task<byte[]> DownloadTemplateBytesAsync(string filePath, CancellationToken ct)
    {
        await using var source = await _files.DownloadAsync(filePath, ct);
        using var ms = new MemoryStream();
        await source.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    internal IQueryable<LeaseAgreementPreviewReadRow> BuildAgreementPreviewQuery(
        WorkspaceReadScope scope,
        int leaseAgreementId) =>
        from agreement in _db.LeaseAgreements.AsNoTracking()
        join status in _db.LeaseAgreementStatusProjections.AsNoTracking()
            on new { agreement.PortfolioId, AgreementId = agreement.Id }
            equals new { status.PortfolioId, AgreementId = status.AgreementId }
        where agreement.PortfolioId == scope.PortfolioId
            && agreement.Id == leaseAgreementId
            && agreement.DraftCanceledAtUtc == null
            && agreement.VoidedAtUtc == null
            && _db.Properties.AsNoTracking()
                .WhereAuthorized(_db, scope, TemplateCapabilityKeys, _timeProvider.UtcNow())
                .Any(property => property.Id == agreement.LeaseManagement!.PropertyId)
        select new LeaseAgreementPreviewReadRow
        {
            LeaseAgreementId = agreement.Id,
            LeaseManagementId = agreement.LeaseManagementId,
            AgreementNumber = agreement.AgreementNumber,
            AgreementStatus = status.AgreementStatus,
            TermStartOn = agreement.TermStartOn,
            TermEndOn = agreement.TermEndOn,
            BaseRentAmount = agreement.BaseRentAmount,
            SecurityDepositObligation = agreement.SecurityDepositObligation,
            LateFeeAmount = agreement.LateFeeAmount,
            RentDueDay = agreement.RentDueDay,
            PropertyId = agreement.LeaseManagement!.PropertyId,
            PropertyName = agreement.LeaseManagement.Property!.Name,
            AddressLine1 = agreement.LeaseManagement.Property.AddressLine1,
            AddressLine2 = agreement.LeaseManagement.Property.AddressLine2,
            City = agreement.LeaseManagement.Property.City,
            State = agreement.LeaseManagement.Property.State,
            PostalCode = agreement.LeaseManagement.Property.PostalCode,
            YearBuilt = agreement.LeaseManagement.Property.YearBuilt,
            UnitNumber = agreement.LeaseManagement.Unit!.UnitNumber,
            LandlordName = agreement.LeaseManagement.Portfolio!.ManagementCompanyName,
            PortfolioName = agreement.LeaseManagement.Portfolio.Name,
            TenantName = agreement.Signers
                .Where(signer => signer.SignerRole == LeaseLegalSignerRole.PrimaryTenant)
                .OrderBy(signer => signer.SigningOrder)
                .Select(signer => signer.NameSnapshot)
                .FirstOrDefault(),
            TenantEmail = agreement.Signers
                .Where(signer => signer.SignerRole == LeaseLegalSignerRole.PrimaryTenant)
                .OrderBy(signer => signer.SigningOrder)
                .Select(signer => signer.EmailSnapshot)
                .FirstOrDefault(),
        };

    private static LeaseAgreementRenderData BuildLeaseAgreementData(LeaseAgreementPreviewReadRow agreement)
    {
        var landlordName = !string.IsNullOrWhiteSpace(agreement.LandlordName)
            ? agreement.LandlordName
            : agreement.PortfolioName;
        var propertyAddress = string.Join(", ", new[]
            {
                agreement.AddressLine1,
                agreement.AddressLine2,
                $"{agreement.City}, {agreement.State} {agreement.PostalCode}".Trim(),
            }.Where(s => !string.IsNullOrWhiteSpace(s)));

        return new LeaseAgreementRenderData
        {
            PropertyId = agreement.PropertyId,
            AgreementNumber = agreement.AgreementNumber,
            TermStartOn = agreement.TermStartOn,
            TermEndOn = agreement.TermEndOn,
            BaseRentAmount = agreement.BaseRentAmount,
            SecurityDepositObligation = agreement.SecurityDepositObligation,
            LateFeeAmount = agreement.LateFeeAmount,
            RentDueDay = agreement.RentDueDay,
            LandlordName = landlordName,
            TenantName = agreement.TenantName ?? string.Empty,
            TenantEmail = agreement.TenantEmail ?? string.Empty,
            PropertyName = agreement.PropertyName,
            PropertyAddress = propertyAddress,
            UnitNumber = agreement.UnitNumber,
            State = agreement.State,
            YearBuilt = agreement.YearBuilt,
        };
    }

    internal sealed class LeaseAgreementPreviewReadRow
    {
        public int LeaseAgreementId { get; init; }
        public int LeaseManagementId { get; init; }
        public string AgreementNumber { get; init; } = string.Empty;
        public string AgreementStatus { get; init; } = string.Empty;
        public DateOnly TermStartOn { get; init; }
        public DateOnly? TermEndOn { get; init; }
        public decimal BaseRentAmount { get; init; }
        public decimal SecurityDepositObligation { get; init; }
        public decimal LateFeeAmount { get; init; }
        public short RentDueDay { get; init; }
        public int PropertyId { get; init; }
        public string PropertyName { get; init; } = string.Empty;
        public string AddressLine1 { get; init; } = string.Empty;
        public string? AddressLine2 { get; init; }
        public string City { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;
        public string PostalCode { get; init; } = string.Empty;
        public int? YearBuilt { get; init; }
        public string? UnitNumber { get; init; }
        public string? LandlordName { get; init; }
        public string PortfolioName { get; init; } = string.Empty;
        public string? TenantName { get; init; }
        public string? TenantEmail { get; init; }
    }

    private static DocumentTemplateOperationResult<DocumentTemplateResponse> MapTemplateResult(
        DocumentTemplateMutationResult result) => result.Outcome switch
        {
            DocumentTemplateMutationOutcome.Applied when result.Template is not null =>
                DocumentTemplateOperationResult<DocumentTemplateResponse>.Success(Response(result.Template)),
            DocumentTemplateMutationOutcome.NotFound =>
                DocumentTemplateOperationResult<DocumentTemplateResponse>.NotFound(
                    result.Error ?? "Document template not found"),
            _ => DocumentTemplateOperationResult<DocumentTemplateResponse>.Invalid(
                result.Error ?? "Document template is invalid"),
        };

    private static DocumentTemplateOperationResult<DocumentTemplateFieldResponse> MapFieldResult(
        DocumentTemplateMutationResult result) => result.Outcome switch
        {
            DocumentTemplateMutationOutcome.Applied when result.Field is not null =>
                DocumentTemplateOperationResult<DocumentTemplateFieldResponse>.Success(Response(result.Field)),
            DocumentTemplateMutationOutcome.NotFound =>
                DocumentTemplateOperationResult<DocumentTemplateFieldResponse>.NotFound(
                    result.Error ?? "Document template field not found"),
            _ => DocumentTemplateOperationResult<DocumentTemplateFieldResponse>.Invalid(
                result.Error ?? "Document template field is invalid"),
        };

    private static DocumentTemplateResponse Response(DocumentTemplateSnapshot template) => new()
    {
        Id = template.Id,
        PortfolioId = template.PortfolioId,
        Kind = template.Kind,
        Status = template.Status,
        RenderMode = template.RenderMode,
        Name = template.Name,
        Description = template.Description,
        OriginalStoredFileId = template.OriginalStoredFileId,
        CompiledStoredFileId = template.CompiledStoredFileId,
        HasDraftHtml = template.HasDraftHtml,
        DefaultForPortfolio = template.DefaultForPortfolio,
        PropertyId = template.PropertyId,
        Version = template.Version,
        FieldCount = template.FieldCount,
        CreatedAtUtc = template.CreatedAtUtc,
        UpdatedAtUtc = template.UpdatedAtUtc,
        ArchivedAtUtc = template.ArchivedAtUtc,
        Fields = template.Fields.Select(Response).ToArray(),
    };

    private static DocumentTemplateFieldResponse Response(DocumentTemplateFieldSnapshot field) => new()
    {
        Id = field.Id,
        DocumentTemplateId = field.DocumentTemplateId,
        FieldKey = field.FieldKey,
        Label = field.Label,
        Kind = field.Kind,
        SignerRole = field.SignerRole,
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

    private static StaffOperationActor Actor(WorkspaceReadScope scope) => new(
        scope.UserId, scope.SessionId, scope.AccessContextId, scope.AccessRevision);

    private static string Digest(string idempotencyKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey)))
            .ToLowerInvariant();

    private static AtomicCommandIdentity Identity(string operation, int portfolioId, string digest) =>
        new(operation, $"{portfolioId}:{digest}");

    private static string? NormalizeNullable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private IQueryable<DocumentTemplate> AuthorizedTemplates(WorkspaceReadScope scope)
    {
        var authorizedProperties = _db.Properties
            .AsNoTracking()
            .WhereAuthorized(_db, scope, TemplateCapabilityKeys, _timeProvider.UtcNow());
        var allProperties = _db.AuthorizedWorkspaceAssignments(
            scope,
            TemplateCapabilityKeys,
            CapabilityAuthorizationTargetKind.Property,
            _timeProvider.UtcNow());

        return _db.DocumentTemplates.Where(template =>
            template.PortfolioId == scope.PortfolioId &&
            (template.PropertyId.HasValue
                ? authorizedProperties.Any(property => property.Id == template.PropertyId.Value)
                : allProperties.Any() || authorizedProperties.Any()));
    }

}
