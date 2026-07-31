using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;
using RentalCommand.Data.Atomic;

namespace RentalCommand.Data.Sandbox;

/// <summary>
/// Narrow data-layer port for the demo-seed domain handler. It is reachable only from an admitted
/// atomic context and does not create, commit, or own a transaction.
/// </summary>
public static class DemoSeedPersistence
{
    public static async Task ReconcileLeaseFactsAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        int portfolioId,
        int actorUserId,
        DateTime updatedAtUtc,
        Guid rowVersion,
        CancellationToken ct)
    {
        await db.ExecuteAtomicSqlMutationAsync<int>(context, $$"""
            UPDATE "LeaseManagements" AS relationship
            SET "PlannedMoveOutAtUtc" = NULL,
                "EndingDispositionDecidedAtUtc" = NULL,
                "EndingDispositionDecidedByUserId" = NULL,
                "UpdatedAtUtc" = {{updatedAtUtc}},
                "RowVersion" = {{rowVersion}},
                "PossessionAgreementExceptionReason" =
                    CASE WHEN relationship."RelationshipNumber" = 'DEMO-LM-ACTIVE-017'
                         THEN 'Demo imported possession is intentionally retained without a governing Agreement.'
                         ELSE relationship."PossessionAgreementExceptionReason"
                    END,
                "PossessionAgreementExceptionAuthorizedByUserId" =
                    CASE WHEN relationship."RelationshipNumber" = 'DEMO-LM-ACTIVE-017'
                         THEN {{actorUserId}}
                         ELSE relationship."PossessionAgreementExceptionAuthorizedByUserId"
                    END
            WHERE relationship."PortfolioId" = {{portfolioId}}
              AND starts_with(relationship."RelationshipNumber", 'DEMO-LM-')
              AND relationship."PossessionGivenAtUtc" IS NOT NULL
              AND relationship."PossessionReturnedAtUtc" IS NULL
              AND relationship."EndingDisposition" = 'Undecided'
              AND EXISTS (
                  SELECT 1
                  FROM "TenantAccounts" AS account
                  WHERE account."PortfolioId" = {{portfolioId}}
                    AND account."LeaseManagementId" = relationship."Id"
                    AND account."ClosedAtUtc" IS NULL)
            RETURNING 1 AS "Value"
            """,
            [new AtomicSqlMutationTarget(
                nameof(RentalCommandDbContext.LeaseManagements),
                AtomicSqlMutationOperation.Update)],
            ct);
    }

    public static async Task<int> ResolveBuiltInLeaseSourceAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        int portfolioId,
        int actorUserId,
        DateTime createdAtUtc,
        CancellationToken ct)
    {
        if (!db.Database.IsNpgsql())
        {
            var existing = await db.Set<LegalDocumentSourceVersion>()
                .SingleOrDefaultAsync(source =>
                    source.PortfolioId == portfolioId
                    && source.SourceKind == LegalDocumentSourceKind.BuiltInRenderer
                    && source.RendererKey == BuiltInLeaseAgreementSource.RendererKey
                    && source.RendererVersion == BuiltInLeaseAgreementSource.RendererVersion,
                    ct);
            if (existing is not null)
            {
                return existing.Id;
            }

            var source = new LegalDocumentSourceVersion
            {
                PublicId = Guid.NewGuid(),
                PortfolioId = portfolioId,
                SourceKind = LegalDocumentSourceKind.BuiltInRenderer,
                BusinessKey = BuiltInLeaseAgreementSource.BusinessKey,
                RendererKey = BuiltInLeaseAgreementSource.RendererKey,
                RendererVersion = BuiltInLeaseAgreementSource.RendererVersion,
                SnapshotPayload = BuiltInLeaseAgreementSource.SnapshotPayload,
                CreatedAtUtc = createdAtUtc,
                CreatedByUserId = actorUserId,
            };
            db.Add(source);
            await context.FlushBusinessAsync(ct);
            return source.Id;
        }

        var rows = await db.ExecuteAtomicSqlMutationAsync<BuiltInSourceRow>(context, $$"""
            WITH inserted AS (
                INSERT INTO "LegalDocumentSourceVersions"
                    ("PublicId", "PortfolioId", "SourceKind", "BusinessKey",
                     "RendererKey", "RendererVersion", "SnapshotPayload", "CreatedAtUtc", "CreatedByUserId")
                VALUES
                    (gen_random_uuid(), {{portfolioId}}, 'BuiltInRenderer',
                     {{BuiltInLeaseAgreementSource.BusinessKey}},
                     {{BuiltInLeaseAgreementSource.RendererKey}},
                     {{BuiltInLeaseAgreementSource.RendererVersion}},
                     {{BuiltInLeaseAgreementSource.SnapshotPayload}}::jsonb,
                     {{createdAtUtc}}, {{actorUserId}})
                ON CONFLICT ("PortfolioId", "RendererKey", "RendererVersion")
                    WHERE "SourceKind" = 'BuiltInRenderer'
                DO NOTHING
                RETURNING "Id"
            )
            SELECT "Id" AS "Value" FROM inserted
            UNION ALL
            SELECT source."Id" AS "Value"
            FROM "LegalDocumentSourceVersions" AS source
            WHERE source."PortfolioId" = {{portfolioId}}
              AND source."SourceKind" = 'BuiltInRenderer'
              AND source."RendererKey" = {{BuiltInLeaseAgreementSource.RendererKey}}
              AND source."RendererVersion" = {{BuiltInLeaseAgreementSource.RendererVersion}}
              AND NOT EXISTS (SELECT 1 FROM inserted)
            LIMIT 1
            """,
            [new AtomicSqlMutationTarget(
                nameof(RentalCommandDbContext.LegalDocumentSourceVersions),
                AtomicSqlMutationOperation.Insert)],
            ct);
        return rows.SingleOrDefault()?.Value ?? 0;
    }

    private sealed class BuiltInSourceRow
    {
        public int Value { get; set; }
    }
}
