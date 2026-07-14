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

public sealed class CreateDocumentTemplateHandler
    : IAtomicCommandHandler<CreateDocumentTemplateCommand, DocumentTemplateMutationResult>,
      IAtomicReplayAuthorizer<CreateDocumentTemplateCommand>
{
    public async Task<DocumentTemplateMutationResult> HandleAsync(
        CreateDocumentTemplateCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        DocumentTemplateCommandSupport.Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.Portfolio, command.PortfolioId, ct);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await DocumentTemplateCommandSupport.CanManageAsync(
                command.PortfolioId, command.Actor, command.PropertyId, attempt.Persistence, now, ct))
            return DocumentTemplateCommandSupport.NotFound(0, "Document template target not found");
        if (!await DocumentTemplateCommandSupport.ReferencesValidAsync(
                attempt.Persistence, command.PortfolioId,
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
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        attempt.UseDatabaseWallClockForAudit(now);
        if (template.DefaultForPortfolio)
            await DocumentTemplateCommandSupport.ClearOtherDefaultAsync(
                attempt, command, template.Kind, template.PropertyId, null, now, ct);
        attempt.Persistence.Add(template);
        attempt.BindSemanticAudit(template, DocumentTemplateCommandSupport.TemplateAudit(
            command.PortfolioId, command.Actor.UserId, template, AuditLogOperation.Created,
            "Created document template."));
        await attempt.FlushBusinessAsync(ct);
        var snapshot = await DocumentTemplateCommandSupport.LoadTemplateSnapshotAsync(
            attempt.Persistence, command.PortfolioId, template.Id, ct);
        DocumentTemplateCommandSupport.StageUpdate(
            attempt, command.PortfolioId, template.Id, command.DeliveryIdempotencyKey, now, "create");
        return new(DocumentTemplateMutationOutcome.Applied, template.Id, Template: snapshot);
    }

    public async Task AuthorizeReplayAsync(
        CreateDocumentTemplateCommand command, IAtomicPersistenceSession persistence, CancellationToken ct)
    {
        DocumentTemplateCommandSupport.Validate(command);
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await DocumentTemplateCommandSupport.CanManageAsync(
                command.PortfolioId, command.Actor, command.PropertyId, persistence, now, ct))
            throw new UnauthorizedAccessException("The active assignment cannot create this document template.");
    }
}

public sealed class UpdateDocumentTemplateHandler
    : IAtomicCommandHandler<UpdateDocumentTemplateCommand, DocumentTemplateMutationResult>,
      IAtomicReplayAuthorizer<UpdateDocumentTemplateCommand>
{
    public async Task<DocumentTemplateMutationResult> HandleAsync(
        UpdateDocumentTemplateCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        DocumentTemplateCommandSupport.Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.Portfolio, command.PortfolioId, ct);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var template = await DocumentTemplateCommandSupport.AuthorizedTemplates(
                command.PortfolioId, command.Actor, attempt.Persistence, now, tracking: true)
            .SingleOrDefaultAsync(item => item.Id == command.DocumentTemplateId, ct);
        if (template is null)
            return DocumentTemplateCommandSupport.NotFound(command.DocumentTemplateId, "Document template not found");

        var propertyId = command.PropertyId ?? template.PropertyId;
        if (!await DocumentTemplateCommandSupport.CanManageAsync(
                command.PortfolioId, command.Actor, propertyId, attempt.Persistence, now, ct))
            return DocumentTemplateCommandSupport.NotFound(command.DocumentTemplateId, "Document template target not found");
        var originalFileId = command.OriginalStoredFileId ?? template.OriginalStoredFileId;
        var compiledFileId = command.CompiledStoredFileId ?? template.CompiledStoredFileId;
        if (!await DocumentTemplateCommandSupport.ReferencesValidAsync(
                attempt.Persistence, command.PortfolioId, originalFileId, compiledFileId, ct))
            return DocumentTemplateCommandSupport.Invalid(
                command.DocumentTemplateId, "Document file not found in this portfolio");

        var oldValues = DocumentTemplateCommandSupport.TemplateValues(template);
        if (command.Name is not null) template.Name = command.Name.Trim();
        if (command.Description is not null) template.Description = DocumentTemplateCommandSupport.Clean(command.Description);
        if (command.Status.HasValue)
        {
            template.Status = command.Status.Value;
            template.ArchivedAtUtc = command.Status.Value == DocumentTemplateStatus.Archived ? now : null;
        }
        if (command.RenderMode.HasValue) template.RenderMode = command.RenderMode.Value;
        if (command.OriginalStoredFileId.HasValue) template.OriginalStoredFileId = command.OriginalStoredFileId;
        if (command.CompiledStoredFileId.HasValue) template.CompiledStoredFileId = command.CompiledStoredFileId;
        if (command.PropertyId.HasValue) template.PropertyId = command.PropertyId;
        if (command.DraftHtml is not null) template.DraftHtml = command.DraftHtml;
        if (command.DefaultForPortfolio.HasValue) template.DefaultForPortfolio = command.DefaultForPortfolio.Value;
        template.UpdatedAtUtc = now;
        template.Version++;

        attempt.UseDatabaseWallClockForAudit(now);
        if (template.DefaultForPortfolio)
            await DocumentTemplateCommandSupport.ClearOtherDefaultAsync(
                attempt, command, template.Kind, template.PropertyId, template.Id, now, ct);
        attempt.BindSemanticAudit(template, new AtomicSemanticAudit(
            command.PortfolioId, nameof(DocumentTemplate), template.Id, AuditLogOperation.Updated,
            command.Actor.UserId, OldValues: oldValues,
            NewValues: DocumentTemplateCommandSupport.TemplateValues(template),
            ChangeReason: "Updated document template."));
        await attempt.FlushBusinessAsync(ct);
        var snapshot = await DocumentTemplateCommandSupport.LoadTemplateSnapshotAsync(
            attempt.Persistence, command.PortfolioId, template.Id, ct);
        DocumentTemplateCommandSupport.StageUpdate(
            attempt, command.PortfolioId, template.Id, command.DeliveryIdempotencyKey, now, "update");
        return new(DocumentTemplateMutationOutcome.Applied, template.Id, Template: snapshot);
    }

    public Task AuthorizeReplayAsync(
        UpdateDocumentTemplateCommand command, IAtomicPersistenceSession persistence, CancellationToken ct) =>
        DocumentTemplateCommandSupport.AuthorizeTemplateReplayAsync(
            command.PortfolioId, command.Actor, command.DocumentTemplateId, persistence, ct);
}

public sealed class AddDocumentTemplateFieldHandler
    : IAtomicCommandHandler<AddDocumentTemplateFieldCommand, DocumentTemplateMutationResult>,
      IAtomicReplayAuthorizer<AddDocumentTemplateFieldCommand>
{
    public async Task<DocumentTemplateMutationResult> HandleAsync(
        AddDocumentTemplateFieldCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        DocumentTemplateCommandSupport.Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.Portfolio, command.PortfolioId, ct);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var template = await DocumentTemplateCommandSupport.AuthorizedTemplates(
                command.PortfolioId, command.Actor, attempt.Persistence, now, tracking: true)
            .SingleOrDefaultAsync(item => item.Id == command.DocumentTemplateId, ct);
        if (template is null)
            return DocumentTemplateCommandSupport.NotFound(command.DocumentTemplateId, "Document template not found");
        var extentError = DocumentTemplateCommandSupport.ValidateExtent(
            command.XPct, command.YPct, command.WidthPct, command.HeightPct);
        if (extentError is not null)
            return DocumentTemplateCommandSupport.Invalid(command.DocumentTemplateId, extentError);

        var field = new DocumentTemplateField
        {
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
        attempt.Persistence.Add(field);
        DocumentTemplateCommandSupport.Bump(template, now);
        attempt.UseDatabaseWallClockForAudit(now);
        attempt.BindSemanticAudit(field, DocumentTemplateCommandSupport.FieldAudit(
            command.PortfolioId, command.Actor.UserId, field, AuditLogOperation.Created,
            "Added document template field."));
        attempt.BindSemanticAudit(template, DocumentTemplateCommandSupport.TemplateAudit(
            command.PortfolioId, command.Actor.UserId, template, AuditLogOperation.Updated,
            "Incremented template version after adding a field."));
        await attempt.FlushBusinessAsync(ct);
        var snapshot = await DocumentTemplateCommandSupport.LoadFieldSnapshotAsync(
            attempt.Persistence, command.PortfolioId, template.Id, field.Id, ct);
        DocumentTemplateCommandSupport.StageUpdate(
            attempt, command.PortfolioId, template.Id, command.DeliveryIdempotencyKey, now, "field-add");
        return new(DocumentTemplateMutationOutcome.Applied, template.Id, field.Id, Field: snapshot);
    }

    public Task AuthorizeReplayAsync(
        AddDocumentTemplateFieldCommand command, IAtomicPersistenceSession persistence, CancellationToken ct) =>
        DocumentTemplateCommandSupport.AuthorizeTemplateReplayAsync(
            command.PortfolioId, command.Actor, command.DocumentTemplateId, persistence, ct);
}

public sealed class UpdateDocumentTemplateFieldHandler
    : IAtomicCommandHandler<UpdateDocumentTemplateFieldCommand, DocumentTemplateMutationResult>,
      IAtomicReplayAuthorizer<UpdateDocumentTemplateFieldCommand>
{
    public async Task<DocumentTemplateMutationResult> HandleAsync(
        UpdateDocumentTemplateFieldCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        DocumentTemplateCommandSupport.Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.Portfolio, command.PortfolioId, ct);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var target = await DocumentTemplateCommandSupport.AuthorizedFieldTargets(
                command.PortfolioId, command.Actor, attempt.Persistence, now, tracking: true)
            .SingleOrDefaultAsync(item => item.Template.Id == command.DocumentTemplateId
                && item.Field.Id == command.FieldId, ct);
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
        DocumentTemplateCommandSupport.Bump(target.Template, now);

        attempt.UseDatabaseWallClockForAudit(now);
        attempt.BindSemanticAudit(field, new AtomicSemanticAudit(
            command.PortfolioId, nameof(DocumentTemplateField), field.Id, AuditLogOperation.Updated,
            command.Actor.UserId, OldValues: oldValues,
            NewValues: DocumentTemplateCommandSupport.FieldValues(field),
            ChangeReason: "Updated document template field."));
        attempt.BindSemanticAudit(target.Template, DocumentTemplateCommandSupport.TemplateAudit(
            command.PortfolioId, command.Actor.UserId, target.Template, AuditLogOperation.Updated,
            "Incremented template version after updating a field."));
        await attempt.FlushBusinessAsync(ct);
        var snapshot = await DocumentTemplateCommandSupport.LoadFieldSnapshotAsync(
            attempt.Persistence, command.PortfolioId, target.Template.Id, field.Id, ct);
        DocumentTemplateCommandSupport.StageUpdate(
            attempt, command.PortfolioId, target.Template.Id, command.DeliveryIdempotencyKey, now, "field-update");
        return new(DocumentTemplateMutationOutcome.Applied, target.Template.Id, field.Id, Field: snapshot);
    }

    public Task AuthorizeReplayAsync(
        UpdateDocumentTemplateFieldCommand command, IAtomicPersistenceSession persistence, CancellationToken ct) =>
        DocumentTemplateCommandSupport.AuthorizeTemplateReplayAsync(
            command.PortfolioId, command.Actor, command.DocumentTemplateId, persistence, ct);
}

public sealed class DeleteDocumentTemplateFieldHandler
    : IAtomicCommandHandler<DeleteDocumentTemplateFieldCommand, DocumentTemplateMutationResult>,
      IAtomicReplayAuthorizer<DeleteDocumentTemplateFieldCommand>
{
    public async Task<DocumentTemplateMutationResult> HandleAsync(
        DeleteDocumentTemplateFieldCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        DocumentTemplateCommandSupport.Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.Portfolio, command.PortfolioId, ct);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var target = await DocumentTemplateCommandSupport.AuthorizedFieldTargets(
                command.PortfolioId, command.Actor, attempt.Persistence, now, tracking: true)
            .SingleOrDefaultAsync(item => item.Template.Id == command.DocumentTemplateId
                && item.Field.Id == command.FieldId, ct);
        if (target is null)
            return DocumentTemplateCommandSupport.NotFound(
                command.DocumentTemplateId, "Document template field not found", command.FieldId);

        attempt.Persistence.Remove(target.Field);
        DocumentTemplateCommandSupport.Bump(target.Template, now);
        attempt.UseDatabaseWallClockForAudit(now);
        attempt.BindSemanticAudit(target.Field, DocumentTemplateCommandSupport.FieldAudit(
            command.PortfolioId, command.Actor.UserId, target.Field, AuditLogOperation.Deleted,
            "Deleted document template field."));
        attempt.BindSemanticAudit(target.Template, DocumentTemplateCommandSupport.TemplateAudit(
            command.PortfolioId, command.Actor.UserId, target.Template, AuditLogOperation.Updated,
            "Incremented template version after deleting a field."));
        await attempt.FlushBusinessAsync(ct);
        DocumentTemplateCommandSupport.StageUpdate(
            attempt, command.PortfolioId, target.Template.Id, command.DeliveryIdempotencyKey, now, "field-delete");
        return new(DocumentTemplateMutationOutcome.Applied, target.Template.Id, target.Field.Id);
    }

    public Task AuthorizeReplayAsync(
        DeleteDocumentTemplateFieldCommand command, IAtomicPersistenceSession persistence, CancellationToken ct) =>
        DocumentTemplateCommandSupport.AuthorizeTemplateReplayAsync(
            command.PortfolioId, command.Actor, command.DocumentTemplateId, persistence, ct);
}

internal static class DocumentTemplateCommandSupport
{
    internal static IQueryable<DocumentTemplate> AuthorizedTemplates(
        int portfolioId, StaffOperationActor actor, IAtomicPersistenceSession persistence,
        DateTime now, bool tracking)
    {
        var properties = StaffOperationAuthorization.AuthorizedProperties(
            portfolioId, actor, CapabilityKeys.LeasingAgreementsPrepare, persistence, now);
        var allProperties = StaffOperationAuthorization.ActiveAssignments(
                portfolioId, actor, CapabilityKeys.LeasingAgreementsPrepare, persistence, now)
            .Where(assignment => assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties);
        var query = persistence.Query<DocumentTemplate>().Where(template =>
            template.PortfolioId == portfolioId &&
            (template.PropertyId.HasValue
                ? properties.Any(property => property.Id == template.PropertyId.Value)
                : allProperties.Any()));
        return tracking ? query : query.AsNoTracking();
    }

    internal static IQueryable<DocumentTemplateFieldTarget> AuthorizedFieldTargets(
        int portfolioId, StaffOperationActor actor, IAtomicPersistenceSession persistence,
        DateTime now, bool tracking)
    {
        var templates = AuthorizedTemplates(portfolioId, actor, persistence, now, tracking);
        var fields = tracking
            ? persistence.Query<DocumentTemplateField>()
            : persistence.Query<DocumentTemplateField>().AsNoTracking();
        return from template in templates
               join field in fields on template.Id equals field.DocumentTemplateId
               select new DocumentTemplateFieldTarget(template, field);
    }

    internal static Task<bool> CanManageAsync(
        int portfolioId, StaffOperationActor actor, int? propertyId,
        IAtomicPersistenceSession persistence, DateTime now, CancellationToken ct) =>
        StaffOperationAuthorization.CanManageNullablePropertyAsync(
            portfolioId, actor, propertyId, CapabilityKeys.LeasingAgreementsPrepare,
            persistence, now, ct);

    internal static async Task<bool> ReferencesValidAsync(
        IAtomicPersistenceSession persistence, int portfolioId,
        int? originalStoredFileId, int? compiledStoredFileId, CancellationToken ct) =>
        await persistence.Query<Portfolio>().AsNoTracking()
            .Where(portfolio => portfolio.Id == portfolioId)
            .Select(_ =>
                (!originalStoredFileId.HasValue || persistence.Query<StoredFile>().AsNoTracking().Any(file =>
                    file.Id == originalStoredFileId.Value && file.PortfolioId == portfolioId && file.DeletedAt == null)) &&
                (!compiledStoredFileId.HasValue || persistence.Query<StoredFile>().AsNoTracking().Any(file =>
                    file.Id == compiledStoredFileId.Value && file.PortfolioId == portfolioId && file.DeletedAt == null)))
            .SingleOrDefaultAsync(ct);

    internal static async Task ClearOtherDefaultAsync<TCommand>(
        IAtomicWriteAttempt attempt, TCommand command, DocumentTemplateKind kind,
        int? propertyId, int? exceptId, DateTime now, CancellationToken ct)
        where TCommand : notnull, IAtomicCommandData
    {
        var portfolioId = command switch
        {
            CreateDocumentTemplateCommand value => value.PortfolioId,
            UpdateDocumentTemplateCommand value => value.PortfolioId,
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
        var actorUserId = command switch
        {
            CreateDocumentTemplateCommand value => value.Actor.UserId,
            UpdateDocumentTemplateCommand value => value.Actor.UserId,
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
        var prior = await attempt.Persistence.Query<DocumentTemplate>()
            .SingleOrDefaultAsync(template =>
                template.PortfolioId == portfolioId && template.Kind == kind &&
                template.PropertyId == propertyId && template.DefaultForPortfolio &&
                (!exceptId.HasValue || template.Id != exceptId.Value), ct);
        if (prior is null) return;
        prior.DefaultForPortfolio = false;
        prior.UpdatedAtUtc = now;
        attempt.BindSemanticAudit(prior, TemplateAudit(
            portfolioId, actorUserId, prior, AuditLogOperation.Updated,
            "Replaced the previous default document template."));
    }

    internal static async Task AuthorizeTemplateReplayAsync(
        int portfolioId, StaffOperationActor actor, int templateId,
        IAtomicPersistenceSession persistence, CancellationToken ct)
    {
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await AuthorizedTemplates(portfolioId, actor, persistence, now, tracking: false)
                .AnyAsync(template => template.Id == templateId, ct))
            throw new UnauthorizedAccessException("The active assignment cannot manage this document template.");
    }

    internal static Task<DocumentTemplateSnapshot> LoadTemplateSnapshotAsync(
        IAtomicPersistenceSession persistence, int portfolioId, int templateId, CancellationToken ct) =>
        persistence.Query<DocumentTemplate>().AsNoTracking()
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
        IAtomicPersistenceSession persistence, int portfolioId, int templateId, int fieldId, CancellationToken ct) =>
        (from template in persistence.Query<DocumentTemplate>().AsNoTracking()
         join field in persistence.Query<DocumentTemplateField>().AsNoTracking()
             on template.Id equals field.DocumentTemplateId
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

    internal static void Bump(DocumentTemplate template, DateTime now)
    {
        template.Version++;
        template.UpdatedAtUtc = now;
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
        field.FieldKey, field.Label, field.Kind, field.SignerRole, field.PageNumber,
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
        IAtomicWriteAttempt attempt, int portfolioId, int templateId,
        string key, DateTime now, string operation) =>
        attempt.StageOutbox(CreateWorkOrderHandler.DataUpdate(
            portfolioId, nameof(DocumentTemplate), templateId,
            $"document-template:{operation}:{key}", now, operation));

    internal static void Validate(CreateDocumentTemplateCommand command)
    {
        ValidateCommon(command.PortfolioId, command.Actor, command.DeliveryIdempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Name);
    }

    internal static void Validate(UpdateDocumentTemplateCommand command)
    {
        ValidateCommon(command.PortfolioId, command.Actor, command.DeliveryIdempotencyKey);
        if (command.DocumentTemplateId <= 0) throw new ArgumentOutOfRangeException(nameof(command.DocumentTemplateId));
        if (command.Name is not null && string.IsNullOrWhiteSpace(command.Name))
            throw new ArgumentException("Template name cannot be blank.", nameof(command.Name));
    }

    internal static void Validate(AddDocumentTemplateFieldCommand command)
    {
        ValidateCommon(command.PortfolioId, command.Actor, command.DeliveryIdempotencyKey);
        if (command.DocumentTemplateId <= 0 || command.PageNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(command));
        ArgumentException.ThrowIfNullOrWhiteSpace(command.FieldKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Label);
    }

    internal static void Validate(UpdateDocumentTemplateFieldCommand command)
    {
        ValidateCommon(command.PortfolioId, command.Actor, command.DeliveryIdempotencyKey);
        if (command.DocumentTemplateId <= 0 || command.FieldId <= 0)
            throw new ArgumentOutOfRangeException(nameof(command));
        if (command.FieldKey is not null && string.IsNullOrWhiteSpace(command.FieldKey))
            throw new ArgumentException("Field key cannot be blank.", nameof(command.FieldKey));
        if (command.Label is not null && string.IsNullOrWhiteSpace(command.Label))
            throw new ArgumentException("Field label cannot be blank.", nameof(command.Label));
    }

    internal static void Validate(DeleteDocumentTemplateFieldCommand command)
    {
        ValidateCommon(command.PortfolioId, command.Actor, command.DeliveryIdempotencyKey);
        if (command.DocumentTemplateId <= 0 || command.FieldId <= 0)
            throw new ArgumentOutOfRangeException(nameof(command));
    }

    private static void ValidateCommon(int portfolioId, StaffOperationActor actor, string idempotencyKey)
    {
        if (portfolioId <= 0 || actor.UserId <= 0 || actor.AuthSessionId == Guid.Empty ||
            actor.AccessContextId <= 0 || actor.AccessRevision <= 0)
            throw new ArgumentOutOfRangeException(nameof(portfolioId), "Document template command scope is invalid.");
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
    }
}

internal sealed record DocumentTemplateFieldTarget(
    DocumentTemplate Template,
    DocumentTemplateField Field);
