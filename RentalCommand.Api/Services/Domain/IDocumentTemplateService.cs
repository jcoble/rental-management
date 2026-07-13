using RentalCommand.Api.DTOs;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Services.Domain;

public interface IDocumentTemplateService
{
    Task<IReadOnlyList<DocumentTemplateResponse>> ListAsync(
        WorkspaceReadScope scope, DocumentTemplateKind? kind, DocumentTemplateStatus? status, int? propertyId, ListQuery query, CancellationToken ct = default);

    Task<DocumentTemplateListResponse> ListPageAsync(
        WorkspaceReadScope scope, DocumentTemplateKind? kind, DocumentTemplateStatus? status, int? propertyId, ListQuery query, CancellationToken ct = default);

    Task<DocumentTemplateResponse?> GetAsync(WorkspaceReadScope scope, int id, CancellationToken ct = default);

    Task<DocumentTemplateOperationResult<DocumentTemplateResponse>> CreateAsync(
        WorkspaceReadScope scope, CreateDocumentTemplateRequest request, CancellationToken ct = default);

    Task<DocumentTemplateOperationResult<DocumentTemplateResponse>> UploadPdfAsync(
        WorkspaceReadScope scope,
        Stream content,
        string fileName,
        string contentType,
        long sizeBytes,
        string name,
        string? description,
        bool defaultForPortfolio,
        int? propertyId,
        CancellationToken ct = default);

    Task<DocumentTemplateOperationResult<DocumentTemplateResponse>> UpdateAsync(
        WorkspaceReadScope scope, int id, UpdateDocumentTemplateRequest request, CancellationToken ct = default);

    Task<DocumentTemplateOperationResult<DocumentTemplateFieldResponse>> AddFieldAsync(
        WorkspaceReadScope scope, int templateId, CreateDocumentTemplateFieldRequest request, CancellationToken ct = default);

    Task<DocumentTemplateOperationResult<DocumentTemplateFieldResponse>> UpdateFieldAsync(
        WorkspaceReadScope scope, int templateId, int fieldId, UpdateDocumentTemplateFieldRequest request, CancellationToken ct = default);

    Task<DocumentTemplateOperationResult<bool>> DeleteFieldAsync(
        WorkspaceReadScope scope, int templateId, int fieldId, CancellationToken ct = default);

    Task<DocumentTemplateOperationResult<DocumentTemplatePreviewResult>> PreviewLeasePdfAsync(
        WorkspaceReadScope scope, int templateId, int leaseAgreementId, CancellationToken ct = default);
}

public sealed record DocumentTemplatePreviewResult(byte[] PdfBytes, string FileName);

public enum DocumentTemplateOperationOutcome
{
    Success,
    NotFound,
    Invalid,
}

public sealed record DocumentTemplateOperationResult<T>(
    DocumentTemplateOperationOutcome Outcome,
    T? Value = default,
    string? Error = null)
{
    public static DocumentTemplateOperationResult<T> Success(T value) => new(DocumentTemplateOperationOutcome.Success, value);
    public static DocumentTemplateOperationResult<T> NotFound(string error) => new(DocumentTemplateOperationOutcome.NotFound, default, error);
    public static DocumentTemplateOperationResult<T> Invalid(string error) => new(DocumentTemplateOperationOutcome.Invalid, default, error);
}
