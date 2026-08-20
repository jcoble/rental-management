using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Documents;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Operations;
using RentalCommand.Data.Operations;

namespace RentalCommand.Data.Documents;

public static class DocumentTemplateWriteSupport
{
    public const string ResultContract = "document-template.mutation.v1";

    public static string IdempotencyKey(int portfolioId, string digest) => $"{portfolioId}:{digest}";

    public static TransactionalWrite<TCommand, DocumentTemplateMutationResult> Write<TCommand>(
        string operationName,
        TCommand command,
        Func<TCommand, IAtomicCommandContext, CancellationToken, Task<DocumentTemplateMutationResult>> executeAsync,
        Func<TCommand, IAtomicCommandContext, CancellationToken, Task> authorizeReplayAsync)
        where TCommand : notnull, IAtomicCommandData
    {
        var actor = command switch
        {
            CreateDocumentTemplateCommand value => value.Actor,
            FinalizeDocumentTemplateUploadCommand value => value.Actor,
            UpdateDocumentTemplateCommand value => value.Actor,
            AddDocumentTemplateFieldCommand value => value.Actor,
            UpdateDocumentTemplateFieldCommand value => value.Actor,
            DeleteDocumentTemplateFieldCommand value => value.Actor,
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
        var portfolioId = command switch
        {
            CreateDocumentTemplateCommand value => value.PortfolioId,
            FinalizeDocumentTemplateUploadCommand value => value.PortfolioId,
            UpdateDocumentTemplateCommand value => value.PortfolioId,
            AddDocumentTemplateFieldCommand value => value.PortfolioId,
            UpdateDocumentTemplateFieldCommand value => value.PortfolioId,
            DeleteDocumentTemplateFieldCommand value => value.PortfolioId,
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
        switch (command)
        {
            case CreateDocumentTemplateCommand value: DocumentTemplateCommandSupport.Validate(value); break;
            case FinalizeDocumentTemplateUploadCommand value: DocumentTemplateCommandSupport.Validate(value); break;
            case UpdateDocumentTemplateCommand value: DocumentTemplateCommandSupport.Validate(value); break;
            case AddDocumentTemplateFieldCommand value: DocumentTemplateCommandSupport.Validate(value); break;
            case UpdateDocumentTemplateFieldCommand value: DocumentTemplateCommandSupport.Validate(value); break;
            case DeleteDocumentTemplateFieldCommand value: DocumentTemplateCommandSupport.Validate(value); break;
        }
        return new(operationName, WriteIdempotencyPolicy.Required, command, ResultContract,
            new WriteLockPlan(WriteLockProtocol.AuthorizationScope,
                WriteLock.For("AuthSession", actor.AuthSessionId),
                WriteLock.For("WorkspaceAccessContext", actor.AccessContextId),
                WriteLock.For("Portfolio", portfolioId)),
            executeAsync, authorizeReplayAsync);
    }

    internal static InvalidOperationException RetiredPath() => new(
        "Document template writes no longer use the legacy document template mutation handlers.");
}

public sealed class CreateDocumentTemplateHandler
    : IAtomicCommandHandler<CreateDocumentTemplateCommand, DocumentTemplateMutationResult>
{
    public Task<DocumentTemplateMutationResult> HandleAsync(
        CreateDocumentTemplateCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        throw DocumentTemplateWriteSupport.RetiredPath();

    public Task AuthorizeReplayAsync(
        CreateDocumentTemplateCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        throw DocumentTemplateWriteSupport.RetiredPath();

    public static async Task<DocumentTemplateMutationResult> ExecuteAsync(
        RentalCommandDbContext db, CreateDocumentTemplateCommand command,
        IAtomicCommandContext context, CancellationToken ct)
    {
        DocumentTemplateCommandSupport.Validate(command);
        var securityNowUtc = await context.ReadDatabaseClockUtcAsync(ct);
        var businessNowUtc = command.BusinessNowUtc;
        if (!await DocumentTemplateCommandSupport.CanManageAsync(
                command.PortfolioId, command.Actor, command.PropertyId,
                db, businessNowUtc, securityNowUtc, ct))
            return DocumentTemplateCommandSupport.NotFound(0, "Document template target not found");
        if (!await DocumentTemplateCommandSupport.ReferencesValidAsync(
                db, command.PortfolioId,
                command.OriginalStoredFileId, command.CompiledStoredFileId, ct))
            return DocumentTemplateCommandSupport.Invalid(0, "Document file not found in this portfolio");

        var template = new DocumentTemplate
        {
            PortfolioId = command.PortfolioId,
            Kind = command.Kind,
            RenderMode = command.RenderMode,
            Status = DocumentTemplateStatus.Draft,
            Name = command.Name.Trim(),
            Description = DocumentTemplateCommandSupport.Clean(command.Description),
            OriginalStoredFileId = command.OriginalStoredFileId,
            CompiledStoredFileId = command.CompiledStoredFileId,
            DraftHtml = command.DraftHtml,
            DefaultForPortfolio = command.DefaultForPortfolio,
            PropertyId = command.PropertyId,
            CreatedAtUtc = businessNowUtc,
            UpdatedAtUtc = businessNowUtc,
        };

        context.UseDatabaseWallClockForAudit(businessNowUtc);
        if (template.DefaultForPortfolio)
            await DocumentTemplateCommandSupport.ClearOtherDefaultAsync(
                context, command, db, template.Kind, template.PropertyId, null, businessNowUtc, ct);
        db.Add(template);
        context.BindSemanticAudit(template, DocumentTemplateCommandSupport.TemplateAudit(
            command.PortfolioId, command.Actor.UserId, template, AuditLogOperation.Created,
            "Created document template."));
        await context.FlushBusinessAsync(ct);
        var snapshot = await DocumentTemplateCommandSupport.LoadTemplateSnapshotAsync(
            db, command.PortfolioId, template.Id, ct);
        DocumentTemplateCommandSupport.StageUpdate(
            context, command.PortfolioId, template.Id, command.DeliveryIdempotencyKey, businessNowUtc, "create");
        return new(DocumentTemplateMutationOutcome.Applied, template.Id, Template: snapshot);
    }

    public static async Task AuthorizeAsync(
        RentalCommandDbContext db, CreateDocumentTemplateCommand command,
        IAtomicCommandContext context, CancellationToken ct)
    {
        DocumentTemplateCommandSupport.Validate(command);
        var securityNowUtc = await context.ReadDatabaseClockUtcAsync(ct);
        if (!await DocumentTemplateCommandSupport.CanManageAsync(
                command.PortfolioId, command.Actor, command.PropertyId,
                db, command.BusinessNowUtc, securityNowUtc, ct))
            throw new UnauthorizedAccessException("The active assignment cannot create this document template.");
    }
}

public sealed class FinalizeDocumentTemplateUploadHandler
    : IAtomicCommandHandler<FinalizeDocumentTemplateUploadCommand, DocumentTemplateMutationResult>
{
    public Task<DocumentTemplateMutationResult> HandleAsync(
        FinalizeDocumentTemplateUploadCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        throw DocumentTemplateWriteSupport.RetiredPath();

    public Task AuthorizeReplayAsync(
        FinalizeDocumentTemplateUploadCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        throw DocumentTemplateWriteSupport.RetiredPath();

    public static async Task<DocumentTemplateMutationResult> ExecuteAsync(
        RentalCommandDbContext db, FinalizeDocumentTemplateUploadCommand command,
        IAtomicCommandContext context, CancellationToken ct)
    {
        DocumentTemplateCommandSupport.Validate(command);
        var securityNowUtc = await context.ReadDatabaseClockUtcAsync(ct);
        var businessNowUtc = command.BusinessNowUtc;
        if (!await DocumentTemplateCommandSupport.CanManageAsync(
                command.PortfolioId, command.Actor, command.PropertyId,
                db, businessNowUtc, securityNowUtc, ct))
            return DocumentTemplateCommandSupport.NotFound(0, "Document template target not found");

        var pending = (await AtomicPendingFileUploadPersistence.LockPreparedSetAsync(db,
            context, command.PortfolioId,
            command.Actor.UserId,
            [new AtomicPendingFileUploadExpectation(
                command.PendingUploadId, command.Purpose, command.OperationKeyHash,
                command.RequestFingerprint, command.StoragePath, command.FileName,
                command.ContentType, command.SizeBytes)],
            ct)).Single();

        var stored = new StoredFile
        {
            PortfolioId = command.PortfolioId,
            EntityType = nameof(DocumentTemplate),
            FileName = command.FileName,
            FilePath = command.StoragePath,
            ContentType = command.ContentType,
            FileSize = command.SizeBytes,
            UploadedAt = businessNowUtc,
        };
        var template = new DocumentTemplate
        {
            PortfolioId = command.PortfolioId,
            Kind = DocumentTemplateKind.Lease,
            RenderMode = DocumentTemplateRenderMode.Overlay,
            Status = DocumentTemplateStatus.Draft,
            Name = command.Name.Trim(),
            Description = DocumentTemplateCommandSupport.Clean(command.Description),
            OriginalStoredFile = stored,
            DefaultForPortfolio = command.DefaultForPortfolio,
            PropertyId = command.PropertyId,
            CreatedAtUtc = businessNowUtc,
            UpdatedAtUtc = businessNowUtc,
        };

        context.UseDatabaseWallClockForAudit(businessNowUtc);
        if (template.DefaultForPortfolio)
            await DocumentTemplateCommandSupport.ClearOtherDefaultAsync(
                context, command, db, template.Kind, template.PropertyId, null, businessNowUtc, ct);
        db.Add(stored);
        db.Add(template);
        context.BindSemanticAudit(stored, new AtomicSemanticAudit(
            command.PortfolioId, nameof(StoredFile), stored.Id, AuditLogOperation.Created,
            command.Actor.UserId,
            NewValues: JsonSerializer.Serialize(new
            {
                command.FileName, command.ContentType, command.SizeBytes, command.Sha256,
            }),
            ChangeReason: "Uploaded document template source PDF."));
        context.BindSemanticAudit(template, DocumentTemplateCommandSupport.TemplateAudit(
            command.PortfolioId, command.Actor.UserId, template, AuditLogOperation.Created,
            "Created document template from uploaded PDF."));
        await context.FlushBusinessAsync(ct);

        stored.EntityId = template.Id;
        pending.State = PendingFileUploadState.Finalized;
        pending.StoredFileId = stored.Id;
        pending.UpdatedAtUtc = businessNowUtc;
        await context.FlushBusinessAsync(ct);

        var snapshot = await DocumentTemplateCommandSupport.LoadTemplateSnapshotAsync(
            db, command.PortfolioId, template.Id, ct);
        DocumentTemplateCommandSupport.StageUpdate(
            context, command.PortfolioId, template.Id, command.DeliveryIdempotencyKey, businessNowUtc, "upload");
        return new(DocumentTemplateMutationOutcome.Applied, template.Id, Template: snapshot);
    }

    public static async Task AuthorizeAsync(
        RentalCommandDbContext db, FinalizeDocumentTemplateUploadCommand command,
        IAtomicCommandContext context, CancellationToken ct)
    {
        DocumentTemplateCommandSupport.Validate(command);
        var securityNowUtc = await context.ReadDatabaseClockUtcAsync(ct);
        if (!await DocumentTemplateCommandSupport.CanManageAsync(
                command.PortfolioId, command.Actor, command.PropertyId,
                db, command.BusinessNowUtc, securityNowUtc, ct))
            throw new UnauthorizedAccessException("The active assignment cannot upload this document template.");
    }
}

public sealed class UpdateDocumentTemplateHandler
    : IAtomicCommandHandler<UpdateDocumentTemplateCommand, DocumentTemplateMutationResult>
{
    public Task<DocumentTemplateMutationResult> HandleAsync(
        UpdateDocumentTemplateCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        throw DocumentTemplateWriteSupport.RetiredPath();

    public Task AuthorizeReplayAsync(
        UpdateDocumentTemplateCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        throw DocumentTemplateWriteSupport.RetiredPath();

    public static async Task<DocumentTemplateMutationResult> ExecuteAsync(
        RentalCommandDbContext db, UpdateDocumentTemplateCommand command,
        IAtomicCommandContext context, CancellationToken ct)
    {
        DocumentTemplateCommandSupport.Validate(command);
        var securityNowUtc = await context.ReadDatabaseClockUtcAsync(ct);
        var businessNowUtc = command.BusinessNowUtc;
        var template = await DocumentTemplateCommandSupport.AuthorizedTemplates(
                command.PortfolioId, command.Actor, db, businessNowUtc, securityNowUtc, tracking: true)
            .SingleOrDefaultAsync(item => item.Id == command.DocumentTemplateId, ct);
        if (template is null)
            return DocumentTemplateCommandSupport.NotFound(command.DocumentTemplateId, "Document template not found");

        var propertyId = command.PropertyId ?? template.PropertyId;
        if (!await DocumentTemplateCommandSupport.CanManageAsync(
                command.PortfolioId, command.Actor, propertyId,
                db, businessNowUtc, securityNowUtc, ct))
            return DocumentTemplateCommandSupport.NotFound(command.DocumentTemplateId, "Document template target not found");
        var originalFileId = command.OriginalStoredFileId ?? template.OriginalStoredFileId;
        var compiledFileId = command.CompiledStoredFileId ?? template.CompiledStoredFileId;
        if (!await DocumentTemplateCommandSupport.ReferencesValidAsync(
                db, command.PortfolioId, originalFileId, compiledFileId, ct))
            return DocumentTemplateCommandSupport.Invalid(
                command.DocumentTemplateId, "Document file not found in this portfolio");

        var oldValues = DocumentTemplateCommandSupport.TemplateValues(template);
        if (command.Name is not null) template.Name = command.Name.Trim();
        if (command.Description is not null) template.Description = DocumentTemplateCommandSupport.Clean(command.Description);
        if (command.Status.HasValue)
        {
            template.Status = command.Status.Value;
            template.ArchivedAtUtc = command.Status.Value == DocumentTemplateStatus.Archived ? businessNowUtc : null;
        }
        if (command.RenderMode.HasValue) template.RenderMode = command.RenderMode.Value;
        if (command.OriginalStoredFileId.HasValue) template.OriginalStoredFileId = command.OriginalStoredFileId;
        if (command.CompiledStoredFileId.HasValue) template.CompiledStoredFileId = command.CompiledStoredFileId;
        if (command.PropertyId.HasValue) template.PropertyId = command.PropertyId;
        if (command.DraftHtml is not null) template.DraftHtml = command.DraftHtml;
        if (command.DefaultForPortfolio.HasValue) template.DefaultForPortfolio = command.DefaultForPortfolio.Value;
        template.UpdatedAtUtc = businessNowUtc;
        template.Version++;

        context.UseDatabaseWallClockForAudit(businessNowUtc);
        if (template.DefaultForPortfolio)
            await DocumentTemplateCommandSupport.ClearOtherDefaultAsync(
                context, command, db, template.Kind, template.PropertyId, template.Id, businessNowUtc, ct);
        context.BindSemanticAudit(template, new AtomicSemanticAudit(
            command.PortfolioId, nameof(DocumentTemplate), template.Id, AuditLogOperation.Updated,
            command.Actor.UserId, OldValues: oldValues,
            NewValues: DocumentTemplateCommandSupport.TemplateValues(template),
            ChangeReason: "Updated document template."));
        await context.FlushBusinessAsync(ct);
        var snapshot = await DocumentTemplateCommandSupport.LoadTemplateSnapshotAsync(
            db, command.PortfolioId, template.Id, ct);
        DocumentTemplateCommandSupport.StageUpdate(
            context, command.PortfolioId, template.Id, command.DeliveryIdempotencyKey, businessNowUtc, "update");
        return new(DocumentTemplateMutationOutcome.Applied, template.Id, Template: snapshot);
    }

    public static Task AuthorizeAsync(
        RentalCommandDbContext db, UpdateDocumentTemplateCommand command,
        IAtomicCommandContext context, CancellationToken ct) =>
        DocumentTemplateCommandSupport.AuthorizeTemplateReplayAsync(
            command.PortfolioId, command.Actor, command.BusinessNowUtc, command.DocumentTemplateId, db, context, ct);
}

public sealed class AddDocumentTemplateFieldHandler
    : IAtomicCommandHandler<AddDocumentTemplateFieldCommand, DocumentTemplateMutationResult>
{
    public Task<DocumentTemplateMutationResult> HandleAsync(
        AddDocumentTemplateFieldCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        throw DocumentTemplateWriteSupport.RetiredPath();

    public Task AuthorizeReplayAsync(
        AddDocumentTemplateFieldCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        throw DocumentTemplateWriteSupport.RetiredPath();

    public static async Task<DocumentTemplateMutationResult> ExecuteAsync(
        RentalCommandDbContext db, AddDocumentTemplateFieldCommand command,
        IAtomicCommandContext context, CancellationToken ct)
    {
        DocumentTemplateCommandSupport.Validate(command);
        var securityNowUtc = await context.ReadDatabaseClockUtcAsync(ct);
        var businessNowUtc = command.BusinessNowUtc;
        var template = await DocumentTemplateCommandSupport.AuthorizedTemplates(
                command.PortfolioId, command.Actor, db, businessNowUtc, securityNowUtc, tracking: true)
            .SingleOrDefaultAsync(item => item.Id == command.DocumentTemplateId, ct);
        if (template is null)
            return DocumentTemplateCommandSupport.NotFound(command.DocumentTemplateId, "Document template not found");
        var extentError = DocumentTemplateCommandSupport.ValidateExtent(
            command.XPct, command.YPct, command.WidthPct, command.HeightPct);
        if (extentError is not null)
            return DocumentTemplateCommandSupport.Invalid(command.DocumentTemplateId, extentError);

        var field = new DocumentTemplateField
        {
            PortfolioId = command.PortfolioId,
            DocumentTemplateId = template.Id,
            FieldKey = command.FieldKey.Trim(),
            Label = command.Label.Trim(),
            Kind = command.Kind,
            SignerRole = command.SignerRole,
            PageNumber = command.PageNumber,
            XPct = command.XPct,
            YPct = command.YPct,
            WidthPct = command.WidthPct,
            HeightPct = command.HeightPct,
            Required = command.Required,
            Locked = command.Locked,
            SortOrder = command.SortOrder,
            DefaultText = DocumentTemplateCommandSupport.Clean(command.DefaultText),
        };
        db.Add(field);
        DocumentTemplateCommandSupport.Bump(template, businessNowUtc);
        context.UseDatabaseWallClockForAudit(businessNowUtc);
        context.BindSemanticAudit(field, DocumentTemplateCommandSupport.FieldAudit(
            command.PortfolioId, command.Actor.UserId, field, AuditLogOperation.Created,
            "Added document template field."));
        context.BindSemanticAudit(template, DocumentTemplateCommandSupport.TemplateAudit(
            command.PortfolioId, command.Actor.UserId, template, AuditLogOperation.Updated,
            "Incremented template version after adding a field."));
        await context.FlushBusinessAsync(ct);
        var snapshot = await DocumentTemplateCommandSupport.LoadFieldSnapshotAsync(
            db, command.PortfolioId, template.Id, field.Id, ct);
        DocumentTemplateCommandSupport.StageUpdate(
            context, command.PortfolioId, template.Id, command.DeliveryIdempotencyKey, businessNowUtc, "field-add");
        return new(DocumentTemplateMutationOutcome.Applied, template.Id, field.Id, Field: snapshot);
    }

    public static Task AuthorizeAsync(
        RentalCommandDbContext db, AddDocumentTemplateFieldCommand command,
        IAtomicCommandContext context, CancellationToken ct) =>
        DocumentTemplateCommandSupport.AuthorizeTemplateReplayAsync(
            command.PortfolioId, command.Actor, command.BusinessNowUtc, command.DocumentTemplateId, db, context, ct);
}

public sealed class UpdateDocumentTemplateFieldHandler
    : IAtomicCommandHandler<UpdateDocumentTemplateFieldCommand, DocumentTemplateMutationResult>
{
    public Task<DocumentTemplateMutationResult> HandleAsync(
        UpdateDocumentTemplateFieldCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        throw DocumentTemplateWriteSupport.RetiredPath();

    public Task AuthorizeReplayAsync(
        UpdateDocumentTemplateFieldCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        throw DocumentTemplateWriteSupport.RetiredPath();

    public static async Task<DocumentTemplateMutationResult> ExecuteAsync(
        RentalCommandDbContext db, UpdateDocumentTemplateFieldCommand command,
        IAtomicCommandContext context, CancellationToken ct)
    {
        DocumentTemplateCommandSupport.Validate(command);
        var securityNowUtc = await context.ReadDatabaseClockUtcAsync(ct);
        var businessNowUtc = command.BusinessNowUtc;
        var target = await DocumentTemplateCommandSupport.AuthorizedFieldTargetAsync(
            command.PortfolioId, command.Actor, command.DocumentTemplateId, command.FieldId,
            db, businessNowUtc, securityNowUtc, tracking: true, ct);
        if (target is null)
            return DocumentTemplateCommandSupport.NotFound(
                command.DocumentTemplateId, "Document template field not found", command.FieldId);

        var field = target.Field;
        var nextX = command.XPct ?? field.XPct;
        var nextY = command.YPct ?? field.YPct;
        var nextWidth = command.WidthPct ?? field.WidthPct;
        var nextHeight = command.HeightPct ?? field.HeightPct;
        var extentError = DocumentTemplateCommandSupport.ValidateExtent(nextX, nextY, nextWidth, nextHeight);
        if (extentError is not null)
            return DocumentTemplateCommandSupport.Invalid(command.DocumentTemplateId, extentError, command.FieldId);

        var oldValues = DocumentTemplateCommandSupport.FieldValues(field);
        if (command.FieldKey is not null) field.FieldKey = command.FieldKey.Trim();
        if (command.Label is not null) field.Label = command.Label.Trim();
        if (command.Kind.HasValue) field.Kind = command.Kind.Value;
        if (command.SignerRole.HasValue) field.SignerRole = command.SignerRole.Value;
        if (command.PageNumber.HasValue) field.PageNumber = command.PageNumber.Value;
        field.XPct = nextX;
        field.YPct = nextY;
        field.WidthPct = nextWidth;
        field.HeightPct = nextHeight;
        if (command.Required.HasValue) field.Required = command.Required.Value;
        if (command.Locked.HasValue) field.Locked = command.Locked.Value;
        if (command.SortOrder.HasValue) field.SortOrder = command.SortOrder.Value;
        if (command.DefaultText is not null) field.DefaultText = DocumentTemplateCommandSupport.Clean(command.DefaultText);
        DocumentTemplateCommandSupport.Bump(target.Template, businessNowUtc);

        context.UseDatabaseWallClockForAudit(businessNowUtc);
        context.BindSemanticAudit(field, new AtomicSemanticAudit(
            command.PortfolioId, nameof(DocumentTemplateField), field.Id, AuditLogOperation.Updated,
            command.Actor.UserId, OldValues: oldValues,
            NewValues: DocumentTemplateCommandSupport.FieldValues(field),
            ChangeReason: "Updated document template field."));
        context.BindSemanticAudit(target.Template, DocumentTemplateCommandSupport.TemplateAudit(
            command.PortfolioId, command.Actor.UserId, target.Template, AuditLogOperation.Updated,
            "Incremented template version after updating a field."));
        await context.FlushBusinessAsync(ct);
        var snapshot = await DocumentTemplateCommandSupport.LoadFieldSnapshotAsync(
            db, command.PortfolioId, target.Template.Id, field.Id, ct);
        DocumentTemplateCommandSupport.StageUpdate(
            context, command.PortfolioId, target.Template.Id, command.DeliveryIdempotencyKey, businessNowUtc, "field-update");
        return new(DocumentTemplateMutationOutcome.Applied, target.Template.Id, field.Id, Field: snapshot);
    }

    public static Task AuthorizeAsync(
        RentalCommandDbContext db, UpdateDocumentTemplateFieldCommand command,
        IAtomicCommandContext context, CancellationToken ct) =>
        DocumentTemplateCommandSupport.AuthorizeTemplateReplayAsync(
            command.PortfolioId, command.Actor, command.BusinessNowUtc, command.DocumentTemplateId, db, context, ct);
}

public sealed class DeleteDocumentTemplateFieldHandler
    : IAtomicCommandHandler<DeleteDocumentTemplateFieldCommand, DocumentTemplateMutationResult>
{
    public Task<DocumentTemplateMutationResult> HandleAsync(
        DeleteDocumentTemplateFieldCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        throw DocumentTemplateWriteSupport.RetiredPath();

    public Task AuthorizeReplayAsync(
        DeleteDocumentTemplateFieldCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        throw DocumentTemplateWriteSupport.RetiredPath();

    public static async Task<DocumentTemplateMutationResult> ExecuteAsync(
        RentalCommandDbContext db, DeleteDocumentTemplateFieldCommand command,
        IAtomicCommandContext context, CancellationToken ct)
    {
        DocumentTemplateCommandSupport.Validate(command);
        var securityNowUtc = await context.ReadDatabaseClockUtcAsync(ct);
        var businessNowUtc = command.BusinessNowUtc;
        var target = await DocumentTemplateCommandSupport.AuthorizedFieldTargetAsync(
            command.PortfolioId, command.Actor, command.DocumentTemplateId, command.FieldId,
            db, businessNowUtc, securityNowUtc, tracking: true, ct);
        if (target is null)
            return DocumentTemplateCommandSupport.NotFound(
                command.DocumentTemplateId, "Document template field not found", command.FieldId);

        db.Remove(target.Field);
        DocumentTemplateCommandSupport.Bump(target.Template, businessNowUtc);
        context.UseDatabaseWallClockForAudit(businessNowUtc);
        context.BindSemanticAudit(target.Field, DocumentTemplateCommandSupport.FieldAudit(
            command.PortfolioId, command.Actor.UserId, target.Field, AuditLogOperation.Deleted,
            "Deleted document template field."));
        context.BindSemanticAudit(target.Template, DocumentTemplateCommandSupport.TemplateAudit(
            command.PortfolioId, command.Actor.UserId, target.Template, AuditLogOperation.Updated,
            "Incremented template version after deleting a field."));
        await context.FlushBusinessAsync(ct);
        DocumentTemplateCommandSupport.StageUpdate(
            context, command.PortfolioId, target.Template.Id, command.DeliveryIdempotencyKey, businessNowUtc, "field-delete");
        return new(DocumentTemplateMutationOutcome.Applied, target.Template.Id, target.Field.Id);
    }

    public static Task AuthorizeAsync(
        RentalCommandDbContext db, DeleteDocumentTemplateFieldCommand command,
        IAtomicCommandContext context, CancellationToken ct) =>
        DocumentTemplateCommandSupport.AuthorizeTemplateReplayAsync(
            command.PortfolioId, command.Actor, command.BusinessNowUtc, command.DocumentTemplateId, db, context, ct);
}

internal static class DocumentTemplateCommandSupport
{
    private static readonly string[] TemplateCapabilityKeys =
    [
        CapabilityKeys.RentalsManage,
        CapabilityKeys.LeasingAgreementsPrepare,
    ];

    internal static IQueryable<DocumentTemplate> AuthorizedTemplates(
        int portfolioId, StaffOperationActor actor, RentalCommandDbContext db,
        DateTime businessNowUtc, DateTime securityNowUtc, bool tracking)
    {
        var properties = StaffOperationAuthorization.AuthorizedProperties(
            portfolioId, actor, TemplateCapabilityKeys, db, businessNowUtc, securityNowUtc);
        var templateScopes = AuthorizedTemplateScopes(
            portfolioId, actor, db, businessNowUtc, securityNowUtc);
        var query = db.Set<DocumentTemplate>().Where(template =>
            template.PortfolioId == portfolioId &&
            (template.PropertyId.HasValue
                ? properties.Any(property => property.Id == template.PropertyId.Value)
                : templateScopes.Any()));
        return tracking ? query.AsTracking() : query.AsNoTracking();
    }

    internal static Task<DocumentTemplateFieldTarget?> AuthorizedFieldTargetAsync(
        int portfolioId, StaffOperationActor actor, int documentTemplateId, int fieldId,
        RentalCommandDbContext db, DateTime businessNowUtc, DateTime securityNowUtc, bool tracking,
        CancellationToken ct) =>
        AuthorizedFieldTargetCoreAsync(
            portfolioId, actor, documentTemplateId, fieldId, db, businessNowUtc, securityNowUtc, tracking, ct);

    private static async Task<DocumentTemplateFieldTarget?> AuthorizedFieldTargetCoreAsync(
        int portfolioId, StaffOperationActor actor, int documentTemplateId, int fieldId,
        RentalCommandDbContext db, DateTime businessNowUtc, DateTime securityNowUtc, bool tracking,
        CancellationToken ct)
    {
        var templates = AuthorizedTemplates(portfolioId, actor, db, businessNowUtc, securityNowUtc, tracking)
            .Where(template => template.Id == documentTemplateId);
        var fields = tracking
            ? db.Set<DocumentTemplateField>().AsTracking()
            : db.Set<DocumentTemplateField>().AsNoTracking();
        fields = fields.Where(field => field.Id == fieldId);
        var target = await (from template in templates
                            join field in fields
                                on new { template.Id, template.PortfolioId }
                                equals new { Id = field.DocumentTemplateId, field.PortfolioId }
                            select new
                            {
                                Template = template,
                                Field = field,
                            })
            .SingleOrDefaultAsync(ct);
        return target is null
            ? null
            : new DocumentTemplateFieldTarget(target.Template, target.Field);
    }

    internal static Task<bool> CanManageAsync(
        int portfolioId, StaffOperationActor actor, int? propertyId,
        RentalCommandDbContext db, DateTime businessNowUtc, DateTime securityNowUtc, CancellationToken ct)
    {
        if (propertyId.HasValue)
        {
            return StaffOperationAuthorization.CanManagePropertyAsync(
                portfolioId, actor, propertyId.Value, TemplateCapabilityKeys,
                db, businessNowUtc, securityNowUtc, ct);
        }

        return AuthorizedTemplateScopes(
            portfolioId, actor, db, businessNowUtc, securityNowUtc).AnyAsync(ct);
    }

    private static IQueryable<MembershipRoleAssignment> AuthorizedTemplateScopes(
        int portfolioId, StaffOperationActor actor, RentalCommandDbContext db,
        DateTime businessNowUtc, DateTime securityNowUtc) =>
        StaffOperationAuthorization.ActiveAssignments(
                portfolioId, actor, TemplateCapabilityKeys, db, businessNowUtc, securityNowUtc)
            .Where(assignment =>
                assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties &&
                assignment.SelectedProperties.Any(selected => selected.PortfolioId == portfolioId));

    internal static async Task<bool> ReferencesValidAsync(
        RentalCommandDbContext db, int portfolioId,
        int? originalStoredFileId, int? compiledStoredFileId, CancellationToken ct) =>
        await db.Set<Portfolio>().AsNoTracking()
            .Where(portfolio => portfolio.Id == portfolioId)
            .Select(_ =>
                (!originalStoredFileId.HasValue || db.Set<StoredFile>().AsNoTracking().Any(file =>
                    file.Id == originalStoredFileId.Value && file.PortfolioId == portfolioId && file.DeletedAt == null)) &&
                (!compiledStoredFileId.HasValue || db.Set<StoredFile>().AsNoTracking().Any(file =>
                    file.Id == compiledStoredFileId.Value && file.PortfolioId == portfolioId && file.DeletedAt == null)))
            .SingleOrDefaultAsync(ct);

    internal static async Task ClearOtherDefaultAsync<TCommand>(
        IAtomicCommandContext context, TCommand command, RentalCommandDbContext db,
        DocumentTemplateKind kind,
        int? propertyId, int? exceptId, DateTime businessNowUtc, CancellationToken ct)
        where TCommand : notnull, IAtomicCommandData
    {
        var portfolioId = command switch
        {
            CreateDocumentTemplateCommand value => value.PortfolioId,
            FinalizeDocumentTemplateUploadCommand value => value.PortfolioId,
            UpdateDocumentTemplateCommand value => value.PortfolioId,
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
        var actorUserId = command switch
        {
            CreateDocumentTemplateCommand value => value.Actor.UserId,
            FinalizeDocumentTemplateUploadCommand value => value.Actor.UserId,
            UpdateDocumentTemplateCommand value => value.Actor.UserId,
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
        var prior = await db.Set<DocumentTemplate>()
            .SingleOrDefaultAsync(template =>
                template.PortfolioId == portfolioId && template.Kind == kind &&
                template.PropertyId == propertyId && template.DefaultForPortfolio &&
                (!exceptId.HasValue || template.Id != exceptId.Value), ct);
        if (prior is null) return;
        prior.DefaultForPortfolio = false;
        prior.UpdatedAtUtc = businessNowUtc;
        context.BindSemanticAudit(prior, TemplateAudit(
            portfolioId, actorUserId, prior, AuditLogOperation.Updated,
            "Replaced the previous default document template."));
    }

    internal static async Task AuthorizeTemplateReplayAsync(
        int portfolioId, StaffOperationActor actor, DateTime businessNowUtc, int templateId,
        RentalCommandDbContext db, IAtomicCommandContext context, CancellationToken ct)
    {
        var securityNowUtc = await context.ReadDatabaseClockUtcAsync(ct);
        if (!await AuthorizedTemplates(portfolioId, actor, db, businessNowUtc, securityNowUtc, tracking: false)
                .AnyAsync(template => template.Id == templateId, ct))
            throw new UnauthorizedAccessException("The active assignment cannot manage this document template.");
    }

    internal static Task<DocumentTemplateSnapshot> LoadTemplateSnapshotAsync(
        RentalCommandDbContext db, int portfolioId, int templateId, CancellationToken ct) =>
        db.Set<DocumentTemplate>().AsNoTracking()
            .Where(template => template.Id == templateId && template.PortfolioId == portfolioId)
            .Select(template => new DocumentTemplateSnapshot(
                template.Id, template.PortfolioId, template.Kind, template.Status, template.RenderMode,
                template.Name, template.Description, template.OriginalStoredFileId,
                template.CompiledStoredFileId, template.DraftHtml != null && template.DraftHtml != string.Empty,
                template.DefaultForPortfolio, template.PropertyId, template.Version, template.Fields.Count,
                template.CreatedAtUtc, template.UpdatedAtUtc, template.ArchivedAtUtc,
                template.Fields.OrderBy(field => field.SortOrder).ThenBy(field => field.Id)
                    .Select(field => new DocumentTemplateFieldSnapshot(
                        field.Id, field.DocumentTemplateId, field.FieldKey, field.Label, field.Kind,
                        field.SignerRole, field.PageNumber, field.XPct, field.YPct, field.WidthPct,
                        field.HeightPct, field.Required, field.Locked, field.SortOrder, field.DefaultText))
                    .ToArray()))
            .SingleAsync(ct);

    internal static Task<DocumentTemplateFieldSnapshot> LoadFieldSnapshotAsync(
        RentalCommandDbContext db, int portfolioId, int templateId, int fieldId, CancellationToken ct) =>
        (from template in db.Set<DocumentTemplate>().AsNoTracking()
         join field in db.Set<DocumentTemplateField>().AsNoTracking()
             on new { template.Id, template.PortfolioId }
             equals new { Id = field.DocumentTemplateId, field.PortfolioId }
         where template.PortfolioId == portfolioId && template.Id == templateId && field.Id == fieldId
         select new DocumentTemplateFieldSnapshot(
             field.Id, field.DocumentTemplateId, field.FieldKey, field.Label, field.Kind,
             field.SignerRole, field.PageNumber, field.XPct, field.YPct, field.WidthPct,
             field.HeightPct, field.Required, field.Locked, field.SortOrder, field.DefaultText))
        .SingleAsync(ct);

    internal static DocumentTemplateMutationResult NotFound(int templateId, string error, int? fieldId = null) =>
        new(DocumentTemplateMutationOutcome.NotFound, templateId, fieldId, Error: error);

    internal static DocumentTemplateMutationResult Invalid(int templateId, string error, int? fieldId = null) =>
        new(DocumentTemplateMutationOutcome.Invalid, templateId, fieldId, Error: error);

    internal static void Bump(DocumentTemplate template, DateTime businessNowUtc)
    {
        template.Version++;
        template.UpdatedAtUtc = businessNowUtc;
    }

    internal static string? ValidateExtent(double x, double y, double width, double height)
    {
        if (x + width > 1) return "Field extends past the right edge of the page.";
        return y + height > 1 ? "Field extends past the bottom edge of the page." : null;
    }

    internal static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static string TemplateValues(DocumentTemplate template) => JsonSerializer.Serialize(new
    {
        template.Kind, template.Status, template.RenderMode, template.Name, template.Description,
        template.OriginalStoredFileId, template.CompiledStoredFileId, template.DefaultForPortfolio,
        template.PropertyId, template.Version, template.ArchivedAtUtc,
    });

    internal static string FieldValues(DocumentTemplateField field) => JsonSerializer.Serialize(new
    {
        field.PortfolioId, field.DocumentTemplateId, field.FieldKey, field.Label,
        field.Kind, field.SignerRole, field.PageNumber,
        field.XPct, field.YPct, field.WidthPct, field.HeightPct, field.Required,
        field.Locked, field.SortOrder, field.DefaultText,
    });

    internal static AtomicSemanticAudit TemplateAudit(
        int portfolioId, int userId, DocumentTemplate template,
        AuditLogOperation operation, string reason) => new(
        portfolioId, nameof(DocumentTemplate), template.Id, operation, userId,
        NewValues: TemplateValues(template), ChangeReason: reason);

    internal static AtomicSemanticAudit FieldAudit(
        int portfolioId, int userId, DocumentTemplateField field,
        AuditLogOperation operation, string reason) => new(
        portfolioId, nameof(DocumentTemplateField), field.Id, operation, userId,
        NewValues: FieldValues(field), ChangeReason: reason);

    internal static void StageUpdate(
        IAtomicCommandContext context, int portfolioId, int templateId,
        string key, DateTime businessNowUtc, string operation) =>
        context.StageOutbox(CreateWorkOrderHandler.DataUpdate(
            portfolioId, nameof(DocumentTemplate), templateId,
            $"document-template:{operation}:{key}", businessNowUtc, operation));

    internal static void Validate(CreateDocumentTemplateCommand command)
    {
        ValidateCommon(command.PortfolioId, command.Actor, command.BusinessNowUtc, command.DeliveryIdempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Name);
    }

    internal static void Validate(UpdateDocumentTemplateCommand command)
    {
        ValidateCommon(command.PortfolioId, command.Actor, command.BusinessNowUtc, command.DeliveryIdempotencyKey);
        if (command.DocumentTemplateId <= 0) throw new ArgumentOutOfRangeException(nameof(command.DocumentTemplateId));
        if (command.Name is not null && string.IsNullOrWhiteSpace(command.Name))
            throw new ArgumentException("Template name cannot be blank.", nameof(command.Name));
    }

    internal static void Validate(FinalizeDocumentTemplateUploadCommand command)
    {
        ValidateCommon(command.PortfolioId, command.Actor, command.BusinessNowUtc, command.DeliveryIdempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.OperationKeyHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.RequestFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.StoragePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.FileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ContentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Sha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Name);
        if (command.PendingUploadId == Guid.Empty || command.SizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(command));
    }

    internal static void Validate(AddDocumentTemplateFieldCommand command)
    {
        ValidateCommon(command.PortfolioId, command.Actor, command.BusinessNowUtc, command.DeliveryIdempotencyKey);
        if (command.DocumentTemplateId <= 0 || command.PageNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(command));
        ArgumentException.ThrowIfNullOrWhiteSpace(command.FieldKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Label);
    }

    internal static void Validate(UpdateDocumentTemplateFieldCommand command)
    {
        ValidateCommon(command.PortfolioId, command.Actor, command.BusinessNowUtc, command.DeliveryIdempotencyKey);
        if (command.DocumentTemplateId <= 0 || command.FieldId <= 0)
            throw new ArgumentOutOfRangeException(nameof(command));
        if (command.FieldKey is not null && string.IsNullOrWhiteSpace(command.FieldKey))
            throw new ArgumentException("Field key cannot be blank.", nameof(command.FieldKey));
        if (command.Label is not null && string.IsNullOrWhiteSpace(command.Label))
            throw new ArgumentException("Field label cannot be blank.", nameof(command.Label));
    }

    internal static void Validate(DeleteDocumentTemplateFieldCommand command)
    {
        ValidateCommon(command.PortfolioId, command.Actor, command.BusinessNowUtc, command.DeliveryIdempotencyKey);
        if (command.DocumentTemplateId <= 0 || command.FieldId <= 0)
            throw new ArgumentOutOfRangeException(nameof(command));
    }

    private static void ValidateCommon(
        int portfolioId, StaffOperationActor actor, DateTime businessNowUtc, string idempotencyKey)
    {
        if (portfolioId <= 0 || actor.UserId <= 0 || actor.AuthSessionId == Guid.Empty ||
            actor.AccessContextId <= 0 || actor.AccessRevision <= 0 || businessNowUtc == default)
            throw new ArgumentOutOfRangeException(nameof(portfolioId), "Document template command scope is invalid.");
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
    }
}

internal sealed record DocumentTemplateFieldTarget(
    DocumentTemplate Template,
    DocumentTemplateField Field);
