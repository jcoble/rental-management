using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Atomic;

/// <summary>
/// PostgreSQL admission fence for atomic blob finalizers. The one statement matches every security
/// and integrity field before returning tracked rows, then locks them in UUID order. A cleanup
/// worker's FOR UPDATE SKIP LOCKED claim therefore cannot observe or mutate an in-flight set.
/// </summary>
internal sealed class AtomicPendingFileUploadPersistence : IAtomicPendingFileUploadPersistence
{
    private readonly RentalCommandDbContext _db;
    private readonly AtomicAuditScope _auditScope;

    public AtomicPendingFileUploadPersistence(
        RentalCommandDbContext db,
        AtomicAuditScope auditScope)
    {
        _db = db;
        _auditScope = auditScope;
    }

    public async Task<IReadOnlyList<PendingFileUpload>> LockPreparedSetAsync(
        int portfolioId,
        int actorScopeId,
        IReadOnlyList<AtomicPendingFileUploadExpectation> expectations,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(portfolioId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(actorScopeId);
        if (expectations.Count == 0)
            throw new ArgumentException("At least one pending upload expectation is required.", nameof(expectations));

        var payload = JsonSerializer.Serialize(expectations.Select(expectation => new
        {
            id = expectation.Id,
            purpose = expectation.Purpose,
            operation_key_hash = expectation.OperationKeyHash,
            request_fingerprint = expectation.RequestFingerprint,
            storage_path = expectation.StoragePath,
            file_name = expectation.FileName,
            content_type = expectation.ContentType,
            size_bytes = expectation.SizeBytes,
        }));
        var parameters = new NpgsqlParameter[]
        {
            new("expectations", NpgsqlDbType.Jsonb) { Value = payload },
            new("portfolioId", NpgsqlDbType.Integer) { Value = portfolioId },
            new("actorScopeId", NpgsqlDbType.Integer) { Value = actorScopeId },
            new("prepared", NpgsqlDbType.Integer) { Value = (int)PendingFileUploadState.Prepared },
        };

        using var lockLease = _auditScope.BeginInternalRawDml(
            "PendingFileUploads",
            AtomicRawDmlOperation.Update);
        return await _db.PendingFileUploads
            .FromSqlRaw(Sql, parameters)
            .AsTracking()
            .ToListAsync(ct);
    }

    private const string Sql = """
        /* scan-upload-admission-lock */
        WITH expected AS MATERIALIZED (
            SELECT *
            FROM jsonb_to_recordset(@expectations::jsonb) AS input(
            id uuid,
            purpose text,
            operation_key_hash text,
            request_fingerprint text,
            storage_path text,
            file_name text,
            content_type text,
            size_bytes bigint)
        )
        SELECT upload.*
        FROM expected
        INNER JOIN "PendingFileUploads" AS upload
            ON upload."Id" = expected.id
           AND upload."PortfolioId" = @portfolioId
           AND upload."ActorScopeId" = @actorScopeId
           AND upload."Purpose" = expected.purpose
           AND upload."OperationKeyHash" = expected.operation_key_hash
           AND upload."RequestFingerprint" = expected.request_fingerprint
           AND upload."StoragePath" = expected.storage_path
           AND upload."FileName" = expected.file_name
           AND lower(upload."ContentType") = lower(expected.content_type)
           AND upload."SizeBytes" = expected.size_bytes
           AND upload."State" = @prepared
           AND upload."CleanupClaimToken" IS NULL
        WHERE (SELECT count(*) FROM expected) = (
            SELECT count(*)
            FROM expected AS candidate
            INNER JOIN "PendingFileUploads" AS candidate_upload
                ON candidate_upload."Id" = candidate.id
               AND candidate_upload."PortfolioId" = @portfolioId
               AND candidate_upload."ActorScopeId" = @actorScopeId
               AND candidate_upload."Purpose" = candidate.purpose
               AND candidate_upload."OperationKeyHash" = candidate.operation_key_hash
               AND candidate_upload."RequestFingerprint" = candidate.request_fingerprint
               AND candidate_upload."StoragePath" = candidate.storage_path
               AND candidate_upload."FileName" = candidate.file_name
               AND lower(candidate_upload."ContentType") = lower(candidate.content_type)
               AND candidate_upload."SizeBytes" = candidate.size_bytes
               AND candidate_upload."State" = @prepared
               AND candidate_upload."CleanupClaimToken" IS NULL
        )
        ORDER BY upload."Id"
        FOR UPDATE OF upload
        """;
}
