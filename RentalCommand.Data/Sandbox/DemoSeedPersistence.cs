using RentalCommand.Core.Atomic;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Leasing;

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
            SET "UpdatedAtUtc" = {{updatedAtUtc}},
                "RowVersion" = {{rowVersion}},
                "PossessionAgreementExceptionReason" =
                    'Demo imported possession is intentionally retained without a governing Agreement.',
                "PossessionAgreementExceptionAuthorizedByUserId" = {{actorUserId}}
            WHERE relationship."PortfolioId" = {{portfolioId}}
              AND relationship."RelationshipNumber" = 'DEMO-LM-ACTIVE-017'
              AND relationship."PossessionGivenAtUtc" IS NOT NULL
              AND relationship."PossessionReturnedAtUtc" IS NULL
              AND relationship."EndingDisposition" = 'Undecided'
              AND EXISTS (
                  SELECT 1
                  FROM "TenantAccounts" AS account
                  WHERE account."PortfolioId" = {{portfolioId}}
                    AND account."LeaseManagementId" = relationship."Id"
                    AND account."ClosedAtUtc" IS NULL)
              AND (relationship."PossessionAgreementExceptionReason" IS DISTINCT FROM
                       'Demo imported possession is intentionally retained without a governing Agreement.'
                   OR relationship."PossessionAgreementExceptionAuthorizedByUserId" IS DISTINCT FROM
                       {{actorUserId}})
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
        var source = await AtomicLeaseMutationPersistence.ResolveBuiltInDocumentSourceVersionAsync(
            db,
            context,
            portfolioId,
            actorUserId,
            createdAtUtc,
            ct);
        return source.DocumentSourceVersionId;
    }
}
