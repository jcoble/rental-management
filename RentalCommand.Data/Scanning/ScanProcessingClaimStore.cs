using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Data.Atomic;

namespace RentalCommand.Data.Scanning;

public sealed record ScanProcessingClaim(
    int Id,
    int PortfolioId,
    string FilePath,
    int? SourceStoredFileId,
    string TargetEntityType,
    string ClaimOwner,
    Guid ClaimToken);

public sealed record ScanProcessingResult(
    string ExtractedFields,
    string? ModelId,
    int? TokensUsed,
    decimal? CostUsd,
    string TargetEntityType,
    DateTime ReviewedAtUtc);

public sealed record ScanProcessingTerminalCommand(
    int DraftId,
    int PortfolioId,
    string ClaimOwner,
    Guid ClaimToken,
    string Status,
    DateTime ReviewedAtUtc,
    ScanProcessingResult? ReviewingResult = null,
    string? FailureReason = null) : IAtomicCommandData;

public sealed record ScanProcessingTerminalResult(bool Applied, int DraftId, string Status);

public interface IScanProcessingClaimStore
{
    Task<IReadOnlyList<ScanProcessingClaim>> ClaimAsync(
        string claimOwner, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default);

}

/// <summary>
/// PostgreSQL boundary for paid scan extraction. Candidate selection, expiry reclaim, ordering,
/// paging, row locking, and lease assignment are one statement. LLM/blob work runs only after it
/// returns. Result and failure writes require the current owner, token, and a live DB-clock lease.
/// </summary>
public sealed class ScanProcessingClaimStore : IScanProcessingClaimStore
{
    private const string ClaimSql = """
        WITH clock AS MATERIALIZED (
            SELECT clock_timestamp() AS now_utc
        ), candidates AS (
            SELECT draft."Id"
            FROM "ScanDrafts" AS draft
            INNER JOIN "Portfolios" AS portfolio ON portfolio."Id" = draft."PortfolioId"
            CROSS JOIN clock
            WHERE portfolio."DeletedAt" IS NULL
              AND (draft."Status" = 'Pending'
                   OR (draft."Status" = 'Processing' AND draft."ProcessingClaimExpiresAtUtc" <= clock.now_utc))
              AND (draft."ProcessingClaimToken" IS NULL OR draft."ProcessingClaimExpiresAtUtc" <= clock.now_utc)
            ORDER BY draft."CreatedAt", draft."Id"
            FOR UPDATE OF draft SKIP LOCKED
            LIMIT @batchSize
        )
        UPDATE "ScanDrafts" AS draft
        SET "Status" = 'Processing',
            "ProcessingClaimOwner" = @claimOwner,
            "ProcessingClaimToken" = gen_random_uuid(),
            "ProcessingClaimExpiresAtUtc" = clock.now_utc + @leaseDuration,
            "ProcessingAttemptCount" = draft."ProcessingAttemptCount" + 1,
            "ProcessingLastAttemptAtUtc" = clock.now_utc,
            "FailureReason" = NULL
        FROM candidates, clock
        WHERE draft."Id" = candidates."Id"
        RETURNING draft."Id", draft."PortfolioId", draft."FilePath",
                  draft."SourceStoredFileId", draft."TargetEntityType",
                  draft."ProcessingClaimOwner", draft."ProcessingClaimToken";
        """;

    private readonly RentalCommandDbContext _db;

    public ScanProcessingClaimStore(RentalCommandDbContext db) => _db = db;

    public async Task<IReadOnlyList<ScanProcessingClaim>> ClaimAsync(
        string claimOwner, TimeSpan leaseDuration, int batchSize,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimOwner);
        if (claimOwner.Length > 200) throw new ArgumentOutOfRangeException(nameof(claimOwner));
        if (leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        if (batchSize is <= 0 or > 100) throw new ArgumentOutOfRangeException(nameof(batchSize));

        var connection = _db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone)
        {
            // Open through EF so configured connection interceptors establish the Engine's RLS
            // actor/session context before the raw one-statement claim executes.
            await _db.Database.OpenConnectionAsync(ct);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = ClaimSql;
            command.Parameters.Add(new NpgsqlParameter("claimOwner", NpgsqlDbType.Text) { Value = claimOwner });
            command.Parameters.Add(new NpgsqlParameter("leaseDuration", NpgsqlDbType.Interval) { Value = leaseDuration });
            command.Parameters.Add(new NpgsqlParameter("batchSize", NpgsqlDbType.Integer) { Value = batchSize });

            var claims = new List<ScanProcessingClaim>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                claims.Add(new ScanProcessingClaim(
                    reader.GetInt32(0),
                    reader.GetInt32(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetGuid(6)));
            }

            return claims;
        }
        finally
        {
            if (closeWhenDone) await _db.Database.CloseConnectionAsync();
        }
    }

}

public static class ScanProcessingTerminalWrite
{
    public const string ResultContract = "scan-processing.terminal.v1";
    private const string OperationName = "scan-processing.terminal";

    public static string StepKey(ScanProcessingTerminalCommand command) =>
        $"scan-terminal:{command.DraftId}:{command.ClaimToken:N}:{command.Status.ToLowerInvariant()}";

    public static TransactionalWrite<ScanProcessingTerminalCommand, ScanProcessingTerminalResult> Write(
        RentalCommandDbContext db,
        ScanProcessingTerminalCommand command)
    {
        Validate(command);
        return new TransactionalWrite<ScanProcessingTerminalCommand, ScanProcessingTerminalResult>(
            OperationName,

            command,
            ResultContract,
            WriteLockPlan.None,
            (request, context, ct) => ExecuteAsync(db, request, context, ct),
            static (_, _, _) => Task.CompletedTask);
    }

    private static async Task<ScanProcessingTerminalResult> ExecuteAsync(
        RentalCommandDbContext db,
        ScanProcessingTerminalCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        await context.AcquireLockAsync("ScanDraft", command.DraftId, ct);
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        var reviewing = command.Status == "Reviewing";
        var rows = await db.ExecuteAtomicSqlMutationAsync<int>(
            context,
            reviewing ? ReviewingSql(command) : FailedSql(command),
            [new AtomicSqlMutationTarget("ScanDrafts", AtomicSqlMutationOperation.Update)],
            ct);
        var applied = rows.Count == 1;
        if (!applied)
            return new ScanProcessingTerminalResult(false, command.DraftId, command.Status);

        context.StageSemanticEvent(new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(ScanDraft),
            command.DraftId,
            AuditLogOperation.Updated,
            ActorLabel: "ScanProcessingWorker",
            NewValues: JsonSerializer.Serialize(new { command.Status, command.FailureReason }),
            ChangeReason: reviewing ? "Scan extraction completed for review." : "Scan extraction failed."), now);
        context.StageOutbox(ScanDraftMutationResults.DataUpdate(
            command.PortfolioId, command.DraftId, "update", StepKey(command), now));
        return new ScanProcessingTerminalResult(true, command.DraftId, command.Status);
    }

    private static void Validate(ScanProcessingTerminalCommand command)
    {
        if (command.DraftId <= 0 || command.PortfolioId <= 0 || command.ClaimToken == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(command));
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ClaimOwner);
        if (command.Status == "Reviewing" && command.ReviewingResult is null
            || command.Status == "Failed" && command.ReviewingResult is not null
            || command.Status is not ("Reviewing" or "Failed"))
            throw new ArgumentException("A valid scan terminal transition is required.", nameof(command));
    }

    private static string? LimitError(string? value) => value is null || value.Length <= 500 ? value : value[..500];

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static FormattableString ReviewingSql(ScanProcessingTerminalCommand command)
    {
        var result = command.ReviewingResult!;
        return $"""
        WITH clock AS MATERIALIZED (SELECT clock_timestamp() AS now_utc)
        UPDATE "ScanDrafts" AS draft
        SET "ExtractedFields" = CAST({result.ExtractedFields} AS jsonb), "FailureReason" = NULL,
            "ModelId" = {result.ModelId}, "TokensUsed" = {result.TokensUsed}, "CostUsd" = {result.CostUsd},
            "TargetEntityType" = {result.TargetEntityType}, "Status" = 'Reviewing',
            "ReviewedAt" = {AsUtc(command.ReviewedAtUtc)},
            "ProcessingClaimOwner" = NULL, "ProcessingClaimToken" = NULL, "ProcessingClaimExpiresAtUtc" = NULL
        FROM clock
        WHERE draft."Id" = {command.DraftId} AND draft."Status" = 'Processing'
          AND draft."ProcessingClaimOwner" = {command.ClaimOwner}
          AND draft."ProcessingClaimToken" = {command.ClaimToken}
          AND draft."ProcessingClaimExpiresAtUtc" > clock.now_utc
        RETURNING 1 AS "Value";
        """;
    }

    private static FormattableString FailedSql(ScanProcessingTerminalCommand command) => $"""
        WITH clock AS MATERIALIZED (SELECT clock_timestamp() AS now_utc)
        UPDATE "ScanDrafts" AS draft
        SET "Status" = 'Failed', "FailureReason" = {LimitError(command.FailureReason)},
            "ReviewedAt" = {AsUtc(command.ReviewedAtUtc)},
            "ProcessingClaimOwner" = NULL, "ProcessingClaimToken" = NULL, "ProcessingClaimExpiresAtUtc" = NULL
        FROM clock
        WHERE draft."Id" = {command.DraftId} AND draft."Status" = 'Processing'
          AND draft."ProcessingClaimOwner" = {command.ClaimOwner}
          AND draft."ProcessingClaimToken" = {command.ClaimToken}
          AND draft."ProcessingClaimExpiresAtUtc" > clock.now_utc
        RETURNING 1 AS "Value";
        """;
}
